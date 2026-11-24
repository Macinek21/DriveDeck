package io.github.macinek21.drivedeck.svc

import android.Manifest
import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.os.BatteryManager
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.os.SystemClock
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import androidx.lifecycle.LifecycleService
import io.github.macinek21.drivedeck.R
import io.github.macinek21.drivedeck.core.Dtc
import io.github.macinek21.drivedeck.core.DtcKind
import io.github.macinek21.drivedeck.core.Fix
import io.github.macinek21.drivedeck.core.Geo
import io.github.macinek21.drivedeck.core.ImpactDetector
import io.github.macinek21.drivedeck.core.ObdPoller
import io.github.macinek21.drivedeck.core.ObdState
import io.github.macinek21.drivedeck.core.Pids
import io.github.macinek21.drivedeck.core.Settings
import io.github.macinek21.drivedeck.ui.MainActivity

/** Foreground service: keeps dashcam, GPS, impact sensor and OBD running while the screen is off or another app is in front. */
class DriveService : LifecycleService() {
    lateinit var cam: Dashcam
        private set
    private var fix: Fix? = null
    private var fixAt = 0L
    private var speed: Double? = null
    private var obd: ObdPoller? = null
    private var obdKey = ""
    private var detector: ImpactDetector? = null
    private var gpsOn = false
    private var started = false
    private var closing = false
    private val alarmed = HashMap<String, Boolean>()
    private val wake by lazy { getSystemService(PowerManager::class.java).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "DriveDeck:drive").apply { setReferenceCounted(false) } }
    private val sensors by lazy { getSystemService(SensorManager::class.java) }
    private val locations by lazy { getSystemService(LocationManager::class.java) }
    private var thermal: PowerManager.OnThermalStatusChangedListener? = null

    private fun granted(p: String) = ContextCompat.checkSelfPermission(this, p) == PackageManager.PERMISSION_GRANTED
    private fun freshFix() = fix?.takeIf { SystemClock.elapsedRealtime() - fixAt < 5000 }
    private fun freshSpeed() = if (freshFix() != null) speed else null

    override fun onCreate() {
        super.onCreate()
        Hub.load(this)
        cam = Dashcam(this, this, ::freshFix, ::freshSpeed)
        cam.onChange = ::updateNotification
        instance = this
        registerReceiver(battery, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        if (Build.VERSION.SDK_INT >= 29) getSystemService(PowerManager::class.java)?.let { pm ->
            thermal = PowerManager.OnThermalStatusChangedListener { st ->
                AppLog.i("Thermal status $st")
                Hub.main.post { if (st >= PowerManager.THERMAL_STATUS_CRITICAL) cam.pauseForHeat(true) else if (st <= PowerManager.THERMAL_STATUS_MODERATE) cam.pauseForHeat(false) }
            }.also { pm.addThermalStatusListener(it) }
        }
        Hub.main.post(alarmTick)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        if (!goForeground()) { stopSelf(); return START_NOT_STICKY }
        when (intent?.action) {
            ACTION_STOP -> shutdown()
            ACTION_SAVE -> saveIncident()
            else -> begin()
        }
        return START_NOT_STICKY // Android does not let a restarted background service use the camera
    }

    /** Foreground types must match the permissions that are granted right now. */
    private fun goForeground(): Boolean {
        val type = types(this)
        if (type == 0) return false
        return try {
            ServiceCompat.startForeground(this, 1, notification(), if (Build.VERSION.SDK_INT >= 30) type else if (Build.VERSION.SDK_INT == 29) type and LEGACY_TYPES else 0)
            true
        } catch (e: Exception) {
            AppLog.e("startForeground", e)
            false
        }
    }

    /** The wake lock lasts as long as the service: with the screen off the phone must keep recording and watching for impacts. */
    @SuppressLint("WakelockTimeout")
    private fun begin() {
        wake.acquire()
        startSensors()
        startObd()
        if (!started) {
            started = true
            AppLog.i("Service started, Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT}), ${Build.MANUFACTURER} ${Build.MODEL}")
            if (Hub.settings.cam.enabled && Hub.settings.cam.autoStart) cam.start() else cam.openPreview()
        } else if (cam.isWanted) cam.start()
        updateNotification()
    }

    fun applySettings(previous: Settings) {
        startSensors()
        startObd()
        goForeground()
        cam.apply(previous.cam)
        updateNotification()
    }

    fun saveIncident() {
        cam.trigger("manual", Hub.gForce)
        val c = Hub.settings.cam
        Hub.post("manual", Severity.INFO, "Saving incident", "Video from ${c.preSeconds} s before until ${c.postSeconds} s after now")
    }

    fun shutdown() {
        if (closing) return
        closing = true
        cam.close {
            ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    override fun onTaskRemoved(rootIntent: Intent?) {
        super.onTaskRemoved(rootIntent)
        shutdown()
    }

    override fun onDestroy() {
        instance = null
        Hub.main.removeCallbacks(alarmTick)
        obd?.stop()
        obd = null
        stopSensors()
        if (wake.isHeld) wake.release()
        runCatching { unregisterReceiver(battery) }
        if (Build.VERSION.SDK_INT >= 29) thermal?.let { getSystemService(PowerManager::class.java)?.removeThermalStatusListener(it) }
        if (!closing) cam.close {}
        Hub.obdConnected = false
        Hub.obdStatus = "off"
        AppLog.i("Service stopped")
        super.onDestroy()
    }

    // ---------------- GPS, impact, heat ----------------

    private val onLocation = object : LocationListener {
        override fun onLocationChanged(l: Location) {
            val f = Fix(System.currentTimeMillis(), l.latitude, l.longitude, if (l.hasAltitude()) l.altitude else null,
                if (l.hasSpeed()) l.speed * 3.6 else null, if (l.hasAccuracy()) l.accuracy.toDouble() else null)
            speed = Geo.speed(fix, f)?.let { if (it < 2) 0.0 else it }
            fix = f
            fixAt = SystemClock.elapsedRealtime()
            speed?.let { Hub.values[Pids.GPS_SPEED] = Sample(it) }
            f.alt?.let { Hub.values[Pids.GPS_ALT] = Sample(it) }
            Hub.gpsStatus = f.accuracy?.let { "±${it.toInt()} m" } ?: "fix"
        }

        override fun onProviderEnabled(provider: String) { Hub.gpsStatus = "searching" }
        override fun onProviderDisabled(provider: String) { Hub.gpsStatus = "location is off" }
        @Deprecated("Required on Android 8-10") override fun onStatusChanged(provider: String?, status: Int, extras: Bundle?) {}
    }

    private val onMotion = object : SensorEventListener {
        override fun onSensorChanged(e: SensorEvent) {
            val d = detector ?: return
            val hit = d.feed(e.values[0] / G, e.values[1] / G, e.values[2] / G, e.timestamp / 1_000_000)
            Hub.gForce = d.lastG
            Hub.values[Pids.GFORCE] = Sample(d.lastG)
            if (hit && Hub.settings.cam.impact) {
                val g = d.lastG
                AppLog.i("Impact detected")
                cam.trigger("impact", g)
                Hub.post("impact", Severity.ALARM, "Impact detected", "Footage is being saved")
                Hub.onImpact?.invoke(g)
            }
        }

        override fun onAccuracyChanged(s: Sensor?, a: Int) {}
    }

    @SuppressLint("MissingPermission")
    private fun startSensors() {
        stopSensors()
        val c = Hub.settings.cam
        if (c.gps && granted(Manifest.permission.ACCESS_FINE_LOCATION)) {
            try {
                if (LocationManager.GPS_PROVIDER in locations.allProviders) {
                    locations.requestLocationUpdates(LocationManager.GPS_PROVIDER, 1000, 0f, onLocation, mainLooper)
                    gpsOn = true
                    Hub.gpsStatus = if (locations.isProviderEnabled(LocationManager.GPS_PROVIDER)) "searching" else "location is off"
                } else Hub.gpsStatus = "no GPS"
            } catch (e: Exception) { AppLog.e("gps", e); Hub.gpsStatus = "error" }
        } else Hub.gpsStatus = if (c.gps) "no permission" else "off"
        // The detector also feeds the G-force tile, so it runs even when automatic saving is off.
        val acc = sensors.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
        if (acc != null) {
            detector = ImpactDetector(c.impactG)
            sensors.registerListener(onMotion, acc, 10_000)
        } else if (c.impact) Hub.post("acc", Severity.WARNING, "No accelerometer", "Impact detection is unavailable")
    }

    private fun stopSensors() {
        if (gpsOn) locations.removeUpdates(onLocation)
        gpsOn = false
        sensors.unregisterListener(onMotion)
        detector = null
    }

    /** Android 8/9 have no thermal API, so the battery temperature is used on every version. */
    private val battery = object : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            val t = i.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0) / 10.0
            if (t >= 50) cam.pauseForHeat(true)
            else if (t in 1.0..45.0 && (Build.VERSION.SDK_INT < 29 || (getSystemService(PowerManager::class.java)?.currentThermalStatus ?: 0) <= PowerManager.THERMAL_STATUS_MODERATE)) cam.pauseForHeat(false)
        }
    }

    // ---------------- OBD ----------------

    private fun startObd() {
        val o = Hub.settings.obd
        val permitted = BluetoothTransport.permitted(this)
        val key = listOf(o.enabled, o.address, o.protocol, o.fuel, o.dtcSeconds, o.permanent, o.custom, permitted).joinToString("|")
        if (key == obdKey && obd != null) return
        obdKey = key
        obd?.stop()
        obd = null
        Hub.unsupported.clear()
        Hub.dtcs = emptySet()
        Hub.obdConnected = false
        Hub.remove { it.key.startsWith("dtc:") }
        Hub.obdStatus = if (!o.enabled) "off" else if (o.address.isEmpty()) "no adapter selected" else "connecting"
        if (!o.enabled || o.address.isEmpty()) return
        val app = applicationContext
        lateinit var self: ObdPoller
        val l = object : ObdPoller.Listener {
            private fun live() = obd === self
            override fun onValue(key: String, v: Double) { if (live()) Hub.values[key] = Sample(v) }
            override fun onUnsupported(key: String) { if (live()) Hub.unsupported += key }
            override fun onState(s: ObdState, msg: String) {
                if (!live()) return
                Hub.obdStatus = msg
                Hub.obdConnected = s == ObdState.CONNECTED
                if (s == ObdState.CONNECTED) { Hub.unsupported.clear(); Hub.post("obd", Severity.INFO, "OBD connected", msg) }
                if (s == ObdState.FAILED) Hub.post("obd", Severity.WARNING, "OBD adapter not reachable", msg, 120_000)
            }

            override fun onDtcs(all: Set<Dtc>, fresh: Set<Dtc>) {
                if (!live()) return
                Hub.dtcs = all
                val keys = all.map { dtcKey(it) }.toSet()
                Hub.remove { it.key.startsWith("dtc:") && it.key !in keys }
                for (d in fresh) Hub.post(dtcKey(d), if (d.kind == DtcKind.Pending) Severity.WARNING else Severity.ALARM,
                    "Fault ${d.code}" + (if (d.kind == DtcKind.Pending) " (pending)" else "") + (if (d.ecu.isEmpty()) "" else " – ${d.ecu}"), d.description)
            }
        }
        self = ObdPoller({ BluetoothTransport.connect(app, o.address) }, o.protocol, o.dtcSeconds, o.permanent, o.fuel == "petrol", l)
        obd = self
        updateWanted()
        self.start()
    }

    private fun dtcKey(d: Dtc) = "dtc:${d.code}${d.kind}${d.ecu}"

    /** Polls what is visible plus everything that has a warning threshold on any page. */
    fun updateWanted() {
        val keys = Hub.visibleKeys + Hub.settings.pages.flatMap { it.tiles }.filter { it.kind.needsValue && (it.alarmAbove != null || it.warnAbove != null || it.warnBelow != null) }.map { it.key }
        obd?.setWanted(Hub.allValues().filter { it.key in keys })
    }

    /** Thresholds are checked on every page, also while the dashboard is not on screen. */
    private val alarmTick = object : Runnable {
        override fun run() {
            val defs = Hub.allValues().associateBy { it.key }
            for (t in Hub.settings.pages.flatMap { it.tiles }) {
                val d = defs[t.key] ?: continue
                if (!t.kind.needsValue) continue
                val v = Hub.values[t.key]?.takeIf { !it.stale }?.v ?: continue
                val high = t.alarmAbove?.let { v >= it } == true
                val low = t.warnBelow?.let { v <= it } == true
                val id = "alarm:${t.id}"
                val was = alarmed[id] == true
                if ((high || low) && !was)
                    Hub.post(id, if (high) Severity.ALARM else Severity.WARNING, d.name + if (high) " too high" else " too low", "%.${d.decimals}f %s".format(v, d.unit), 120_000)
                alarmed[id] = high || low
            }
            Hub.main.postDelayed(this, 500)
        }
    }

    // ---------------- Notification ----------------

    private fun notification(): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel("drive", "Recording", NotificationManager.IMPORTANCE_LOW).apply { setShowBadge(false) })
        fun svc(action: String) = PendingIntent.getService(this, action.hashCode(), Intent(this, DriveService::class.java).setAction(action), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val text = if (cam.paused) "Paused: phone is too hot" else if (cam.recordingSince != null) "Recording" else "Not recording"
        return NotificationCompat.Builder(this, "drive").setSmallIcon(R.drawable.ic_stat).setContentTitle("DriveDeck").setContentText(text).setContentIntent(open)
            .setOngoing(true).setOnlyAlertOnce(true).setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .addAction(0, "Save incident", svc(ACTION_SAVE)).addAction(0, "Stop", svc(ACTION_STOP)).build()
    }

    private fun updateNotification() {
        if (closing || types(this) == 0) return
        try { getSystemService(NotificationManager::class.java).notify(1, notification()) } catch (_: SecurityException) {}
    }

    companion object {
        const val ACTION_STOP = "stop"
        const val ACTION_SAVE = "save"
        private const val G = 9.80665
        /** Types that exist on Android 10 (camera and microphone types came with 11). */
        private val LEGACY_TYPES = if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_LOCATION or ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE else 0

        /** Main thread only. */
        var instance: DriveService? = null
            private set

        /** Foreground-service types the current permissions and settings allow; 0 = nothing to run. */
        fun types(c: Context): Int {
            fun ok(p: String) = ContextCompat.checkSelfPermission(c, p) == PackageManager.PERMISSION_GRANTED
            var t = 0
            if (ok(Manifest.permission.CAMERA) && Hub.settings.cam.enabled) t = t or CAMERA
            if (ok(Manifest.permission.CAMERA) && Hub.settings.cam.enabled && Hub.settings.cam.audio && ok(Manifest.permission.RECORD_AUDIO)) t = t or MICROPHONE
            if (ok(Manifest.permission.ACCESS_FINE_LOCATION) && Hub.settings.cam.gps) t = t or LOCATION
            if (Hub.settings.obd.enabled && Hub.settings.obd.address.isNotEmpty() && BluetoothTransport.permitted(c)) t = t or DEVICE
            return t
        }

        private const val CAMERA = 64
        private const val MICROPHONE = 128
        private const val LOCATION = 8
        private const val DEVICE = 16

        /** Call only while an activity is visible. */
        fun start(c: Context) {
            if (types(c) == 0) return
            try { ContextCompat.startForegroundService(c, Intent(c, DriveService::class.java)) } catch (e: Exception) { AppLog.e("start service", e) }
        }
    }
}
