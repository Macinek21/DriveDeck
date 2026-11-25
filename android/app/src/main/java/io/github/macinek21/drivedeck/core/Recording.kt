package io.github.macinek21.drivedeck.core

import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.DateTimeParseException
import java.util.Locale
import kotlin.math.abs
import kotlin.math.asin
import kotlin.math.cos
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.sqrt

/** One loop clip. Times are epoch ms; size includes the .srt/.gpx next to the video. */
class Segment(val name: String, val start: Long, var end: Long = 0, var size: Long = 0, var inProgress: Boolean = false, val root: String = "")

object Names {
    private val F = DateTimeFormatter.ofPattern("yyyyMMdd_HHmmss", Locale.ROOT)
    private val OURS = Regex("""^\d{8}_\d{6}(_(impact|manual))?( \(\d{1,3}\))?$""")

    fun of(t: Long, zone: ZoneId = ZoneId.systemDefault()): String = F.format(Instant.ofEpochMilli(t).atZone(zone))

    /** Only names DriveDeck itself generates; anything else in the folder is never touched. */
    fun parse(n: String, zone: ZoneId = ZoneId.systemDefault()): Long? {
        if (!OURS.matches(n)) return null
        return try { LocalDateTime.parse(n.substring(0, 15), F).atZone(zone).toInstant().toEpochMilli() } catch (_: DateTimeParseException) { null }
    }
}

object LoopPolicy {
    /** Oldest finished clips to delete so the loop fits [cap] (null = unlimited) and [reserve] stays free once [incoming] bytes are written. */
    fun toDelete(all: List<Segment>, cap: Long?, freeNow: Long, reserve: Long, incoming: Long): List<Segment> {
        var total = all.sumOf { it.size }
        var free = freeNow
        val out = mutableListOf<Segment>()
        for (s in all.filter { !it.inProgress }.sortedBy { it.start }) {
            if (!((cap != null && total + incoming > cap) || free < reserve + incoming)) break
            out += s
            total -= s.size
            free += s.size
        }
        return out
    }

    fun estimate(bitrate: Int, audio: Boolean, seconds: Int) = (bitrate.toLong() + if (audio) 128_000 else 0) / 8 * seconds * 11 / 10

    /** Clips overlapping [time - pre, until]. */
    fun forEvent(all: List<Segment>, time: Long, preMs: Long, until: Long) =
        all.filter { it.start <= until && (if (it.end == 0L) Long.MAX_VALUE else it.end) >= time - preMs }.sortedBy { it.start }
}

/** Detects impacts from raw accelerometer samples in g; gravity is tracked and removed so the mounting angle does not matter. */
class ImpactDetector(var threshold: Double) {
    private var g: DoubleArray? = null
    private var started = 0L
    private var lastT = 0L
    private var lastImpact = Long.MIN_VALUE / 2
    var lastG = 0.0
        private set

    /** [t] in ms, monotonic. True exactly when a new impact is detected. */
    fun feed(x: Double, y: Double, z: Double, t: Long): Boolean {
        if (x.isNaN() || y.isNaN() || z.isNaN()) return false
        val gr = g ?: run { g = doubleArrayOf(x, y, z); started = t; lastT = t; return false }
        // ~1 s time constant regardless of the sensor's sample rate
        val dt = (t - lastT).coerceIn(0, 200) / 1000.0
        lastT = t
        val a = dt / (1.0 + dt)
        gr[0] += a * (x - gr[0]); gr[1] += a * (y - gr[1]); gr[2] += a * (z - gr[2])
        lastG = sqrt((x - gr[0]) * (x - gr[0]) + (y - gr[1]) * (y - gr[1]) + (z - gr[2]) * (z - gr[2]))
        if (t - started < WARMUP || lastG < threshold || t - lastImpact < COOLDOWN) return false
        lastImpact = t
        return true
    }

    companion object {
        const val WARMUP = 3000L
        const val COOLDOWN = 10_000L
        fun preset(p: String) = when (p) { "high" -> 1.3; "low" -> 3.0; else -> 2.0 }
    }
}

class Fix(val time: Long, val lat: Double, val lon: Double, val alt: Double? = null, val speedKmh: Double? = null, val accuracy: Double? = null)

