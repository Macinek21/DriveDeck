package io.github.macinek21.drivedeck.ui

import android.Manifest
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.activity.OnBackPressedCallback
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import io.github.macinek21.drivedeck.BuildConfig
import io.github.macinek21.drivedeck.core.Formula
import io.github.macinek21.drivedeck.core.LoopPolicy
import io.github.macinek21.drivedeck.core.Settings
import io.github.macinek21.drivedeck.core.Stamp
import io.github.macinek21.drivedeck.core.ValueDef
import io.github.macinek21.drivedeck.svc.AppLog
import io.github.macinek21.drivedeck.svc.BluetoothTransport
import io.github.macinek21.drivedeck.svc.DriveService
import io.github.macinek21.drivedeck.svc.Hub
import io.github.macinek21.drivedeck.svc.Store
import kotlin.math.floor

class SettingsActivity : FormActivity() {
    private lateinit var s: Settings
    private lateinit var original: String
    private var meter: TextView? = null
    private var peak = 0.0
    private val tick = object : Runnable {
        override fun run() {
            peak = maxOf(peak, Hub.gForce)
            meter?.text = "Live: %.2f g   Peak: %.2f g".format(Hub.gForce, peak)
            Hub.main.postDelayed(this, 200)
        }
    }

    private val askMic = registerForActivityResult(ActivityResultContracts.RequestPermission()) { ok -> if (!ok) { s.cam.audio = false; build() } }
    private val askBt = registerForActivityResult(ActivityResultContracts.RequestPermission()) { build() }
    private val exportTo = registerForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { it?.let(::export) }
    private val importFrom = registerForActivityResult(ActivityResultContracts.OpenDocument()) { it?.let(::import) }

