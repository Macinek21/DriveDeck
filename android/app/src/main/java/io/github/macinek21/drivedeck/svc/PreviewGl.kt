package io.github.macinek21.drivedeck.svc

import android.graphics.SurfaceTexture
import android.opengl.EGL14
import android.opengl.EGLConfig
import android.opengl.EGLExt
import android.opengl.EGLSurface
import android.opengl.GLES11Ext
import android.opengl.GLES20
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Receives the camera's preview for as long as the camera runs, and shows it on screen whenever a view is attached.
 * Because the camera never notices whether the screen is on, opening or leaving the app cannot interrupt a recording.
 */
class PreviewGl {
    private val thread = HandlerThread("preview").also { it.start() }
    private val h = Handler(thread.looper)
    private val display = EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY)
    private var config: EGLConfig? = null
    private var context = EGL14.EGL_NO_CONTEXT
    private var idle: EGLSurface = EGL14.EGL_NO_SURFACE
    private var window: EGLSurface = EGL14.EGL_NO_SURFACE
    private var winW = 0
    private var winH = 0
    private var program = 0
    private val textures = HashMap<SurfaceTexture, Int>()
    private var current: SurfaceTexture? = null
    private var camW = 1
    private var camH = 1
    private val matrix = FloatArray(16)

    /** Display rotation in quarter turns. */
    @Volatile var rotation = 0

    init {
        h.post { try { setup() } catch (e: Exception) { AppLog.e("preview setup", e) } }
    }

    private fun setup() {
        val v = IntArray(2)
        check(EGL14.eglInitialize(display, v, 0, v, 1)) { "eglInitialize" }
        val c = arrayOfNulls<EGLConfig>(1)
        val n = IntArray(1)
        EGL14.eglChooseConfig(display, intArrayOf(EGL14.EGL_RED_SIZE, 8, EGL14.EGL_GREEN_SIZE, 8, EGL14.EGL_BLUE_SIZE, 8, EGL14.EGL_RENDERABLE_TYPE, EGL14.EGL_OPENGL_ES2_BIT,
            EGL14.EGL_SURFACE_TYPE, EGL14.EGL_WINDOW_BIT or EGL14.EGL_PBUFFER_BIT, EGL14.EGL_NONE), 0, c, 0, 1, n, 0)
        config = c[0]
        context = EGL14.eglCreateContext(display, config, EGL14.EGL_NO_CONTEXT, intArrayOf(EGL14.EGL_CONTEXT_CLIENT_VERSION, 2, EGL14.EGL_NONE), 0)
        idle = EGL14.eglCreatePbufferSurface(display, config, intArrayOf(EGL14.EGL_WIDTH, 1, EGL14.EGL_HEIGHT, 1, EGL14.EGL_NONE), 0)
        check(EGL14.eglMakeCurrent(display, idle, idle, context)) { "eglMakeCurrent" }
        fun shader(type: Int, src: String) = GLES20.glCreateShader(type).also { GLES20.glShaderSource(it, src); GLES20.glCompileShader(it) }
        program = GLES20.glCreateProgram()
        GLES20.glAttachShader(program, shader(GLES20.GL_VERTEX_SHADER, "attribute vec4 p; attribute vec4 t; uniform mat4 m; varying vec2 v; void main(){ gl_Position = p; v = (m * t).xy; }"))
        GLES20.glAttachShader(program, shader(GLES20.GL_FRAGMENT_SHADER, "#extension GL_OES_EGL_image_external : require\nprecision mediump float; varying vec2 v; uniform samplerExternalOES s; void main(){ gl_FragColor = texture2D(s, v); }"))
        GLES20.glLinkProgram(program)
    }

    /** A new target for the camera; [ready] runs on the preview thread. The caller releases it with [release]. */
    fun create(w: Int, height: Int, ready: (SurfaceTexture) -> Unit) {
        h.post {
            val ids = IntArray(1)
            GLES20.glGenTextures(1, ids, 0)
            GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, ids[0])
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_LINEAR)
            GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR)
            val st = SurfaceTexture(ids[0])
            textures[st] = ids[0]
            st.setDefaultBufferSize(w, height)
            camW = w
            camH = height
            st.setOnFrameAvailableListener({ draw(it) }, h)
            current = st
            ready(st)
        }
    }

    fun release(st: SurfaceTexture) {
        h.post {
            if (current === st) current = null
            st.setOnFrameAvailableListener(null)
            st.release()
            textures.remove(st)?.let { GLES20.glDeleteTextures(1, intArrayOf(it), 0) }
        }
    }

    /** Where to show the picture; null = nowhere, the camera keeps running. */
    fun show(s: Surface?, w: Int, height: Int) {
        h.post {
            EGL14.eglMakeCurrent(display, idle, idle, context)
            if (window != EGL14.EGL_NO_SURFACE) EGL14.eglDestroySurface(display, window)
            window = EGL14.EGL_NO_SURFACE
            winW = w
            winH = height
            if (s != null && s.isValid) window = try { EGL14.eglCreateWindowSurface(display, config, s, intArrayOf(EGL14.EGL_NONE), 0) } catch (e: Exception) { AppLog.i("preview window: ${e.message}"); EGL14.EGL_NO_SURFACE }
        }
    }

    private fun floats(vararg f: Float) = ByteBuffer.allocateDirect(f.size * 4).order(ByteOrder.nativeOrder()).asFloatBuffer().put(f).also { it.position(0) }

    private fun draw(st: SurfaceTexture) {
        if (st !in textures) return
        try {
            st.updateTexImage()
            if (st !== current) return
            if (window == EGL14.EGL_NO_SURFACE || winW == 0 || winH == 0) return
            if (!EGL14.eglMakeCurrent(display, window, window, context)) return
            st.getTransformMatrix(matrix)
            GLES20.glViewport(0, 0, winW, winH)
            GLES20.glUseProgram(program)
            GLES20.glUniformMatrix4fv(GLES20.glGetUniformLocation(program, "m"), 1, false, matrix, 0)
            // The camera picture stands upright in the phone's natural (portrait) orientation; turn it with the display and fill the tile.
            val quarter = rotation and 3
            val turned = quarter % 2 == 1
            val imgW = (if (turned) maxOf(camW, camH) else minOf(camW, camH)).toFloat()
            val imgH = (if (turned) minOf(camW, camH) else maxOf(camW, camH)).toFloat()
            val scale = maxOf(winW / imgW, winH / imgH)
            val x = imgW * scale / winW
            val y = imgH * scale / winH
            val corners = arrayOf(-1f to -1f, 1f to -1f, 1f to 1f, -1f to 1f)     // counter-clockwise from bottom left
            val uv = arrayOf(0f to 0f, 1f to 0f, 1f to 1f, 0f to 1f)
            val pos = FloatArray(8)
            val tc = FloatArray(8)
            for ((i, k) in listOf(0, 1, 3, 2).withIndex()) {
                pos[i * 2] = corners[k].first * x
                pos[i * 2 + 1] = corners[k].second * y
                val u = uv[(k + 4 - quarter) % 4]
                tc[i * 2] = u.first
                tc[i * 2 + 1] = u.second
            }
            val p = GLES20.glGetAttribLocation(program, "p")
            val t = GLES20.glGetAttribLocation(program, "t")
            GLES20.glEnableVertexAttribArray(p)
            GLES20.glEnableVertexAttribArray(t)
            GLES20.glVertexAttribPointer(p, 2, GLES20.GL_FLOAT, false, 0, floats(*pos))
            GLES20.glVertexAttribPointer(t, 2, GLES20.GL_FLOAT, false, 0, floats(*tc))
            GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4)
            EGLExt.eglPresentationTimeANDROID(display, window, st.timestamp)
            if (!EGL14.eglSwapBuffers(display, window)) { // the view is gone
                EGL14.eglMakeCurrent(display, idle, idle, context)
                EGL14.eglDestroySurface(display, window)
                window = EGL14.EGL_NO_SURFACE
            }
        } catch (e: Exception) { AppLog.i("preview: ${e.message}") }
    }

    fun close() {
        h.post {
            EGL14.eglMakeCurrent(display, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_CONTEXT)
            if (window != EGL14.EGL_NO_SURFACE) EGL14.eglDestroySurface(display, window)
            if (idle != EGL14.EGL_NO_SURFACE) EGL14.eglDestroySurface(display, idle)
            if (context != EGL14.EGL_NO_CONTEXT) EGL14.eglDestroyContext(display, context)
            thread.quitSafely()
        }
    }
}
