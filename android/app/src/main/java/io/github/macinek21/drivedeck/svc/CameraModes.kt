package io.github.macinek21.drivedeck.svc

import android.content.Context
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraManager
import android.media.CamcorderProfile
import android.media.MediaCodecList
import android.media.MediaExtractor
import android.media.MediaFormat
import android.media.MediaRecorder
import android.os.Build
import android.util.Range
import android.util.Size
import androidx.camera.core.CameraInfo
import androidx.camera.core.CameraSelector
import androidx.camera.core.DynamicRange
import androidx.camera.core.Preview
import androidx.camera.core.SessionConfig
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.video.Quality
import androidx.camera.video.QualitySelector
import androidx.camera.video.Recorder
import androidx.camera.video.VideoCapture
import androidx.core.content.ContextCompat
import io.github.macinek21.drivedeck.BuildConfig
import io.github.macinek21.drivedeck.core.Video
import io.github.macinek21.drivedeck.core.VideoMode
import java.io.File

/** Finds out which resolutions and frame rates the back camera really offers to apps. */
object CameraModes {
    private const val CODEC = Recorder.VIDEO_CAPABILITIES_SOURCE_CODEC_CAPABILITIES
    private const val PROFILE = Recorder.VIDEO_CAPABILITIES_SOURCE_CAMCORDER_PROFILE
    private val QUALITIES = listOf("480p" to Quality.SD, "720p" to Quality.HD, "1080p" to Quality.FHD, "1440p" to Quality.QHD, "2160p" to Quality.UHD)
    private val NOMINAL = mapOf("480p" to Size(720, 480), "720p" to Size(1280, 720), "1080p" to Size(1920, 1080), "1440p" to Size(2560, 1440), "2160p" to Size(3840, 2160))

    /** Resolutions only the phone's camcorder profiles know, not the codec list. */
    private val profileOnly = HashSet<Quality>()
    private var reported: List<VideoMode>? = null

    val hevc: Boolean by lazy {
        MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.any { c ->
            c.isEncoder && MediaFormat.MIMETYPE_VIDEO_HEVC in c.supportedTypes && !c.name.startsWith("OMX.google") && !c.name.startsWith("c2.android")
        }
    }

    fun quality(res: String) = QUALITIES.first { it.first == res }.second

    fun recorder(q: Quality) = Recorder.Builder().setQualitySelector(QualitySelector.from(q))
        .setVideoCapabilitiesSource(if (q in profileOnly) PROFILE else CODEC)

    /** Calls back on the main thread; an empty list means the camera could not be asked. */
    fun load(c: Context, done: (List<VideoMode>) -> Unit) {
        val f = ProcessCameraProvider.getInstance(c.applicationContext)
        f.addListener({ done(try { query(f.get()) } catch (e: Exception) { AppLog.e("camera modes", e); emptyList() }) }, ContextCompat.getMainExecutor(c))
    }

    /** Everything the phone's own information offers, without the modes it has already refused in real use. */
    fun query(p: ProcessCameraProvider): List<VideoMode> {
        val refused = refused()
        return (listed(p) + special).filter { it.id !in refused || !it.special }.sortedWith(compareBy({ Video.RES.indexOf(it.res) }, { it.fps }))
    }

    private var special: List<VideoMode> = emptyList()

    private fun listed(p: ProcessCameraProvider) = reported ?: ask(p).also { if (it.isNotEmpty()) reported = it }

