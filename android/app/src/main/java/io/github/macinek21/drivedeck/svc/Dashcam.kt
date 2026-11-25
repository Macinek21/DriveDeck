package io.github.macinek21.drivedeck.svc

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Paint
import android.graphics.PorterDuff
import android.media.MediaFormat
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.util.Range
import android.view.Surface
import androidx.camera.core.Camera
import androidx.camera.core.CameraEffect
import androidx.camera.core.CameraSelector
import androidx.camera.core.Preview
import androidx.camera.core.UseCaseGroup
import androidx.camera.effects.Frame
import androidx.camera.effects.OverlayEffect
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.video.FallbackStrategy
import androidx.camera.video.FileOutputOptions
import androidx.camera.video.QualitySelector
import androidx.camera.video.Recorder
import androidx.camera.video.VideoCapture
import androidx.camera.video.VideoRecordEvent
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleOwner
import io.github.macinek21.drivedeck.core.CamSettings
import io.github.macinek21.drivedeck.core.Fix
import io.github.macinek21.drivedeck.core.Gpx
import io.github.macinek21.drivedeck.core.LoopPolicy
import io.github.macinek21.drivedeck.core.Names
import io.github.macinek21.drivedeck.core.Segment
import io.github.macinek21.drivedeck.core.Srt
import io.github.macinek21.drivedeck.core.Stamp
import io.github.macinek21.drivedeck.core.Video
import io.github.macinek21.drivedeck.core.VideoMode
import java.io.File
import java.io.IOException
import java.util.Locale
import kotlin.math.roundToInt

/**
 * Loop dashcam. Records fixed-length MP4 clips to Documents/DriveDeck/Loop, deletes the oldest to respect the limits, burns in a
 * stamp, writes .srt/.gpx files and moves clips around an incident to Events. All methods run on the main thread; file work on [Store.io].
 */
class Dashcam(private val ctx: Context, private val owner: LifecycleOwner, private val fix: () -> Fix?, private val speed: () -> Double?) {
    private class Pending(val time: Long, var until: Long, val reason: String, var peak: Double, val fix: Fix?, val speed: Double?)

    private class Clip(val seg: Segment, val file: File) {
        var t0 = 0L
        var stopper: (() -> Unit)? = null
        var stopAt = 0L
        var rotation = 0
        var turnedAt = 0L
        // What this clip was recorded with; the settings may have changed by the time it is finished.
        var cam: FastCam? = null
        var scale = 1.0
        var hevc = false
        var wantedBits = 0
        var fast = false
        val startedAt = SystemClock.elapsedRealtime()
        var stopping = false
        var stalled = false
        var srt: Srt? = null
        var cueFrom = 0L
        var cue = ""
        val track = ArrayList<Fix>()
        var progressAt = SystemClock.elapsedRealtime()
        var bytes = 0L
    }

    private val s get() = Hub.settings.cam
    private val main = Hub.main
    private val exec = ContextCompat.getMainExecutor(ctx)
    private var provider: ProcessCameraProvider? = null
    private var camera: Camera? = null
    private var video: VideoCapture<Recorder>? = null
    private var preview: Preview? = null
    private var effect: OverlayEffect? = null
    private val effectThread by lazy { HandlerThread("stamp").also { it.start() } }
    private var opening = false
    private var closed = false
    private var clip: Clip? = null
    private val segments = ArrayList<Segment>() // guarded by itself
    private val pending = ArrayList<Pending>()
    private var wanted = false
    private var failures = 0
    private var stampFailures = 0
    private var quickRetries = 0
    private var effectBroken = false
    private var restartPosted = false
    private var lastVideoEnd = 0L
    private var modeFailures = 0
    private var safeMode = false
    private var fast: FastCam? = null
    private var fastReady = false
    private var fastClosing = false
    private var fastChecked = false
    private var fastFailures = 0
    private var fastBytesPerMs = 0.0
    /** Set while the camera is being re-configured on purpose: the clip that ends now is not a failure and no new one starts yet. */
    private var switching = false

    /** Takes the camera's preview all the time and shows it when a screen is attached. */
    val gl = PreviewGl()
    private var lastPlace = ""
    @Volatile private var stampText = ""

    var rotation = Surface.ROTATION_0
    var paused = false
        private set
    var onChange: (() -> Unit)? = null
    val recordingSince get() = clip?.takeIf { it.t0 > 0 }?.t0
    val isWanted get() = wanted
    val loopBytes get() = synchronized(segments) { segments.sumOf { it.size } }

