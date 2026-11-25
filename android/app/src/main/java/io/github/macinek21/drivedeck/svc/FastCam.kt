@file:Suppress("DEPRECATION")

package io.github.macinek21.drivedeck.svc

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.SurfaceTexture
import android.hardware.Camera
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraConstrainedHighSpeedCaptureSession
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.media.MediaCodec
import android.media.MediaRecorder
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Range
import android.view.Surface
import io.github.macinek21.drivedeck.core.VideoMode
import java.io.File

/**
 * Records the modes the camera library cannot: official high-speed modes through Camera2, and 60 fps / 4K through the
 * manufacturer's older camera interface. No burned-in stamp. All camera work runs on its own thread; callbacks come on the main thread.
 */
class FastCam(private val ctx: Context, val mode: VideoMode, private val preview: PreviewGl, private val failed: (String, Boolean) -> Unit) {
    /** [bitrate] is what is asked of the encoder; [maxBytes]: size at which recording moves on to the next file. */
    class Config(val bitrate: Int, val hevc: Boolean, val audio: Boolean, val maxBytes: Long)

    private val thread = HandlerThread("fastcam").also { it.start() }
    private val h = Handler(thread.looper)
    private var st: SurfaceTexture? = null
    private var old: Camera? = null
    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var input: Surface? = null
    private var recorder: MediaRecorder? = null
    private var closed = false
    private var ready = false
    private var next: File? = null

    /** Moving on to the next file while recording; switched off if this phone cannot do it. */
    @Volatile var seamless = true
        private set

    /** [refused]: the phone does not allow this mode, as opposed to a passing problem. */
    private fun fail(where: String, e: Throwable?, refused: Boolean = true) {
        if (e != null) AppLog.e("fast camera $where", e) else AppLog.i("fast camera $where")
        if (!closed) Hub.main.post { failed(where, refused) }
    }

    fun open(cfg: Config, done: () -> Unit) {
        val w = if (mode.highSpeed) mode.width else minOf(mode.width, 1920)
        val height = if (mode.highSpeed) mode.height else minOf(mode.height, 1080)
        preview.create(w, height) { t ->
            h.post {
                if (closed) { preview.release(t); return@post }
                st = t
                try { if (mode.legacy) openLegacy(t, w, height, done) else openHighSpeed(t, cfg, done) } catch (e: Throwable) { fail("not accepted by the camera", e) }
            }
        }
    }

    // ---------------- Older interface (Samsung 60 fps and 4K) ----------------

    private fun openLegacy(t: SurfaceTexture, w: Int, height: Int, done: () -> Unit) {
        val c = try { Camera.open(0) } catch (e: RuntimeException) { fail("the camera is busy", e, false); return }
        old = c
        c.setErrorCallback { e, _ -> fail("camera error $e", null, false) }
        val info = Camera.CameraInfo()
        Camera.getCameraInfo(0, info)
        val p = c.parameters
        p.setRecordingHint(true)
        p.setPreviewSize(w, height)
        p.set("video-size", "${mode.width}x${mode.height}")
        if (mode.fps > 30 && p.get("fast-fps-mode") != null) p.set("fast-fps-mode", when (mode.fps) { 240 -> 3; 120 -> 2; else -> 1 })
        if (mode.fps > 30) p.setPreviewFpsRange(mode.fps * 1000, mode.fps * 1000)
        else p.supportedPreviewFpsRange.filter { it[1] == mode.fps * 1000 }.maxByOrNull { it[0] }?.let { p.setPreviewFpsRange(it[0], it[1]) }
        if (Camera.Parameters.FOCUS_MODE_CONTINUOUS_VIDEO in p.supportedFocusModes) p.focusMode = Camera.Parameters.FOCUS_MODE_CONTINUOUS_VIDEO
        c.parameters = p
        c.setDisplayOrientation(info.orientation)
        c.setPreviewTexture(t)
        c.startPreview()
        ready = true
        Hub.main.post(done)
    }

    // ---------------- Official high speed ----------------