    private fun ask(p: ProcessCameraProvider): List<VideoMode> {
        val info = p.getCameraInfo(CameraSelector.DEFAULT_BACK_CAMERA)
        val out = ArrayList<VideoMode>()
        val codec = Recorder.getVideoCapabilities(info, CODEC)
        val profile = Recorder.getVideoCapabilities(info, PROFILE)
        for ((res, q) in QUALITIES) {
            if (!codec.isQualitySupported(q, DynamicRange.SDR)) {
                if (!profile.isQualitySupported(q, DynamicRange.SDR)) continue
                profileOnly += q
            }
            val size = QualitySelector.getResolution(info, q) ?: NOMINAL.getValue(res)
            val session = SessionConfig(listOf(Preview.Builder().build(), VideoCapture.withOutput(recorder(q).build())))
            out += modes(res, size, ranges(info, session))
        }
        special = try { highSpeed(out) } catch (e: Exception) { AppLog.e("high speed modes", e); emptyList() }
        special = special + Video.legacyModes(prefs().getString("params", null), features(), out + special) { w, h, _ -> canEncode(w, h, 1) } // real rates are checked in the recording itself; chips understate them
        if (BuildConfig.DEBUG) Regex("debug-legacy=([0-9]+)").find(prefs().getString("params", "").orEmpty())?.let { special = special + VideoMode("720p", 1280, 720, it.groupValues[1].toInt(), legacy = true) } // engine test without a Samsung phone
        AppLog.i("Camera modes: " + describe(out + special))
        return out
    }

    fun describe(l: List<VideoMode>) = l.groupBy { it.res }.map { (r, x) -> r + " " + x.joinToString("/") { "${it.fps}" + (if (it.highSpeed) "hs" else "") + (if (it.legacy) "L" else "") } }.joinToString(", ")

    private fun ranges(info: CameraInfo, s: SessionConfig): Set<Range<Int>> = try { info.getSupportedFrameRateRanges(s) } catch (e: Exception) { AppLog.e("frame rates", e); emptySet() }

    /** Per frame rate the most constant range the camera has. */
    private fun modes(res: String, size: Size, ranges: Collection<Range<Int>>) = ranges.filter { it.upper in Video.FPS && it.upper >= 24 }
        .groupBy { it.upper }.map { (fps, r) -> VideoMode(res, size.width, size.height, fps, r.maxOf { it.lower }) }.sortedBy { it.fps }

    /** Official high-speed modes, straight from the camera's description (the camera library misses them on some phones). */
    private fun highSpeed(normal: List<VideoMode>): List<VideoMode> {
        val m = Store.app.getSystemService(CameraManager::class.java)
        val ch = m.getCameraCharacteristics(backId(m) ?: return emptyList())
        if (ch.get(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES)?.contains(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES_CONSTRAINED_HIGH_SPEED_VIDEO) != true) return emptyList()
        val map = ch.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: return emptyList()
        return map.highSpeedVideoSizes.orEmpty().flatMap { z ->
            val res = NOMINAL.entries.firstOrNull { it.value == z }?.key ?: return@flatMap emptyList()
            // Only constant rates: a variable high-speed range would not give steady video.
            map.getHighSpeedVideoFpsRangesFor(z).filter { it.lower == it.upper && it.upper in Video.FPS && normal.none { n -> n.res == res && n.fps == it.upper } && canEncode(z.width, z.height, it.upper) }
                .map { VideoMode(res, z.width, z.height, it.upper, highSpeed = true) }
        }
    }

    fun backId(m: CameraManager): String? = m.cameraIdList.firstOrNull { m.getCameraCharacteristics(it).get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_BACK }

    val sensorOrientation: Int by lazy {
        val m = Store.app.getSystemService(CameraManager::class.java)
        backId(m)?.let { m.getCameraCharacteristics(it).get(CameraCharacteristics.SENSOR_ORIENTATION) } ?: 90
    }

    private fun canEncode(w: Int, h: Int, fps: Int) = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.any { c ->
        c.isEncoder && MediaFormat.MIMETYPE_VIDEO_AVC in c.supportedTypes && try {
            c.getCapabilitiesForType(MediaFormat.MIMETYPE_VIDEO_AVC).videoCapabilities?.let { it.isSizeSupported(w, h) && it.getSupportedFrameRatesFor(w, h).upper >= fps } == true
        } catch (_: IllegalArgumentException) { false }
    }