    private fun granted(p: String) = ContextCompat.checkSelfPermission(ctx, p) == PackageManager.PERMISSION_GRANTED
    private val ticker = object : Runnable { override fun run() { tick(); main.postDelayed(this, 200) } }

    init {
        rescan()
        main.post(ticker)
    }

    // ---------------- Camera ----------------

    private fun open() {
        if (closed || opening || !s.enabled) return
        if (!granted(Manifest.permission.CAMERA)) { Hub.post("dashcam", Severity.WARNING, "Dashcam", "Camera permission is missing", 180_000); return }
        opening = true
        Store.io.execute {
            CameraModes.learn(ctx)
            main.post {
                val f = ProcessCameraProvider.getInstance(ctx)
                f.addListener({
                    opening = false
                    if (closed) return@addListener
                    try { provider = f.get(); bind() } catch (e: Exception) { fail("Camera could not start", e) }
                }, exec)
            }
        }
    }

    private fun rebind() {
        if (closed || provider == null) return
        try { bind() } catch (e: Exception) { fail("Camera could not start", e) }
    }

    private fun bind() {
        val p = provider ?: return
        release()
        if (fastClosing) { main.postDelayed(::rebind, 150); return }
        switching = false
        val modes = CameraModes.query(p)
        val wish = if (safeMode) Video.pick(modes.filter { !it.special }, "1080p", 30) else Video.pick(modes, s.resolution, s.fps)
        val m = wish ?: VideoMode("1080p", 1920, 1080, 30, 15)
        if (!safeMode && wish != null && (m.res != s.resolution || m.fps != s.fps))
            Hub.post("mode", Severity.WARNING, "Video mode changed", "Not available: ${s.resolution} ${s.fps} fps. Recording ${m.res} ${m.fps} fps", 600_000)
        if (m.special) { bindFast(m); return }
        val q = CameraModes.quality(m.res)
        val rb = CameraModes.recorder(q).setTargetVideoEncodingBitRate(s.bitrateMbps * 1_000_000)
        if (wish == null) rb.setQualitySelector(QualitySelector.from(q, FallbackStrategy.lowerQualityOrHigherThan(q)))
        if (s.codec == "hevc" && CameraModes.hevc && !safeMode) rb.setVideoMimeType(MediaFormat.MIMETYPE_VIDEO_HEVC)
        val v = VideoCapture.Builder(rb.build()).setTargetRotation(rotation).setTargetFrameRate(Range(m.minFps, m.fps)).build()
        val pv = Preview.Builder().build().also { it.surfaceProvider = previewTarget }
        val group = UseCaseGroup.Builder().addUseCase(pv).addUseCase(v)
        if (s.stamp && !effectBroken) effect = OverlayEffect(CameraEffect.VIDEO_CAPTURE, 0, Handler(effectThread.looper)) { t -> main.post { AppLog.e("stamp effect", t); effectFailed() } }
            .also { it.setOnDrawListener(::drawStamp); group.addEffect(it) }
        camera = p.bindToLifecycle(owner, CameraSelector.DEFAULT_BACK_CAMERA, group.build())
        video = v
        preview = pv
        AppLog.i("Camera bound: ${m.width}x${m.height} ${m.minFps}-${m.fps} fps ${s.bitrateMbps} Mbit/s ${s.codec} audio=${s.audio} stamp=${effect != null}")
        if (wanted) startClip()
    }

    private val previewTarget = Preview.SurfaceProvider { r ->
        gl.create(r.resolution.width, r.resolution.height) { st ->
            val surface = Surface(st)
            main.post { r.provideSurface(surface, exec) { surface.release(); gl.release(st) } }
        }
    }

    private fun fastHevc() = s.codec == "hevc" && CameraModes.hevc

    private fun fastConfig(m: VideoMode): FastCam.Config {
        val wanted = s.bitrateMbps * 1_000_000
        // Clips move on by size, so the size is chosen to give about the clip length that is set.
        val perMs = if (fastBytesPerMs > 0) fastBytesPerMs else wanted / 8000.0
        val bytes = (perMs * s.segmentSeconds * 1000).toLong().coerceIn(1_000_000L, MAX_FILE)
        return FastCam.Config((wanted * CameraModes.scale(m, fastHevc())).toInt().coerceAtLeast(500_000), fastHevc(), s.audio && granted(Manifest.permission.RECORD_AUDIO), bytes)
    }