object Geo {
    fun distance(la1: Double, lo1: Double, la2: Double, lo2: Double): Double {
        val dLa = Math.toRadians(la2 - la1)
        val dLo = Math.toRadians(lo2 - lo1)
        val a = sin(dLa / 2) * sin(dLa / 2) + cos(Math.toRadians(la1)) * cos(Math.toRadians(la2)) * sin(dLo / 2) * sin(dLo / 2)
        return 2 * 6371000.0 * asin(min(1.0, sqrt(a)))
    }

    /** The receiver's own speed when present, otherwise from the distance to the previous fix. */
    fun speed(prev: Fix?, cur: Fix): Double? {
        cur.speedKmh?.let { if (!it.isNaN() && it >= 0) return it }
        val p = prev ?: return null
        val s = (cur.time - p.time) / 1000.0
        if (s <= 0.2 || s > 10 || (cur.accuracy ?: 0.0) > 50) return null
        return (distance(p.lat, p.lon, cur.lat, cur.lon) / s * 3.6).takeIf { it <= 400 }
    }
}

object Stamp {
    const val MAX_LABEL = 24
    private val F = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss", Locale.ROOT)
    fun cleanLabel(s: String?) = (s ?: "").filter { !it.isISOControl() }.take(MAX_LABEL).trim()

    fun text(now: Long, fix: Fix?, speed: Double?, date: Boolean, showSpeed: Boolean, coords: Boolean, label: String, zone: ZoneId = ZoneId.systemDefault()): String {
        val p = mutableListOf<String>()
        if (date) p += F.format(Instant.ofEpochMilli(now).atZone(zone))
        if (showSpeed) p += (speed?.roundToInt()?.toString() ?: "--") + " km/h"
        if (coords) p += if (fix == null) "no GPS" else
            (if (fix.lat >= 0) "N" else "S") + "%.5f".format(Locale.ROOT, abs(fix.lat)) + " " + (if (fix.lon >= 0) "E" else "W") + "%.5f".format(Locale.ROOT, abs(fix.lon))
        cleanLabel(label).let { if (it.isNotEmpty()) p += it }
        return p.joinToString("   ")
    }
}

/** .srt subtitle track relative to the moment video really started. */
class Srt(private val start: Long) {
    private val sb = StringBuilder()
    private var n = 0

    fun add(from: Long, to: Long, text: String) {
        if (to <= from || to <= start) return
        sb.append(++n).append("\r\n").append(ts(maxOf(0, from - start))).append(" --> ").append(ts(to - start)).append("\r\n")
            .append(text.replace('\r', ' ').replace('\n', ' ')).append("\r\n\r\n")
    }

    override fun toString() = sb.toString()

    class Cue(val from: Long, val to: Long, val text: String)

    companion object {
        private fun ts(ms: Long) = "%02d:%02d:%02d,%03d".format(Locale.ROOT, ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000)
        private val TIME = Regex("""(\d+):(\d\d):(\d\d),(\d{3}) --> (\d+):(\d\d):(\d\d),(\d{3})""")

        fun parse(s: String): List<Cue> = s.take(1_000_000).split(Regex("\r?\n\r?\n")).mapNotNull { block ->
            val m = TIME.find(block) ?: return@mapNotNull null
            val v = m.groupValues.drop(1).map { it.toLongOrNull() ?: return@mapNotNull null }
            Cue(((v[0] * 60 + v[1]) * 60 + v[2]) * 1000 + v[3], ((v[4] * 60 + v[5]) * 60 + v[6]) * 1000 + v[7],
                block.substring(m.range.last + 1).trim())
        }
    }
}

object Gpx {
    fun build(name: String, fixes: List<Fix>) = buildString {
        append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<gpx version=\"1.1\" creator=\"DriveDeck\" xmlns=\"http://www.topografix.com/GPX/1/1\">\r\n<trk><name>")
        append(escape(name)).append("</name><trkseg>\r\n")
        for (f in fixes) {
            append("<trkpt lat=\"").append("%.6f".format(Locale.ROOT, f.lat)).append("\" lon=\"").append("%.6f".format(Locale.ROOT, f.lon)).append("\">")
            f.alt?.let { append("<ele>").append("%.1f".format(Locale.ROOT, it)).append("</ele>") }
            append("<time>").append(Instant.ofEpochSecond(f.time / 1000)).append("</time>")
            f.speedKmh?.let { append("<extensions><speed>").append("%.2f".format(Locale.ROOT, it / 3.6)).append("</speed></extensions>") }
            append("</trkpt>\r\n")
        }
        append("</trkseg></trk>\r\n</gpx>\r\n")
    }

    private fun escape(s: String) = s.filter { !it.isISOControl() }.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
}