    private fun prefs() = Store.app.getSharedPreferences("camera_modes", Context.MODE_PRIVATE)

    /** Forgets everything after an Android update, when the camera software may have changed. */
    private fun sameFirmware() = prefs().getString("firmware", "") == Build.FINGERPRINT

    /** A new app version may handle modes better, so earlier refusals are tried again. */
    private fun refused(): Set<String> = if (sameFirmware() && prefs().getInt("app", 0) == BuildConfig.VERSION_CODE) prefs().getStringSet("refused", emptySet()).orEmpty() else emptySet()

    fun refuse(m: VideoMode) = prefs().edit().putStringSet("refused", refused() + m.id).putInt("app", BuildConfig.VERSION_CODE).apply()

    private fun key(m: VideoMode, hevc: Boolean) = "rate ${m.id} $hevc"
    fun scale(m: VideoMode, hevc: Boolean) = prefs().getFloat(key(m, hevc), Video.startScale(m).toFloat()).toDouble()
    fun learnScale(m: VideoMode, hevc: Boolean, used: Double, wanted: Int, actual: Double) {
        val now = scale(m, hevc)
        val better = minOf(now, Video.correctScale(used, wanted, actual))
        if (better < now - 0.001) { prefs().edit().putFloat(key(m, hevc), better.toFloat()).apply(); AppLog.i("Bitrate of ${m.id}: %.1f instead of %.1f Mbit/s, corrected".format(java.util.Locale.ROOT, actual / 1e6, wanted / 1e6)) }
    }

    /** Real frame rate of a special mode on this phone, 0 = not recorded yet. */
    fun realFps(m: VideoMode) = prefs().getInt("fps ${m.id}", 0)
    fun saveRealFps(m: VideoMode, fps: Int) = prefs().edit().putInt("fps ${m.id}", fps).apply()

    /** Samsung's description of its own camera, readable on some firmware versions. */
    private fun features(): String? = (listOf("camera-feature.xml") + (1..12).map { "camera-feature-v$it.xml" }).map { File("/system/cameradata", it) }
        .firstNotNullOfOrNull { f -> try { if (f.canRead() && f.length() < 2_000_000) f.readText() else null } catch (_: java.io.IOException) { null } }

    /**
     * Reads the older camera interface's parameters once per firmware. Must run before the camera is in use, off the main thread.
     * Other makers' phones have nothing to add here, so they are not asked.
     */
    @Suppress("DEPRECATION")
    fun learn(c: Context) {
        if (sameFirmware()) return
        var params: String? = null
        if (Build.MANUFACTURER.equals("samsung", true)) try {
            val cam = android.hardware.Camera.open(0)
            try { params = cam.parameters.flatten() } finally { cam.release() }
        } catch (e: Exception) { AppLog.i("CAM1 not available: ${e.message}"); return }
        prefs().edit().clear().putString("firmware", Build.FINGERPRINT).putString("params", params).apply()
        report(c)
        val words = listOf("fps", "video-size", "fast", "speed", "frame-rate", "recording")
        params?.let { AppLog.i("CAM1 " + it.split(';').filter { p -> words.any { w -> p.substringBefore('=').contains(w) } }.joinToString("; ")) }
        AppLog.i("CAM features " + (features()?.lineSequence()?.filter { it.contains("CAMCORDER_RESOLUTION", true) }?.joinToString(" ") { it.trim() }?.take(6000) ?: "not readable"))
    }