    private fun bindFast(m: VideoMode) {
        lateinit var fc: FastCam
        fc = FastCam(ctx, m, gl) { why, refused -> if (fast === fc) fastFailed(m, why, refused) }
        fast = fc
        fastReady = false
        fastChecked = false
        fastBytesPerMs = 0.0
        // The camera library needs a moment to let go of the camera.
        main.postDelayed({
            if (fast === fc && !closed) fc.open(fastConfig(m)) {
                if (fast === fc) {
                    fastReady = true
                    AppLog.i("Camera open: ${m.width}x${m.height} ${m.fps} fps ${if (m.legacy) "older interface" else "high speed"} ${s.bitrateMbps} Mbit/s ${s.codec} audio=${s.audio}")
                    if (wanted) startClip()
                }
            }
        }, 800)
    }

    /** The phone did not deliver a special mode: record normally instead, and if it was a refusal do not offer it again. */
    private fun fastFailed(m: VideoMode, why: String, refused: Boolean) {
        AppLog.i("Mode ${m.id} failed: $why refused=$refused")
        // A mode that has already worked on this phone is tried again, and never struck off the list for good.
        val proven = CameraModes.realFps(m) > 0
        if (refused && proven && ++fastFailures >= 3) {
            safeMode = true
            Hub.post("mode", Severity.WARNING, "${m.res} ${m.fps} fps does not work now", "Recording 1080p 30 fps for the rest of this drive ($why).")
            switching = true
            stopClip()
            main.postDelayed({ restartNow(wanted) }, 300)
        } else if (refused && !proven) {
            CameraModes.refuse(m)
            Hub.post("mode", Severity.WARNING, "${m.res} ${m.fps} fps is not possible", "This phone does not give it to other apps ($why). Recording the closest mode that works.")
            switching = true
            stopClip()
            main.postDelayed({ restartNow(wanted) }, 300)
        } else fail(why, null, false)
    }