    @SuppressLint("MissingPermission")
    private fun openHighSpeed(t: SurfaceTexture, cfg: Config, done: () -> Unit) {
        val m = ctx.getSystemService(CameraManager::class.java)
        val id = CameraModes.backId(m) ?: throw IllegalStateException("no back camera")
        // The recorder's input must have its size before the camera session is made; one input lasts for all clips.
        val target = MediaCodec.createPersistentInputSurface()
        input = target
        val warm = File(ctx.cacheDir, "warm.mp4")
        prepare(cfg, warm, 0).release()
        warm.delete()
        m.openCamera(id, object : CameraDevice.StateCallback() {
            override fun onOpened(d: CameraDevice) {
                device = d
                if (closed) { d.close(); return }
                try {
                    val show = Surface(t)
                    d.createConstrainedHighSpeedCaptureSession(listOf(show, target), object : CameraCaptureSession.StateCallback() {
                        override fun onConfigured(s: CameraCaptureSession) {
                            if (closed) return
                            try {
                                session = s
                                val r = d.createCaptureRequest(CameraDevice.TEMPLATE_RECORD)
                                r.addTarget(show)
                                r.addTarget(target)
                                r.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, Range(mode.fps, mode.fps))
                                r.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
                                s.setRepeatingBurst((s as CameraConstrainedHighSpeedCaptureSession).createHighSpeedRequestList(r.build()), null, h)
                                ready = true
                                Hub.main.post(done)
                            } catch (e: Throwable) { fail("high speed could not start", e) }
                        }

                        override fun onConfigureFailed(s: CameraCaptureSession) = fail("high speed refused", null)
                    }, h)
                } catch (e: Throwable) { fail("high speed refused", e) }
            }

            override fun onDisconnected(d: CameraDevice) { d.close(); fail("the camera is in use by another app", null, false) }
            override fun onError(d: CameraDevice, e: Int) { d.close(); fail("camera error $e", null, false) }
        }, h)
    }

    // ---------------- Recording ----------------

    private fun prepare(cfg: Config, f: File, orientation: Int): MediaRecorder {
        val r = if (Build.VERSION.SDK_INT >= 31) MediaRecorder(ctx) else MediaRecorder()
        try {
            val c = old
            if (c != null) { c.unlock(); r.setCamera(c) }
            if (cfg.audio) r.setAudioSource(MediaRecorder.AudioSource.CAMCORDER)
            r.setVideoSource(if (c != null) MediaRecorder.VideoSource.CAMERA else MediaRecorder.VideoSource.SURFACE)
            r.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
            r.setVideoEncoder(if (cfg.hevc) MediaRecorder.VideoEncoder.HEVC else MediaRecorder.VideoEncoder.H264)
            r.setVideoSize(mode.width, mode.height)
            // Through the older interface Android refuses rates above the listed 30; the frames still keep their real times.
            if (c == null || mode.fps <= 30) r.setVideoFrameRate(mode.fps)
            r.setVideoEncodingBitRate(cfg.bitrate)
            if (cfg.audio) {
                r.setAudioEncoder(MediaRecorder.AudioEncoder.AAC)
                r.setAudioSamplingRate(48000)
                r.setAudioEncodingBitRate(128_000)
            }
            input?.let { r.setInputSurface(it) }
            r.setOrientationHint(orientation)
            r.setOutputFile(f.path)
            if (seamless) r.setMaxFileSize(cfg.maxBytes)
            r.prepare()
            return r
        } catch (e: Throwable) {
            r.release()
            runCatching { old?.reconnect() }
            throw e
        }
    }

    /**
     * [started] when recording runs. Near the size limit [nextFile] names the next file and [switched] reports that recording
     * has moved on to it, without a gap. [ended] (good) after [stop] or a failure.
     */
    fun record(f: File, cfg: Config, orientation: Int, nextFile: () -> File?, started: () -> Unit, switched: (File) -> Unit, ended: (Boolean) -> Unit) {
        h.post {
            if (closed || !ready) { Hub.main.post { ended(false) }; return@post }
            try {
                val r = prepare(cfg, f, orientation)
                recorder = r
                r.setOnErrorListener { _, what, extra -> fail("recorder error $what/$extra", null, false) }
                r.setOnInfoListener { _, what, _ -> onInfo(r, what, nextFile, switched) }
                end = ended
                val t = SystemClock.elapsedRealtime()
                r.start()
                AppLog.i("Recorder started in ${SystemClock.elapsedRealtime() - t} ms")
                Hub.main.post(started)
            } catch (e: Throwable) {
                recorder?.release()
                recorder = null
                end = null
                runCatching { old?.reconnect() }
                AppLog.e("fast recording", e)
                Hub.main.post { ended(false) }
            }
        }
    }

    private fun onInfo(r: MediaRecorder, what: Int, nextFile: () -> File?, switched: (File) -> Unit) {
        if (recorder !== r) return
        when (what) {
            MediaRecorder.MEDIA_RECORDER_INFO_MAX_FILESIZE_APPROACHING -> if (next == null) {
                val n = nextFile() ?: return
                try { r.setNextOutputFile(n); next = n } catch (e: Exception) { seamless = false; n.delete(); AppLog.e("next file", e) }
            }
            MediaRecorder.MEDIA_RECORDER_INFO_NEXT_OUTPUT_FILE_STARTED -> next?.let { n -> next = null; Hub.main.post { switched(n) } }
            MediaRecorder.MEDIA_RECORDER_INFO_MAX_FILESIZE_REACHED -> { // it stopped instead of moving on
                AppLog.i("recorder stopped at the size limit")
                seamless = false
                stop()
            }
        }
    }

    private var end: ((Boolean) -> Unit)? = null

    fun stop() = h.post {
        val r = recorder ?: return@post
        recorder = null
        val ok = try { r.stop(); true } catch (_: RuntimeException) { false } // thrown when not a single frame arrived
        r.release()
        runCatching { old?.reconnect() }
        next?.delete() // prepared, but never used
        next = null
        val e = end
        end = null
        if (e != null) Hub.main.post { e(ok) }
    }

    fun close(done: () -> Unit = {}) {
        stop()
        h.post {
            closed = true
            runCatching { session?.close() }
            runCatching { device?.close() }
            runCatching { old?.setErrorCallback(null); old?.stopPreview() }
            runCatching { old?.release() }
            old = null
            device = null
            session = null
            input?.release()
            input = null
            st?.let { preview.release(it) }
            st = null
            Hub.main.post(done)
            thread.quitSafely()
        }
    }
}