    /** Real frame rate and size of a recorded file: (frames per second, width, height). */
    fun measure(f: File): Triple<Double, Int, Int> {
        val x = MediaExtractor()
        try {
            x.setDataSource(f.path)
            val t = (0 until x.trackCount).firstOrNull { x.getTrackFormat(it).getString(MediaFormat.KEY_MIME)?.startsWith("video/") == true } ?: return Triple(0.0, 0, 0)
            val fmt = x.getTrackFormat(t)
            x.selectTrack(t)
            var n = 0
            // The first two seconds are enough; reading a whole clip back would slow down the memory card while recording.
            var first = Long.MAX_VALUE
            var last = Long.MIN_VALUE
            while (x.sampleTime >= 0 && (first == Long.MAX_VALUE || x.sampleTime - first < 2_000_000)) {
                first = minOf(first, x.sampleTime)
                last = maxOf(last, x.sampleTime)
                n++
                x.advance()
            }
            return Triple(if (n > 5 && last > first) (n - 1) * 1e6 / (last - first) else 0.0, fmt.getInteger(MediaFormat.KEY_WIDTH), fmt.getInteger(MediaFormat.KEY_HEIGHT))
        } catch (e: Exception) { AppLog.i("measure: ${e.message}"); return Triple(0.0, 0, 0) } finally { x.release() }
    }

    /** Everything the phone says about its back cameras, for the log. Contains no personal data. */
    fun report(c: Context) {
        try {
            val m = c.getSystemService(CameraManager::class.java)
            AppLog.i("CAM ids ${m.cameraIdList.joinToString()}; HEVC encoder $hevc; build ${Build.DISPLAY}")
            for (id in m.cameraIdList) {
                val ch = m.getCameraCharacteristics(id)
                if (ch.get(CameraCharacteristics.LENS_FACING) != CameraCharacteristics.LENS_FACING_BACK) continue
                AppLog.i("CAM $id level ${ch.get(CameraCharacteristics.INFO_SUPPORTED_HARDWARE_LEVEL)} caps ${ch.get(CameraCharacteristics.REQUEST_AVAILABLE_CAPABILITIES)?.joinToString()}")
                AppLog.i("CAM $id AE fps ${ch.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES)?.joinToString()}")
                val s = ch.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP) ?: continue
                for (z in s.getOutputSizes(MediaRecorder::class.java).orEmpty().filter { it.height in listOf(480, 720, 1080, 1440, 2160) })
                    AppLog.i("CAM $id $z min frame ${s.getOutputMinFrameDuration(MediaRecorder::class.java, z) / 1000} us")
                for (z in s.highSpeedVideoSizes.orEmpty()) AppLog.i("CAM $id high speed $z ${s.getHighSpeedVideoFpsRangesFor(z).joinToString()}")
                AppLog.i("CAM $id vendor keys " + ch.keys.map { it.name }.filter { !it.startsWith("android.") }.joinToString())
                AppLog.i("CAM $id request keys " + ch.availableCaptureRequestKeys.map { it.name }.filter { !it.startsWith("android.") }.joinToString())
            }
            @Suppress("DEPRECATION")
            for (q in listOf(4, 5, 6, 8, 11, 12, 2000, 2001, 2002, 2003, 2004, 2005)) if (CamcorderProfile.hasProfile(0, q))
                CamcorderProfile.get(0, q).let { AppLog.i("CAM profile $q ${it.videoFrameWidth}x${it.videoFrameHeight} ${it.videoFrameRate} fps ${it.videoBitRate / 1000} kbit/s codec ${it.videoCodec}") }
            for (i in MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos.filter { it.isEncoder }) for (t in i.supportedTypes.filter { it == MediaFormat.MIMETYPE_VIDEO_AVC || it == MediaFormat.MIMETYPE_VIDEO_HEVC }) {
                val v = i.getCapabilitiesForType(t).videoCapabilities ?: continue
                AppLog.i("ENC ${i.name} $t " + NOMINAL.values.filter { v.isSizeSupported(it.width, it.height) }.joinToString { "$it up to ${v.getSupportedFrameRatesFor(it.width, it.height).upper.toInt()} fps" })
            }
        } catch (e: Exception) { AppLog.e("camera report", e) }
    }
}
