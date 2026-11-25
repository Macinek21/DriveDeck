package io.github.macinek21.drivedeck.svc

import android.content.Context
import android.media.MediaScannerConnection
import android.os.Build
import android.os.Environment
import android.os.Handler
import android.os.Looper
import android.os.StatFs
import android.os.SystemClock
import android.util.AtomicFile
import android.util.Log
import io.github.macinek21.drivedeck.BuildConfig
import io.github.macinek21.drivedeck.core.Dtc
import io.github.macinek21.drivedeck.core.Pids
import io.github.macinek21.drivedeck.core.Settings
import io.github.macinek21.drivedeck.core.ValueDef
import java.io.File
import java.io.IOException
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors

enum class Severity { INFO, WARNING, ALARM }
class Notice(val key: String, val severity: Severity, val title: String, val detail: String, val time: Long = System.currentTimeMillis())
class Sample(val v: Double, private val at: Long = SystemClock.elapsedRealtime()) { val stale get() = SystemClock.elapsedRealtime() - at > 7000 }

/** A place recordings can go: the phone itself or a memory card. */
class Place(val id: String, val root: File) {
    val loop get() = File(root, "Loop")
    val events get() = File(root, "Events")
    val label get() = if (id == "card") "Memory card" else "Phone"
    fun ensure(): Boolean = (loop.isDirectory || loop.mkdirs()) && (events.isDirectory || events.mkdirs())
    fun free(): Long = try { StatFs(existing().path).availableBytes } catch (_: IllegalArgumentException) { 0 }
    fun total(): Long = try { StatFs(existing().path).totalBytes } catch (_: IllegalArgumentException) { 0 }
    private fun existing(): File { var f = root; while (!f.exists()) f = f.parentFile ?: break; return f }
}

/**
 * Phone: Documents/DriveDeck, which survives uninstalling. Memory card: the same folder on Android 11+; older versions only let
 * apps write to their own folder there (Android/media/<app>/DriveDeck), which Android deletes when the app is uninstalled.
 */
object Store {
    lateinit var app: Context
    val io = Executors.newSingleThreadExecutor { Thread(it, "io") }!!
    /** Set while Android is ejecting the card, so nothing new is written to it. */
    @Volatile var ejecting = false

    val device: Place by lazy {
        @Suppress("DEPRECATION") Place("device", File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOCUMENTS), "DriveDeck"))
    }

    /** The memory card, or null when none is usable right now. */
    fun card(): Place? {
        if (ejecting) return null
        val own = app.getExternalFilesDirs(null).drop(1).firstOrNull {
            it != null && Environment.getExternalStorageState(it) == Environment.MEDIA_MOUNTED && Environment.isExternalStorageRemovable(it)
        } ?: return null
        val volume = own.path.substringBefore("/Android/", "")
        if (volume.isEmpty()) return null
        if (Build.VERSION.SDK_INT >= 30) return Place("card", File(volume, "Documents/DriveDeck"))
        @Suppress("DEPRECATION") val media = app.externalMediaDirs.firstOrNull { it != null && it.path.startsWith("$volume/") } ?: return null
        return Place("card", File(media, "DriveDeck"))
    }

    fun places() = listOfNotNull(device, card())

    /** Up to Android 10 files written by an app only show up (with the right size) over USB after they were announced. */
    fun announce(vararg files: File) {
        if (Build.VERSION.SDK_INT < 30) MediaScannerConnection.scanFile(app, files.map { it.path }.toTypedArray(), null, null)
    }
    fun wanted() = if (Hub.settings.cam.storage == "card") card() else device
}

/** Diagnostic log: app events and errors only, no positions and no car data. Rotated at 256 KB. */
object AppLog {
    private val io = Executors.newSingleThreadExecutor { Thread(it, "log") }
    private val fmt = SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.ROOT)
    private var file: File? = null
    private var announced = 0L

    /** Makes the complete log visible over USB now. */
    fun announce() = io.execute { file?.let { Store.announce(it) } }

    fun i(m: String) = write("INFO  $m")
    fun e(where: String, t: Throwable) = write("ERROR $where: ${Log.getStackTraceString(t).take(4000)}")

    private fun write(line: String) {
        val at = Date()
        if (BuildConfig.DEBUG) Log.d("DriveDeck", line)
        io.execute {
            // A log left by an earlier installation may not be writable any more; use the next free name then.
            for (n in 0..3) {
                val f = file ?: File(Store.device.root, if (n == 0) "DriveDeck-log.txt" else "DriveDeck-log-$n.txt")
                try {
                    if (f.length() > 256 * 1024) f.renameTo(File(Store.device.root, "DriveDeck-log.old.txt"))
                    f.appendText(fmt.format(at) + " " + line + "\r\n")
                    file = f
                    if (System.currentTimeMillis() - announced > 10_000 || line.contains("Service stopped")) { announced = System.currentTimeMillis(); Store.announce(f) }
                    return@execute
                } catch (_: IOException) { file = null }
            }
        }
    }
}

/** State shared by the service and the screens. */
object Hub {
    val main = Handler(Looper.getMainLooper())
    lateinit var settings: Settings
    val values = ConcurrentHashMap<String, Sample>()
    val unsupported: MutableSet<String> = ConcurrentHashMap.newKeySet()
    @Volatile var dtcs: Set<Dtc> = emptySet()
    @Volatile var obdStatus = "off"
    @Volatile var obdConnected = false
    @Volatile var gpsStatus = "off"
    @Volatile var gForce = 0.0
    /** Main thread only. */
    val notices = ArrayList<Notice>()
    var onNotice: ((Notice) -> Unit)? = null
    var onNoticesChanged: (() -> Unit)? = null
    var onImpact: ((Double) -> Unit)? = null
    /** Value keys on the visible page. Main thread only. */
    var visibleKeys: Set<String> = emptySet()
    private val lastPosted = HashMap<String, Long>()
    private var loaded = false

    fun allValues(): List<ValueDef> = Pids.all(settings.obd.custom)

    private fun file(c: Context) = AtomicFile(File(c.filesDir, "settings.json"))

    fun load(c: Context) {
        if (loaded) return
        loaded = true
        Store.app = c.applicationContext
        settings = try { Settings.fromJson(file(c).readFully().toString(Charsets.UTF_8)).settings } catch (_: IOException) { null } ?: Settings()
    }

    /** AtomicFile keeps the previous settings if writing is interrupted. */
    fun save(c: Context): Boolean {
        settings.normalize()
        val f = file(c)
        val out = try { f.startWrite() } catch (e: IOException) { AppLog.e("save", e); return false }
        return try { out.write(settings.toJson().toByteArray()); f.finishWrite(out); true } catch (e: IOException) { f.failWrite(out); AppLog.e("save", e); false }
    }

    fun post(key: String, s: Severity, title: String, detail: String, throttleMs: Long = 0) {
        main.post {
            val now = SystemClock.elapsedRealtime()
            val last = lastPosted[key]
            if (throttleMs > 0 && last != null && now - last < throttleMs) return@post
            lastPosted[key] = now
            if (!key.startsWith("dtc:") && !key.startsWith("alarm:")) AppLog.i("Notice [$s] $title: $detail")
            notices.removeAll { it.key == key }
            val n = Notice(key, s, title, detail)
            notices.add(0, n)
            while (notices.size > 30) notices.removeAt(notices.lastIndex)
            onNotice?.invoke(n)
            onNoticesChanged?.invoke()
        }
    }

    fun remove(match: (Notice) -> Boolean) {
        main.post { if (notices.removeAll(match)) onNoticesChanged?.invoke() }
    }
}
