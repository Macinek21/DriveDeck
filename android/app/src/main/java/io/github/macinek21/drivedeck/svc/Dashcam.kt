package io.github.macinek21.drivedeck.svc

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Paint
import android.graphics.PorterDuff
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
import androidx.camera.video.Quality
import androidx.camera.video.QualitySelector
import androidx.camera.video.Recorder
import androidx.camera.video.Recording
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
        var recording: Recording? = null
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
    private var bound: CamSettings? = null
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
    @Volatile private var stampText = ""

    var surface: Preview.SurfaceProvider? = null
        set(v) { field = v; preview?.surfaceProvider = v }
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
        Store.io.execute { scan() }
        main.post(ticker)
    }

    // ---------------- Camera ----------------

    private fun open() {
        if (closed || opening || !s.enabled) return
        if (!granted(Manifest.permission.CAMERA)) { Hub.post("dashcam", Severity.WARNING, "Dashcam", "Camera permission is missing", 180_000); return }
        opening = true
        val f = ProcessCameraProvider.getInstance(ctx)
        f.addListener({
            opening = false
            if (closed) return@addListener
            try { provider = f.get(); bind() } catch (e: Exception) { fail("Camera could not start", e) }
        }, exec)
    }

    private fun bind() {
        val p = provider ?: return
        release()
        val q = if (s.resolution == "1080p") Quality.FHD else Quality.HD
        val rec = Recorder.Builder().setQualitySelector(QualitySelector.from(q, FallbackStrategy.lowerQualityOrHigherThan(q)))
            .setTargetVideoEncodingBitRate(s.bitrateMbps * 1_000_000).build()
        val v = VideoCapture.Builder(rec).setTargetFrameRate(Range(s.fps, s.fps)).setTargetRotation(rotation).build()
        val pv = Preview.Builder().build().also { it.surfaceProvider = surface }
        val group = UseCaseGroup.Builder().addUseCase(pv).addUseCase(v)
        if (s.stamp && !effectBroken) {
            effect = OverlayEffect(CameraEffect.VIDEO_CAPTURE, 0, Handler(effectThread.looper)) { t -> main.post { AppLog.e("stamp effect", t); effectFailed() } }
                .also { it.setOnDrawListener(::drawStamp); group.addEffect(it) }
        }
        camera = p.bindToLifecycle(owner, CameraSelector.DEFAULT_BACK_CAMERA, group.build())
        video = v
        preview = pv
        bound = Hub.settings.copy().cam
        AppLog.i("Camera bound: ${s.resolution} ${s.fps} fps ${s.bitrateMbps} Mbit/s audio=${s.audio} stamp=${effect != null}")
        if (wanted) startClip()
    }

    private fun release() {
        provider?.unbindAll()
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
        if (e != null) AppLog.e(what, e) else AppLog.i(what)
        Hub.post("dashcam", Severity.WARNING, "Dashcam", what + (e?.message?.let { ": " + it.take(120) } ?: ""), 120_000)
        if (stampFailures >= 2) effectFailed() else restart(minOf(30_000L, 2000L * failures))
    }

    /** Re-creates the camera after a failure, once, with a delay. */
    private fun restart(delay: Long) {
        if (restartPosted || closed) return
        restartPosted = true
        main.postDelayed({
            restartPosted = false
            if (closed || !wanted || paused) return@postDelayed
            if (clip != null) { stopClip(); restart(1000) } else if (provider == null) open() else try { bind() } catch (e: Exception) { fail("Camera could not start", e) }
        }, delay)
    }

    /** Opens the camera for the preview without recording. */
    fun openPreview() { if (video == null && provider == null) open() }

    fun start() {
        wanted = s.enabled
        if (!wanted || paused || clip != null) return
        if (video == null) open() else startClip()
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
            previous.audio != n.audio || previous.stamp != n.stamp || previous.enabled != n.enabled
        if (!cameraChanged) return
        effectBroken = false
        failures = 0
        stampFailures = 0
        if (!n.enabled) { stop(); if (clip == null) release() } else { val w = wanted; restartNow(w) }
    }

    private fun restartNow(record: Boolean) {
        wanted = record
        if (clip != null) { stopClip(); main.postDelayed({ if (!closed) restartNow(record) }, 300); return }
        if (provider == null) open() else try { bind() } catch (e: Exception) { fail("Camera could not start", e) }
    }

    /** Finishes the current clip and releases the camera, then calls [done]. */
    fun close(done: () -> Unit) {
        wanted = false
        closed = true
        stopClip()
        val end = SystemClock.elapsedRealtime() + 4000
        fun poll() {
            if (clip != null && SystemClock.elapsedRealtime() < end) { main.postDelayed(::poll, 50); return }
            main.removeCallbacks(ticker)
            release()
            effectThread.quitSafely()
            Store.io.execute { processEvents(true); main.post(done) }
        }
        poll()
    }

    // ---------------- Clips ----------------

    @SuppressLint("MissingPermission")
    private fun startClip() {
        val v = video ?: return
        if (clip != null && !clip!!.stopping) return
        if (!wanted || paused || closed) return
        if (!Store.ensure()) { Hub.post("dashcam", Severity.WARNING, "Dashcam", "Cannot write to Documents/DriveDeck. Allow storage access in Android settings.", 120_000); return }
        val now = System.currentTimeMillis()
        val base = Names.of(now)
        var f = File(Store.loop, "$base.mp4")
        var n = 2
        while (f.exists() && n < 100) f = File(Store.loop, "$base (${n++}).mp4")
        v.targetRotation = rotation
        val c = Clip(Segment(f.nameWithoutExtension, now, inProgress = true), f)
        try {
            var p = v.output.prepareRecording(ctx, FileOutputOptions.Builder(f).build())
            if (s.audio && granted(Manifest.permission.RECORD_AUDIO)) p = p.withAudioEnabled()
            clip = c
            c.recording = p.start(exec) { onEvent(c, it) }
            synchronized(segments) { segments += c.seg }
        } catch (e: Exception) {
            if (clip === c) clip = null
            f.delete()
            fail("Recording could not start", e)
        }
    }

    private fun stopClip() {
        val c = clip ?: return
        if (c.stopping) return
        c.stopping = true
        c.recording?.stop()
    }

    private fun onEvent(c: Clip, e: VideoRecordEvent) {
        val dur = e.recordingStats.recordedDurationNanos / 1_000_000
        if (e is VideoRecordEvent.Status) {
            if (e.recordingStats.numBytesRecorded != c.bytes) { c.bytes = e.recordingStats.numBytesRecorded; c.progressAt = SystemClock.elapsedRealtime() }
            if (c.t0 == 0L && dur > 0) { // first real video: the subtitle clock starts here
                c.t0 = System.currentTimeMillis() - dur
                c.srt = Srt(c.t0)
                if (lastVideoEnd > 0) AppLog.i("Gap since previous clip: ${c.t0 - lastVideoEnd} ms")
                failures = 0
                stampFailures = 0
                quickRetries = 0
                onChange?.invoke()
            }
        }
        if (e is VideoRecordEvent.Finalize) finished(c, e, dur)
    }

    private fun finished(c: Clip, e: VideoRecordEvent.Finalize, dur: Long) {
        val now = System.currentTimeMillis()
        if (clip === c) clip = null
        if (c.cue.isNotEmpty() && s.srt) c.srt?.add(c.cueFrom, now, c.cue)
        val srt = if (s.srt) c.srt?.toString().orEmpty() else ""
        val gpx = if (s.gpx && c.track.isNotEmpty()) Gpx.build(c.seg.name, c.track) else ""
        AppLog.i("Clip ${c.seg.name}: ${dur / 1000.0} s, ${e.recordingStats.numBytesRecorded / 1024} KB, result ${e.error}")
        if (c.t0 > 0) lastVideoEnd = c.t0 + dur
        val bad = e.error == VideoRecordEvent.Finalize.ERROR_NO_VALID_DATA || dur <= 0
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
            }
            processEvents(!wanted || paused)
        }
        onChange?.invoke()
        // Stopped by us: the next clip starts at once. Stopped by the system (camera taken by another app, disk full...): retry with a delay.
        if (wanted && !paused && !closed && clip == null) {
            if (c.stopping && !bad && !c.stalled) startClip()
            else if (e.error == VideoRecordEvent.Finalize.ERROR_SOURCE_INACTIVE) {
                // The camera stream was re-configured (normal while starting) or another app took the camera: not the stamp's fault.
                if (quickRetries++ < 3) main.postDelayed(::startClip, 400) else fail("The camera is in use by another app", null, false)
            } else fail(if (c.stalled) "The camera stopped delivering video" else "Recording was interrupted (code ${e.error})", e.cause)
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
            if (now - c.t0 >= s.segmentSeconds * 1000L || synchronized(pending) { pending.any { now >= it.until } }) stopClip()
        }
        if (SystemClock.elapsedRealtime() - c.progressAt > 15_000) { // camera delivers nothing
            c.stalled = true
            stopClip()
        }
    }

    // ---------------- Storage (Store.io thread) ----------------

    private fun scan() {
        if (!Store.ensure()) return
        val found = ArrayList<Segment>()
        for ((name, files) in Store.loop.listFiles().orEmpty().groupBy { it.nameWithoutExtension }) {
            val start = Names.parse(name) ?: continue // not ours: never touched
            val mp4 = files.firstOrNull { it.extension == "mp4" }
            if (mp4 == null || mp4.length() == 0L) { files.filter { it.extension in EXT }.forEach { it.delete() }; continue }
            found += Segment(name, start, maxOf(start, mp4.lastModified()), files.sumOf { it.length() })
        }
        synchronized(segments) { segments.addAll(0, found.filter { f -> segments.none { it.name == f.name } }.sortedBy { it.start }) }
    }

    /** Frees space for the current and the next clip. Footage an incident may still need is kept. */
    private fun makeRoom() {
        val st = s
        val incoming = LoopPolicy.estimate(st.bitrateMbps * 1_000_000, st.audio, st.segmentSeconds) * 2
        val reserve = (st.reserveGb * GB).toLong()
        val keepFrom = (synchronized(pending) { pending.minOfOrNull { it.time } } ?: System.currentTimeMillis()) - st.preSeconds * 1000L
        val all = synchronized(segments) { segments.toList() }
        val candidates = all.map { Segment(it.name, it.start, it.end, it.size, it.inProgress || it.end > keepFrom) }
        var free = Store.freeBytes()
        for (d in LoopPolicy.toDelete(candidates, if (st.unlimited) null else (st.capGb * GB).toLong(), free, reserve, incoming)) {
            EXT.forEach { File(Store.loop, "${d.name}.$it").delete() }
            synchronized(segments) { segments.removeAll { it.name == d.name } }
            free += d.size
        }
        if (free < reserve) main.post {
            Hub.post("full", Severity.ALARM, "Phone storage is full", "Recording stopped. Delete saved incidents or other files.", 120_000)
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
            var dir = File(Store.events, Names.of(p.time) + if (p.reason == "manual") "_manual" else "_impact")
            var n = 2
            val base = dir.name
            while (dir.exists()) dir = File(Store.events, "$base (${n++})")
            if (!dir.mkdirs()) { AppLog.i("Cannot create incident folder"); Hub.post("event", Severity.ALARM, "Incident not saved", "Cannot create the incident folder"); continue }
            var moved = 0
            for (seg in segs) {
                var ok = false
                for (x in EXT) {
                    val f = File(Store.loop, "${seg.name}.$x")
                    if (f.exists() && f.renameTo(File(dir, f.name)) && x == "mp4") ok = true
                }
                if (ok) { moved++; synchronized(segments) { segments -= seg } } else AppLog.i("Could not move ${seg.name}")
            }
            val info = listOf("DriveDeck incident", "Type: " + if (p.reason == "manual") "saved manually" else "impact detected",
                "Time: " + Stamp.text(p.time, null, null, true, false, false, ""),
                "Peak acceleration: %.2f g".format(Locale.ROOT, p.peak), "Speed: " + (p.speed?.let { "${it.roundToInt()} km/h" } ?: "unknown"),
                "Position: " + (p.fix?.let { "%.6f, %.6f".format(Locale.ROOT, it.lat, it.lon) } ?: "unknown"),
                "Video files: " + if (moved == 0) "none" else segs.joinToString { it.name + ".mp4" })
            try { File(dir, "incident.txt").writeText(info.joinToString("\r\n") + "\r\n") } catch (e: IOException) { AppLog.e("incident.txt", e) }
            Hub.post("event:${dir.name}", if (moved > 0) Severity.INFO else Severity.WARNING, if (moved > 0) "Incident saved" else "Incident has no video",
                "$moved video(s) in Documents/DriveDeck/Events/${dir.name}")
        }
    }

    companion object {
        private const val GB = 1024.0 * 1024 * 1024
        val EXT = listOf("mp4", "srt", "gpx")
    }
}