    private fun release() {
        provider?.unbindAll()
        fast?.let { fastClosing = true; it.close { fastClosing = false } }
        fast = null
        fastReady = false
        effect?.close()
        effect = null
        video = null
        preview = null
        camera = null
    }

    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; isFakeBoldText = true }
    private val back = Paint().apply { color = 0x8C000000.toInt() }

    /** Draws the stamp upright at the bottom left of the final picture, whatever the sensor orientation. Runs on the effect thread. */
    private fun drawStamp(frame: Frame): Boolean {
        val c = frame.overlayCanvas
        c.drawColor(Color.TRANSPARENT, PorterDuff.Mode.CLEAR)
        val text = stampText
        if (text.isEmpty()) return true
        val r = frame.cropRect
        c.save()
        c.translate(r.left.toFloat(), r.top.toFloat())
        val rot = frame.rotationDegrees
        when (rot) {
            90 -> { c.translate(0f, r.height().toFloat()); c.rotate(-90f) }
            180 -> { c.translate(r.width().toFloat(), r.height().toFloat()); c.rotate(180f) }
            270 -> { c.translate(r.width().toFloat(), 0f); c.rotate(90f) }
        }
        val w = (if (rot % 180 == 0) r.width() else r.height()).toFloat()
        val h = (if (rot % 180 == 0) r.height() else r.width()).toFloat()
        paint.textSize = minOf(w, h) / 28f
        val tw = paint.measureText(text)
        if (tw > w * 0.96f) paint.textSize *= w * 0.96f / tw
        val pad = paint.textSize * 0.4f
        val y = h - pad * 2.5f
        c.drawRect(pad, y - paint.textSize, pad * 3 + minOf(tw, w * 0.96f), y + pad, back)
        c.drawText(text, pad * 2, y, paint)
        c.restore()
        return true
    }

    private fun effectFailed() {
        if (effectBroken) return
        effectBroken = true
        Hub.post("stamp", Severity.WARNING, "Speed stamp off", "The burned-in stamp does not work on this phone. Speed is still saved in the .srt file.")
        restart(500)
    }

    /** [stampSuspect]: the stamp effect could be the cause; after two such failures in a row it is switched off. */
    private fun fail(what: String, e: Throwable?, stampSuspect: Boolean = true) {
        failures++
        if (stampSuspect && effect != null) stampFailures++
        if (stampSuspect) modeFailures++
        if (e != null) AppLog.e(what, e) else AppLog.i(what)
        Hub.post("dashcam", Severity.WARNING, "Dashcam", what + (e?.message?.let { ": " + it.take(120) } ?: ""), 120_000)
        if (modeFailures >= 4 && !safeMode) {
            safeMode = true
            Hub.post("mode", Severity.WARNING, "Video mode not working", "Recording 1080p 30 fps instead. Choose another mode in Settings.")
        }
        if (stampFailures >= 2) effectFailed() else restart(minOf(30_000L, 2000L * failures))
    }

    /** Re-creates the camera after a failure, once, with a delay. */
    private fun restart(delay: Long) {
        if (restartPosted || closed) return
        restartPosted = true
        main.postDelayed({
            restartPosted = false
            if (closed || !wanted || paused) { switching = false; return@postDelayed }
            if (clip != null) { switching = true; stopClip(); restart(1000) } else if (provider == null) open() else try { bind() } catch (e: Exception) { fail("Camera could not start", e) }
        }, delay)
    }

    /** Opens the camera for the preview without recording. */
    fun openPreview() { if (video == null && fast == null && provider == null) open() }

    fun start() {
        wanted = s.enabled
        if (!wanted || paused || clip != null || switching) return
        if (video == null && fast == null) { if (provider == null) open() else rebind() } else startClip()
    }

    fun stop() {
        wanted = false
        stopClip()
    }

    fun pauseForHeat(hot: Boolean) {
        if (paused == hot) return
        paused = hot
        if (hot) {
            Hub.post("hot", Severity.ALARM, "Phone is too hot", "Recording is paused until it cools down")
            stopClip()
        } else {
            Hub.post("hot", Severity.INFO, "Phone cooled down", "Recording resumes")
            if (wanted) start()
        }
        onChange?.invoke()
    }

    /** Applies changed settings; the camera restarts only if something about it changed. */
    fun apply(previous: CamSettings) {
        val n = s
        val cameraChanged = previous.resolution != n.resolution || previous.fps != n.fps || previous.bitrateMbps != n.bitrateMbps ||
            previous.codec != n.codec || previous.audio != n.audio || previous.stamp != n.stamp || previous.enabled != n.enabled
        if (previous.storage != n.storage) { rescan(); if (!cameraChanged) rollover() }
        if (!cameraChanged) return
        safeMode = false
        modeFailures = 0
        effectBroken = false
        failures = 0
        fastFailures = 0
        stampFailures = 0
        if (!n.enabled) { stop(); if (clip == null) release() } else { val w = wanted; restartNow(w) }
    }

    private fun restartNow(record: Boolean) {
        wanted = record
        switching = true
        if (clip != null) { stopClip(); main.postDelayed({ if (!closed) restartNow(record) }, 300); return }
        if (provider == null) open() else try { bind() } catch (e: Exception) { fail("Camera could not start", e) }
    }

    /** Finishes the current clip and releases the camera, then calls [done]. */
    fun close(done: () -> Unit) {
        wanted = false
        closed = true
        stopClip()
        val end = SystemClock.elapsedRealtime() + 10_000
        fun poll() {
            if (clip != null && SystemClock.elapsedRealtime() < end) { main.postDelayed(::poll, 50); return }
            main.removeCallbacks(ticker)
            release()
            main.postDelayed({ gl.close() }, 1500)
            effectThread.quitSafely()
            Store.io.execute { processEvents(true); main.post(done) }
        }
        poll()
    }

    // ---------------- Clips ----------------

    @SuppressLint("MissingPermission")
    private fun startClip() {
        val v = video
        val fc = fast.takeIf { fastReady }
        if (v == null && fc == null) return
        if (clip != null && !clip!!.stopping) return
        if (!wanted || paused || closed || switching) return
        val place = choosePlace() ?: return
        val now = System.currentTimeMillis()
        val f = newFile(place, now)
        val c = Clip(Segment(f.nameWithoutExtension, now, inProgress = true, root = place.root.path), f)
        try {
            clip = c
            if (fc != null) {
                c.fast = true
                c.stopper = { fc.stop() }
                synchronized(segments) { segments += c.seg }
                c.rotation = rotation
                c.cam = fc
                c.hevc = fastHevc()
                c.wantedBits = s.bitrateMbps * 1_000_000
                c.scale = CameraModes.scale(fc.mode, c.hevc)
                var cur = c // the clip being written; recording may move on to further ones
                fc.record(f, fastConfig(fc.mode), (CameraModes.sensorOrientation - rotation * 90 + 360) % 360,
                    { choosePlace()?.let { newFile(it, System.currentTimeMillis()) } },
                    { begun(cur, System.currentTimeMillis()) }, { next -> cur = movedOn(cur, next) }) { ok ->
                    val until = if (cur.stopAt > 0) cur.stopAt else System.currentTimeMillis()
                    if (cur.stopAt > 0) AppLog.i("Closing the clip took ${System.currentTimeMillis() - cur.stopAt} ms")
                    val result = if (!ok) VideoRecordEvent.Finalize.ERROR_NO_VALID_DATA else if (cur.stopping) VideoRecordEvent.Finalize.ERROR_NONE else VideoRecordEvent.Finalize.ERROR_FILE_SIZE_LIMIT_REACHED
                    finished(cur, result, null, if (cur.t0 > 0) until - cur.t0 else 0, cur.file.length())
                }
                return
            }
            v!!.targetRotation = rotation
            var p = v.output.prepareRecording(ctx, FileOutputOptions.Builder(f).apply { setFileSizeLimit(MAX_FILE) }.build())
            if (s.audio && granted(Manifest.permission.RECORD_AUDIO)) p = p.withAudioEnabled()
            val r = p.start(exec) { onEvent(c, it) }
            c.stopper = { r.stop() }
            synchronized(segments) { segments += c.seg }
        } catch (e: Exception) {
            if (clip === c) clip = null
            f.delete()
            fail("Recording could not start", e)
        }
    }

    private fun newFile(place: Place, now: Long): File {
        val base = Names.of(now)
        var f = File(place.loop, "$base.mp4")
        var n = 2
        while (f.exists() && n < 100) f = File(place.loop, "$base (${n++}).mp4")
        return f
    }

    /** Recording has moved on from clip [before] to [prepared] without stopping; returns the new clip. */
    private fun movedOn(before: Clip, prepared: File): Clip {
        val now = System.currentTimeMillis()
        // The file was named when it was prepared, some seconds ago; the name should say when it really begins.
        val place = Store.places().firstOrNull { it.loop == prepared.parentFile }
        val exact = place?.let { newFile(it, now) }
        val f = if (exact != null && prepared.renameTo(exact)) exact else prepared
        val c = Clip(Segment(f.nameWithoutExtension, now, inProgress = true, root = f.parentFile!!.parentFile!!.path), f)
        c.fast = true
        c.stopper = before.stopper
        c.stopAt = before.stopAt
        c.stopping = before.stopping
        c.rotation = before.rotation
        c.cam = before.cam
        c.scale = before.scale
        c.hevc = before.hevc
        c.wantedBits = before.wantedBits
        if (clip === before) clip = c
        synchronized(segments) { segments += c.seg }
        AppLog.i("Moved on to the next clip while recording")
        finished(before, VideoRecordEvent.Finalize.ERROR_NONE, null, now - before.t0, before.file.length())
        lastVideoEnd = 0
        begun(c, now)
        return c
    }

    /** The chosen place; if the memory card is missing or not writable, the phone, so that driving is still recorded. */
    private fun choosePlace(): Place? {
        val w = Store.wanted()
        if (w != null && w.ensure()) { note(w); return w }
        if (s.storage == "card") Hub.post("card", Severity.WARNING, "Memory card not available", "Recording to the phone instead", 300_000)
        if (Store.device.ensure()) { note(Store.device); return Store.device }
        Hub.post("dashcam", Severity.WARNING, "Dashcam", "Cannot write to Documents/DriveDeck. Allow storage access in Android settings.", 120_000)
        return null
    }

    private fun note(p: Place) {
        if (lastPlace != p.root.path) { lastPlace = p.root.path; AppLog.i("Recording to ${p.id}: ${p.root.path}") }
    }

    /** Ends the current clip so the next one starts with the current storage choice. */
    fun rollover() { if (clip != null) stopClip() }

    private fun stopClip() {
        val c = clip ?: return
        if (c.stopping) return
        c.stopping = true
        c.stopAt = System.currentTimeMillis()
        c.stopper?.invoke()
    }

    private fun onEvent(c: Clip, e: VideoRecordEvent) {
        val dur = e.recordingStats.recordedDurationNanos / 1_000_000
        if (e is VideoRecordEvent.Status) {
            if (e.recordingStats.numBytesRecorded != c.bytes) { c.bytes = e.recordingStats.numBytesRecorded; c.progressAt = SystemClock.elapsedRealtime() }
            if (c.t0 == 0L && dur > 0) { // first real video: the subtitle clock starts here
                begun(c, System.currentTimeMillis() - dur)
            }
        }
        if (e is VideoRecordEvent.Finalize) finished(c, e.error, e.cause, dur, e.recordingStats.numBytesRecorded)
    }

    private fun begun(c: Clip, t0: Long) {
        c.t0 = t0
        c.srt = Srt(t0)
        if (lastVideoEnd > 0) AppLog.i("Gap since previous clip: ${t0 - lastVideoEnd} ms")
        failures = 0
        stampFailures = 0
        modeFailures = 0
        quickRetries = 0
        onChange?.invoke()
    }

    private fun finished(c: Clip, error: Int, cause: Throwable?, dur: Long, bytes: Long) {
        val now = System.currentTimeMillis()
        if (clip === c) clip = null
        if (c.cue.isNotEmpty() && s.srt) c.srt?.add(c.cueFrom, now, c.cue)
        val srt = if (s.srt) c.srt?.toString().orEmpty() else ""
        val gpx = if (s.gpx && c.track.isNotEmpty()) Gpx.build(c.seg.name, c.track) else ""
        AppLog.i("Clip ${c.seg.name}: ${dur / 1000.0} s, ${bytes / 1024} KB, result $error")
        if (c.t0 > 0) lastVideoEnd = c.t0 + dur
        val bad = error == VideoRecordEvent.Finalize.ERROR_NO_VALID_DATA || dur <= 0
        c.seg.end = now
        Store.io.execute {
            if (bad) {
                c.file.delete()
                synchronized(segments) { segments -= c.seg }
            } else {
                try {
                    if (srt.isNotEmpty()) side(c.file, "srt").writeText(srt)
                    if (gpx.isNotEmpty()) side(c.file, "gpx").writeText(gpx)
                } catch (x: IOException) { AppLog.e("sidecar", x) }
                c.seg.size = EXT.sumOf { side(c.file, it).length() }
                c.seg.inProgress = false
                val fc = c.cam
                if (c.fast && fc != null && dur > 4000) {
                    fastFailures = 0
                    val actual = c.file.length() * 8000.0 / dur
                    if (dur > 8000) {
                        if (fast === fc) fastBytesPerMs = c.file.length().toDouble() / dur
                        CameraModes.learnScale(fc.mode, c.hevc, c.scale, c.wantedBits, actual)
                    }
                    if (!fastChecked && fast === fc) { // is it really the promised rate?
                        fastChecked = true
                        val (fps, w, h) = CameraModes.measure(c.file)
                        AppLog.i("Mode ${fc.mode.id}: file has %.1f fps, %dx%d, %.1f Mbit/s".format(Locale.ROOT, fps, w, h, actual / 1e6))
                        if (fps >= fc.mode.fps * 0.5) CameraModes.saveRealFps(fc.mode, fps.roundToInt())
                        if (fps > 0 && fps < fc.mode.fps * 0.5) main.post { if (fast === fc) fastFailed(fc.mode, "it delivered only %.0f fps".format(fps), true) }
                        else if (fps > 0 && fps < fc.mode.fps * 0.9) Hub.post("mode", Severity.INFO, "${fc.mode.res} ${fc.mode.fps} fps", "The phone manages about %.0f fps in this mode".format(fps))
                    }
                }
                Store.announce(*EXT.map { side(c.file, it) }.filter { it.exists() }.toTypedArray())
            }
            processEvents(!wanted || paused)
        }
        onChange?.invoke()
        // Stopped by us: the next clip starts at once. Stopped by the system (camera taken by another app, disk full...): retry with a delay.
        if (!wanted || paused || closed || clip != null || switching) return
        val fc = fast
        when {
            c.fast && bad && fc != null && (c.stalled || !c.stopping || SystemClock.elapsedRealtime() - c.startedAt > 4000) -> fastFailed(fc.mode, "no video arrived", true)
            c.stalled -> fail("The camera stopped delivering video", cause)
            c.stopping || error == VideoRecordEvent.Finalize.ERROR_FILE_SIZE_LIMIT_REACHED -> if (bad) main.postDelayed(::startClip, 500) else startClip() // ended by us
            error == VideoRecordEvent.Finalize.ERROR_SOURCE_INACTIVE -> // re-configured while starting, or another app took the camera
                if (quickRetries++ < 3) main.postDelayed(::startClip, 400) else fail("The camera is in use by another app", null, false)
            !File(c.seg.root).exists() -> startClip() // the card was pulled out: carry on at once on the phone
            else -> fail("Recording was interrupted (code $error)", cause)
        }
    }

    private fun side(f: File, ext: String) = File(f.parentFile, f.nameWithoutExtension + "." + ext)

    private var roomCheckedFor: Clip? = null

    private fun tick() {
        val now = System.currentTimeMillis()
        val fx = fix()
        val sp = speed()
        stampText = Stamp.text(now, fx, sp, s.stampDate, s.stampSpeed && s.gps, s.stampCoords && s.gps, s.label)
        val c = clip ?: return
        if (c.stopping) return
        if (c.t0 > 0) {
            if (now - c.cueFrom >= 1000) {
                if (c.cue.isNotEmpty() && s.srt) c.srt?.add(c.cueFrom, now, c.cue)
                c.cueFrom = now
                c.cue = Stamp.text(now, fx, sp, true, s.gps, s.gps, "")
                if (s.gpx && fx != null && c.track.lastOrNull() !== fx) c.track += fx
            }
            if (roomCheckedFor !== c) { roomCheckedFor = c; Store.io.execute(::makeRoom) }
            // Seamless clips move on by themselves; the time limit is only their safety net. After an incident every clip is
            // closed at once, at the price of a short gap: an unfinished file would not survive the phone losing power.
            val seamless = c.fast && fast?.seamless == true
            val limit = if (seamless) s.segmentSeconds * 1600L + 10_000 else s.segmentSeconds * 1000L
            // Turned for good, not just bumped or tilted for a moment.
            if (c.rotation == rotation) c.turnedAt = 0 else if (c.turnedAt == 0L) c.turnedAt = now
            val turned = seamless && c.turnedAt > 0 && now - c.turnedAt > 5000
            if (now - c.t0 >= limit || turned || synchronized(pending) { pending.any { now >= it.until } }) stopClip()
        }
        if (c.fast) c.file.length().let { if (it != c.bytes) { c.bytes = it; c.progressAt = SystemClock.elapsedRealtime() } }
        if (SystemClock.elapsedRealtime() - c.progressAt > 20_000) { // camera delivers nothing
            c.stalled = true
            stopClip()
        }
    }

    // ---------------- Storage (Store.io thread) ----------------

    /** Re-reads the clips of all places, e.g. after a card change or after clips were deleted by hand. */
    fun rescan() = Store.io.execute {
        val found = ArrayList<Segment>()
        for (p in Store.places()) {
            for ((name, files) in p.loop.listFiles().orEmpty().groupBy { it.nameWithoutExtension }) {
                val start = Names.parse(name) ?: continue // not ours: never touched
                if (synchronized(segments) { segments.any { it.inProgress && it.name == name } }) continue
                val mp4 = files.firstOrNull { it.extension == "mp4" }
                if (mp4 == null || mp4.length() == 0L) {
                    // Leftovers only; a file this fresh may belong to a clip that is just starting.
                    if (System.currentTimeMillis() - start > 60_000) files.filter { it.extension in EXT }.forEach { it.delete() }
                    continue
                }
                found += Segment(name, start, maxOf(start, mp4.lastModified()), files.sumOf { it.length() }, root = p.root.path)
            }
        }
        synchronized(segments) {
            segments.removeAll { !it.inProgress }
            segments.addAll(0, found.filter { f -> segments.none { it.name == f.name } }.sortedBy { it.start })
        }
    }

    private fun loopFile(seg: Segment, ext: String) = File(File(seg.root, "Loop"), "${seg.name}.$ext")

    /**
     * Frees space for the current and the next clip. The size limit counts the clips of phone and card together, the free-space
     * reserve only concerns where we are recording. Footage an incident may still need is kept.
     */
    private fun makeRoom() {
        val st = s
        val incoming = LoopPolicy.estimate(st.bitrateMbps * 1_000_000, st.audio, st.segmentSeconds) * 2
        val keepFrom = (synchronized(pending) { pending.minOfOrNull { it.time } } ?: System.currentTimeMillis()) - st.preSeconds * 1000L
        fun candidates(root: String?) = synchronized(segments) { segments.toList() }.filter { root == null || it.root == root }
            .map { Segment(it.name, it.start, it.end, it.size, it.inProgress || it.end > keepFrom, it.root) }
        fun remove(l: List<Segment>) = l.forEach { d ->
            EXT.forEach { loopFile(d, it).delete() }
            Store.announce(*EXT.map { loopFile(d, it) }.toTypedArray())
            synchronized(segments) { segments.removeAll { it.name == d.name && !it.inProgress } }
        }
        if (!st.unlimited) remove(LoopPolicy.toDelete(candidates(null), (st.capGb * GB).toLong(), Long.MAX_VALUE / 4, 0, incoming))
        val here = synchronized(segments) { segments.lastOrNull { it.inProgress }?.root } ?: return
        val place = Store.places().firstOrNull { it.root.path == here } ?: return
        // The large reserve is for Android itself; a memory card only needs a little air.
        val phoneReserve = (st.reserveGb * GB).toLong()
        val reserve = if (place.id == "card") minOf(phoneReserve, maxOf(50_000_000L, place.total() / 50)) else phoneReserve
        remove(LoopPolicy.toDelete(candidates(here), null, place.free(), reserve, incoming))
        if (place.free() < reserve) main.post {
            Hub.post("full", Severity.ALARM, "${place.label} is full", "Recording stopped. Delete saved incidents or other files.", 120_000)
            stop()
            onChange?.invoke()
        }
    }

    // ---------------- Incidents ----------------

    fun trigger(reason: String, peak: Double) {
        val now = System.currentTimeMillis()
        val until = now + s.postSeconds * 1000L
        synchronized(pending) {
            val open = pending.firstOrNull { now <= it.until }
            if (open != null) { open.until = until; open.peak = maxOf(open.peak, peak) } else pending += Pending(now, until, reason, peak, fix(), speed())
        }
        if (clip == null) Store.io.execute { processEvents(true) }
    }

    private fun processEvents(force: Boolean) {
        val now = System.currentTimeMillis()
        val due = synchronized(pending) { pending.filter { force || now >= it.until }.also { pending.removeAll(it) } }
        for (p in due) {
            val segs = LoopPolicy.forEvent(synchronized(segments) { segments.filter { !it.inProgress } }, p.time, s.preSeconds * 1000L, p.until)
            // Clips stay on the volume they were recorded to: moving within it is instant and cannot fail half-way.
            val name = Names.of(p.time) + if (p.reason == "manual") "_manual" else "_impact"
            val roots = segs.map { it.root }.distinct().ifEmpty { listOf((Store.wanted() ?: Store.device).root.path) }
            var moved = 0
            val dirs = ArrayList<File>()
            for (root in roots) {
                var dir = File(File(root, "Events"), name)
                var n = 2
                while (dir.exists()) dir = File(File(root, "Events"), "$name (${n++})")
                if (!dir.mkdirs()) { AppLog.i("Cannot create incident folder"); continue }
                dirs += dir
                for (seg in segs.filter { it.root == root }) {
                    var ok = false
                    for (x in EXT) {
                        val f = loopFile(seg, x)
                        if (f.exists() && f.renameTo(File(dir, f.name)) && x == "mp4") ok = true
                    }
                    if (ok) { moved++; synchronized(segments) { segments -= seg } } else AppLog.i("Could not move ${seg.name}")
                }
            }
            if (dirs.isEmpty()) { Hub.post("event", Severity.ALARM, "Incident not saved", "Cannot create the incident folder"); continue }
            val info = listOf("DriveDeck incident", "Type: " + if (p.reason == "manual") "saved manually" else "impact detected",
                "Time: " + Stamp.text(p.time, null, null, true, false, false, ""),
                "Peak acceleration: %.2f g".format(Locale.ROOT, p.peak), "Speed: " + (p.speed?.let { "${it.roundToInt()} km/h" } ?: "unknown"),
                "Position: " + (p.fix?.let { "%.6f, %.6f".format(Locale.ROOT, it.lat, it.lon) } ?: "unknown"),
                "Video files: " + if (moved == 0) "none" else segs.joinToString { it.name + ".mp4" })
            for (dir in dirs) try { File(dir, "incident.txt").writeText(info.joinToString("\r\n") + "\r\n") } catch (e: IOException) { AppLog.e("incident.txt", e) }
            Store.announce(*(dirs.flatMap { it.listFiles().orEmpty().toList() } + segs.flatMap { g -> EXT.map { loopFile(g, it) } }).toTypedArray())
            val where = dirs.joinToString(" and ") { if (it.path.startsWith(Store.device.root.path)) "phone" else "memory card" }
            Hub.post("event:$name", if (moved > 0) Severity.INFO else Severity.WARNING, if (moved > 0) "Incident saved" else "Incident has no video", "$moved video(s) on $where, folder Events/$name")
        }
    }

    companion object {
        /** Below the 4 GB limit of FAT32 memory cards. */
        private const val MAX_FILE = 3_900_000_000L
        private const val GB = 1024.0 * 1024 * 1024
        val EXT = listOf("mp4", "srt", "gpx")
    }
}