    override fun onCreate(b: Bundle?) {
        super.onCreate(b)
        s = b?.getString("draft")?.let { Settings.fromJson(it).settings } ?: Hub.settings.copy()
        original = Hub.settings.copy().toJson()
        frame("Settings")
        barButton("Cancel") { leave() }
        barButton("Save") { save() }
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) { override fun handleOnBackPressed() = leave() })
        build()
    }

    override fun onSaveInstanceState(o: Bundle) {
        super.onSaveInstanceState(o)
        o.putString("draft", s.toJson())
    }

    override fun onStart() { super.onStart(); Hub.main.post(tick) }
    override fun onStop() { Hub.main.removeCallbacks(tick); super.onStop() }

    private fun leave() {
        if (s.toJson() == original) { finish(); return }
        AlertDialog.Builder(this).setTitle("Discard changes?").setPositiveButton("Discard") { _, _ -> finish() }.setNegativeButton("Keep editing", null).show()
    }

    private fun save() {
        s.normalize()
        val old = Hub.settings
        Hub.settings = s
        if (!Hub.save(this)) { Hub.settings = old; info("Could not save", "The settings could not be written."); return }
        DriveService.instance?.applySettings(old)
        finish()
    }

    private fun info(title: String, text: String) { AlertDialog.Builder(this).setTitle(title).setMessage(text).setPositiveButton("OK", null).show() }

    private fun hours(gb: Double): String {
        val h = gb * 1073741824.0 / LoopPolicy.estimate(s.cam.bitrateMbps * 1_000_000, s.cam.audio, 3600)
        return if (h < 1) "${(h * 60).toInt()} min" else "%.1f h".format(h)
    }

    private fun build(): Unit = rebuild {
        val f = Form(this, body)
        val c = s.cam
        val o = s.obd
        val u = s.ui
        f.header("Dashcam")
        f.switch("Dashcam enabled", c.enabled) { c.enabled = it }
        f.switch("Start recording when the app opens", c.autoStart) { c.autoStart = it }
        f.choice("Resolution", listOf("1080p" to "1080p", "720p" to "720p (cooler phone, smaller files)"), c.resolution) { c.resolution = it }
        f.choice("Frame rate", listOf(30 to "30 fps", 24 to "24 fps"), c.fps) { c.fps = it }
        f.slider("Video bitrate", 2.0, 30.0, 1.0, c.bitrateMbps.toDouble(), { "%.0f Mbit/s".format(it) }) { c.bitrateMbps = it.toInt() }
        f.switch("Record sound (microphone)", c.audio) { c.audio = it; if (it) askMic.launch(Manifest.permission.RECORD_AUDIO) }
        f.choice("Clip length", listOf(30 to "30 s", 60 to "1 min", 120 to "2 min", 180 to "3 min", 300 to "5 min"), c.segmentSeconds) { c.segmentSeconds = it }

        f.header("Storage")
        f.switch("Unlimited (use all space except the reserve)", c.unlimited) { c.unlimited = it; build() }
        f.slider("Maximum for loop recording", 1.0, maxOf(8.0, floor(Store.totalGb())), 1.0, c.capGb, { "%.0f GB (about %s)".format(it, hours(it)) }, !c.unlimited) { c.capGb = it }
        f.slider("Always keep free on the phone", 0.5, 8.0, 0.5, c.reserveGb, { "%.1f GB".format(it) }) { c.reserveGb = it }
        f.note("At the limit the oldest loop clips are overwritten. Saved incidents are never deleted automatically. Files are in Documents/DriveDeck.")

        f.header("GPS and speed in the video")
        f.switch("Use GPS", c.gps) { c.gps = it }
        f.switch("Burn a stamp into the video", c.stamp) { c.stamp = it; build() }
        if (c.stamp) {
            f.switch("    Date and time", c.stampDate) { c.stampDate = it }
            f.switch("    GPS speed", c.stampSpeed) { c.stampSpeed = it }
            f.switch("    GPS coordinates", c.stampCoords) { c.stampCoords = it }
            f.text("Extra label, e.g. licence plate", c.label, Stamp.MAX_LABEL, "optional") { c.label = it }
        }
        f.switch("Subtitle file with speed and position (.srt)", c.srt) { c.srt = it }
        f.switch("GPS track file (.gpx)", c.gpx) { c.gpx = it }

        f.header("Impact detection")
        f.switch("Save footage automatically on impact", c.impact) { c.impact = it; build() }
        meter = null
        if (c.impact) {
            f.choice("Sensitivity", listOf("high" to "High (1.3 g)", "medium" to "Medium (2.0 g)", "low" to "Low (3.0 g)", "custom" to "Custom"), c.sensitivity) { c.sensitivity = it; build() }
            if (c.sensitivity == "custom") f.slider("Threshold", 0.8, 8.0, 0.1, c.thresholdG, { "%.1f g".format(it) }) { c.thresholdG = it }
            meter = f.note("")
            f.note("Mount the phone, drive over a few potholes and compare the peak with the threshold.")
            f.slider("Keep before impact", 10.0, 180.0, 10.0, c.preSeconds.toDouble(), { "%.0f s".format(it) }) { c.preSeconds = it.toInt() }
            f.slider("Keep after impact", 10.0, 120.0, 10.0, c.postSeconds.toDouble(), { "%.0f s".format(it) }) { c.postSeconds = it.toInt() }
            f.switch("Offer an emergency call after an impact", c.offerCall) { c.offerCall = it }
            f.choice("Emergency number", listOf("112" to "112 (EU)", "155" to "155 (CZ ambulance)", "158" to "158 (CZ police)", "150" to "150 (CZ fire)", "911" to "911", "999" to "999"), c.emergency) { c.emergency = it }
        }

        f.header("OBD-II (ELM327 Bluetooth)")
        f.switch("Read data from the car", o.enabled) { o.enabled = it }
        if (!BluetoothTransport.permitted(this)) f.button("Allow Bluetooth access") { if (Build.VERSION.SDK_INT >= 31) askBt.launch(Manifest.permission.BLUETOOTH_CONNECT) }
        val paired = BluetoothTransport.paired(this)
        val items = listOf("" to "(none)") + paired + (if (o.address.isNotEmpty() && paired.none { it.first == o.address }) listOf(o.address to "${o.name} (not paired)") else emptyList())
        f.choice("Adapter", items, o.address) { a -> o.address = a; o.name = items.first { it.first == a }.second }
        f.note("First pair the ELM327 in Android's Bluetooth settings (PIN 1234 or 0000). OBDeleven only works with its own app.")
        f.choice("Protocol", listOf("6" to "CAN 11-bit 500k (SEAT Leon Mk4)", "0" to "Automatic", "7" to "CAN 29-bit 500k", "8" to "CAN 11-bit 250k", "9" to "CAN 29-bit 250k"), o.protocol) { o.protocol = it }
        f.choice("Fuel (for consumption)", listOf("petrol" to "Petrol", "diesel" to "Diesel"), o.fuel) { o.fuel = it }
        f.slider("Check fault codes every", 10.0, 300.0, 10.0, o.dtcSeconds.toDouble(), { "%.0f s".format(it) }) { o.dtcSeconds = it.toInt() }
        f.switch("Also read permanent fault codes", o.permanent) { o.permanent = it }
        f.note("Read-only: DriveDeck cannot clear codes, change coding or run tests.")
        f.header("Custom values (advanced)")
        f.note("Read services 01, 02, 03, 07, 09, 0A, 19, 21 and 22 only. The formula uses A-H for the reply bytes, e.g. (A*256+B)/10.")
        for (v in o.custom) f.button("${v.name}  (${v.header} ${v.request})") { editCustom(v) }
        if (o.custom.size < Settings.MAX_CUSTOM) f.button("Add custom value") { editCustom(null) }

        f.header("Display")
        f.choice("Notification strip", listOf("top" to "Top", "bottom" to "Bottom"), u.notices) { u.notices = it }
        f.slider("Shrink warnings to a badge after", 0.0, 60.0, 2.0, u.collapseSeconds.toDouble(), { if (it < 1) "never" else "%.0f s".format(it) }) { u.collapseSeconds = it.toInt() }
        f.switch("Keep the screen on", u.keepOn) { u.keepOn = it }
        f.switch("Dim the display (night)", u.dim) { u.dim = it }
        f.choice("Accent colour", COLORS.drop(1), u.accent) { u.accent = it }

        f.header("Backup")
        f.button("Export settings…") { exportTo.launch("DriveDeck-settings.json") }
        f.button("Import settings…") { importFrom.launch(arrayOf("application/json", "text/plain", "application/octet-stream")) }
        f.button("Reset to defaults") {
            AlertDialog.Builder(this).setTitle("Reset all settings?").setMessage("Settings and dashboard layout return to defaults. Recordings are not touched.")
                .setPositiveButton("Reset") { _, _ -> s = Settings(); build() }.setNegativeButton("Cancel", null).show()
        }
        f.note("DriveDeck ${BuildConfig.VERSION_NAME}. The app has no internet permission; everything stays on this phone.")
    }

    private fun editCustom(existing: ValueDef?) {
        var d = existing ?: ValueDef("", "", "", "", "22 ", "7E0", "A", priority = 1)
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(20), dp(8), dp(20), 0) }
        val f = Form(this, box)
        f.text("Name", d.name, 24, "e.g. Oil temperature") { d = d.copy(name = it, short = it) }
        f.text("Unit", d.unit, 8, "°C") { d = d.copy(unit = it) }
        f.text("ECU header (3 hex digits, empty = broadcast)", d.header, 3, "7E0") { d = d.copy(header = it) }
        f.text("Request (hex)", d.request, 20, "22 11 BD") { d = d.copy(request = it) }
        f.text("Formula", d.formula, Formula.MAX_LENGTH, "(A*256+B)/10") { d = d.copy(formula = it) }
        f.number("Gauge minimum", d.min) { d = d.copy(min = it ?: 0.0) }
        f.number("Gauge maximum", d.max) { d = d.copy(max = it ?: 100.0) }
        f.choice("Decimals", (0..3).map { it to "$it" }, d.decimals) { d = d.copy(decimals = it) }
        f.choice("Update rate", listOf(0 to "Fast", 1 to "1 s", 2 to "5 s"), d.priority) { d = d.copy(priority = it) }
        val err = f.error()
        val dlg = AlertDialog.Builder(this).setTitle("Custom value").setView(ScrollView(this).apply { addView(box) }).setPositiveButton("OK", null).setNegativeButton("Cancel", null)
            .apply { if (existing != null) setNeutralButton("Delete") { _, _ -> s.obd.custom.remove(existing); build() } }.show()
        dlg.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
            val (ok, why) = Settings.cleanCustom(d)
            if (ok == null) { err.text = why; err.visibility = TextView.VISIBLE; return@setOnClickListener }
            val i = s.obd.custom.indexOf(existing)
            if (i >= 0) s.obd.custom[i] = ok else s.obd.custom += ok
            dlg.dismiss()
            build()
        }
    }

    private fun export(u: Uri) {
        try {
            s.normalize()
            contentResolver.openOutputStream(u, "wt")!!.use { it.write(s.toJson().toByteArray()) }
            info("Exported", "The settings were saved.")
        } catch (e: Exception) { AppLog.e("export", e); info("Not exported", "The file could not be written.") }
    }

    private fun import(u: Uri) {
        val text = try {
            // Never read more than the largest acceptable file, whatever its source claims.
            contentResolver.openInputStream(u)!!.use { i ->
                val buf = ByteArray(Settings.MAX_CHARS + 1)
                var n = 0
                while (n < buf.size) { val r = i.read(buf, n, buf.size - n); if (r < 0) break; n += r }
                String(buf, 0, n, Charsets.UTF_8)
            }
        } catch (e: Exception) { AppLog.e("import", e); info("Not imported", "The file could not be read."); return }
        val p = Settings.fromJson(text)
        val n = p.settings
        if (n == null) { info("Not imported", p.error ?: ""); return }
        s = n
        build()
        info("Imported", (if (p.notes.isEmpty()) "" else "Corrected:\n• " + p.notes.take(8).joinToString("\n• ") + "\n\n") + "Press Save to apply.")
    }

    companion object {
        val COLORS = listOf("" to "Accent", "#FF3FA9F5" to "Blue", "#FF4CD964" to "Green", "#FFFF9500" to "Orange", "#FFE81123" to "Red", "#FFB4009E" to "Purple", "#FFFFFFFF" to "White", "#FF00B7C3" to "Cyan")
    }
}
