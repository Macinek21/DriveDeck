package io.github.macinek21.drivedeck.core

import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import java.util.UUID

enum class Kind(val label: String, val needsValue: Boolean) {
    NUMBER("Number", true), GAUGE("Dial gauge", true), BAR("Bar", true), GRAPH("Graph", true),
    CAMERA("Camera preview", false), CLOCK("Clock", false), STATUS("Status", false)
}

private fun newId() = UUID.randomUUID().toString().take(8)

class Tile(
    var kind: Kind, var key: String = "", var row: Int = 0, var col: Int = 0, var rows: Int = 1, var cols: Int = 1,
    var label: String = "", var color: String = "", var warnAbove: Double? = null, var alarmAbove: Double? = null,
    var warnBelow: Double? = null, var min: Double? = null, var max: Double? = null, var id: String = newId(),
)

class DashPage(var name: String = "Page", var rows: Int = 3, var cols: Int = 4, var orientation: String = "any", val tiles: MutableList<Tile> = mutableListOf())

class CamSettings {
    var enabled = true
    var autoStart = true
    var resolution = "1080p"
    var fps = 30
    var bitrateMbps = 10
    var codec = "h264"
    var storage = "device"
    var audio = false
    var segmentSeconds = 60
    var unlimited = false
    var capGb = 8.0
    var reserveGb = 1.5
    var gps = true
    var stamp = true
    var stampDate = true
    var stampSpeed = true
    var stampCoords = false
    var label = ""
    var srt = true
    var gpx = true
    var impact = true
    var sensitivity = "medium"
    var thresholdG = 2.0
    var preSeconds = 60
    var postSeconds = 30
    var offerCall = true
    var emergency = "112"
    val impactG get() = if (sensitivity == "custom") thresholdG else ImpactDetector.preset(sensitivity)
}

class ObdSettings {
    var enabled = true
    var address = ""
    var name = ""
    var protocol = "6"
    var fuel = "petrol"
    var dtcSeconds = 30
    var permanent = false
    var custom = mutableListOf<ValueDef>()
}

class UiSettings {
    var notices = "top"
    var collapseSeconds = 8
    var keepOn = true
    var dim = false
    var accent = "#FF3FA9F5"
}

class Settings {
    var cam = CamSettings()
    var obd = ObdSettings()
    var ui = UiSettings()
    var pages = defaultPages()

    fun copy(): Settings = fromJson(toJson()).settings!!

    fun toJson(): String = JSONObject()
        .put("version", 1)
        .put("cam", JSONObject().apply {
            put("enabled", cam.enabled); put("autoStart", cam.autoStart); put("resolution", cam.resolution); put("fps", cam.fps)
            put("bitrateMbps", cam.bitrateMbps); put("codec", cam.codec); put("storage", cam.storage); put("audio", cam.audio); put("segmentSeconds", cam.segmentSeconds)
            put("unlimited", cam.unlimited); num("capGb", cam.capGb); num("reserveGb", cam.reserveGb); put("gps", cam.gps)
            put("stamp", cam.stamp); put("stampDate", cam.stampDate); put("stampSpeed", cam.stampSpeed); put("stampCoords", cam.stampCoords)
            put("label", cam.label); put("srt", cam.srt); put("gpx", cam.gpx); put("impact", cam.impact); put("sensitivity", cam.sensitivity)
            num("thresholdG", cam.thresholdG); put("preSeconds", cam.preSeconds); put("postSeconds", cam.postSeconds)
            put("offerCall", cam.offerCall); put("emergency", cam.emergency)
        })
        .put("obd", JSONObject().apply {
            put("enabled", obd.enabled); put("address", obd.address); put("name", obd.name); put("protocol", obd.protocol); put("fuel", obd.fuel)
            put("dtcSeconds", obd.dtcSeconds); put("permanent", obd.permanent)
            put("custom", JSONArray(obd.custom.map { v ->
                JSONObject().apply {
                    put("key", v.key); put("name", v.name); put("short", v.short); put("unit", v.unit); put("request", v.request); put("header", v.header)
                    put("formula", v.formula); num("min", v.min); num("max", v.max); put("decimals", v.decimals); put("priority", v.priority)
                }
            }))
        })
        .put("ui", JSONObject().put("notices", ui.notices).put("collapseSeconds", ui.collapseSeconds).put("keepOn", ui.keepOn).put("dim", ui.dim).put("accent", ui.accent))
        .put("pages", JSONArray(pages.map { p ->
            JSONObject().put("name", p.name).put("rows", p.rows).put("cols", p.cols).put("orientation", p.orientation)
                .put("tiles", JSONArray(p.tiles.map { t ->
                    JSONObject().apply {
                        put("id", t.id); put("kind", t.kind.name); put("key", t.key); put("row", t.row); put("col", t.col); put("rows", t.rows); put("cols", t.cols)
                        put("label", t.label); put("color", t.color); num("warnAbove", t.warnAbove); num("alarmAbove", t.alarmAbove)
                        num("warnBelow", t.warnBelow); num("min", t.min); num("max", t.max)
                    }
                }))
        }))
        .toString()

    /** Forces every value into safe bounds; returns what had to be corrected. Nothing in settings is ever executed. */
    fun normalize(): List<String> {
        val notes = mutableListOf<String>()
        fun oneOf(v: String, fallback: String, vararg ok: String) = if (v in ok) v else fallback
        fun clamp(v: Double, lo: Double, hi: Double, fb: Double) = if (v.isNaN() || v.isInfinite()) fb else v.coerceIn(lo, hi)
        fun fin(v: Double?) = v?.takeIf { !it.isNaN() && !it.isInfinite() }
        cam.apply {
            resolution = if (resolution in Video.RES) resolution else "1080p"
            fps = if (fps in Video.FPS) fps else 30
            bitrateMbps = bitrateMbps.coerceIn(2, Video.MAX_MBIT)
            codec = oneOf(codec, "h264", "h264", "hevc")
            storage = oneOf(storage, "device", "device", "card")
            segmentSeconds = segmentSeconds.coerceIn(30, 300)
            capGb = clamp(capGb, 1.0, 1024.0, 8.0)
            reserveGb = clamp(reserveGb, 0.5, 16.0, 1.5)
            label = Stamp.cleanLabel(label)
            sensitivity = oneOf(sensitivity, "medium", "high", "medium", "low", "custom")
            thresholdG = clamp(thresholdG, 0.8, 8.0, 2.0)
            preSeconds = preSeconds.coerceIn(10, 180)
            postSeconds = postSeconds.coerceIn(10, 120)
            emergency = oneOf(emergency, "112", "112", "150", "155", "158", "911", "999")
        }
        obd.apply {
            if (address.isNotEmpty() && !ADDRESS.matches(address)) { address = ""; name = ""; notes += "Invalid adapter address removed" }
            name = Stamp.cleanLabel(name)
            protocol = oneOf(protocol, "6", "0", "6", "7", "8", "9")
            fuel = oneOf(fuel, "petrol", "petrol", "diesel")
            dtcSeconds = dtcSeconds.coerceIn(10, 600)
            custom = custom.take(MAX_CUSTOM).mapNotNull { v ->
                val (ok, err) = cleanCustom(v)
                if (ok == null) notes += "Custom value '${Stamp.cleanLabel(v.name)}' removed: $err"
                ok
            }.distinctBy { it.key }.toMutableList()
        }
        ui.apply {
            notices = oneOf(notices, "top", "top", "bottom")
            collapseSeconds = collapseSeconds.coerceIn(0, 120)
            if (!isColor(accent)) accent = "#FF3FA9F5"
        }
        pages = pages.take(MAX_PAGES).toMutableList()
        if (pages.isEmpty()) { pages = defaultPages(); notes += "Dashboard layout reset" }
        val keys = Pids.all(obd.custom).map { it.key }.toSet()
        val ids = HashSet<String>()
        for (p in pages) {
            p.name = Stamp.cleanLabel(p.name).ifEmpty { "Page" }
            p.rows = p.rows.coerceIn(1, 6)
            p.cols = p.cols.coerceIn(1, 6)
            p.orientation = oneOf(p.orientation, "any", "any", "landscape", "portrait")
            while (p.tiles.size > MAX_TILES) p.tiles.removeAt(p.tiles.lastIndex)
            for (t in p.tiles) {
                t.row = t.row.coerceIn(0, p.rows - 1)
                t.col = t.col.coerceIn(0, p.cols - 1)
                t.rows = t.rows.coerceIn(1, p.rows - t.row)
                t.cols = t.cols.coerceIn(1, p.cols - t.col)
                t.label = Stamp.cleanLabel(t.label)
                if (!isColor(t.color)) t.color = ""
                t.warnAbove = fin(t.warnAbove); t.alarmAbove = fin(t.alarmAbove); t.warnBelow = fin(t.warnBelow); t.min = fin(t.min); t.max = fin(t.max)
                if (t.kind.needsValue && t.key !in keys) { t.key = Pids.GPS_SPEED; notes += "A tile on '${p.name}' had an unknown value" }
                if (t.id.length !in 1..16 || !ids.add(t.id)) { t.id = newId(); ids += t.id }
            }
        }
        return notes
    }

    class Parsed(val settings: Settings?, val notes: List<String>, val error: String?)

    companion object {
        const val MAX_PAGES = 8
        const val MAX_TILES = 24
        const val MAX_CUSTOM = 40
        const val MAX_CHARS = 256 * 1024
        private const val MAX_NESTING = 16
        private val ADDRESS = Regex("([0-9A-F]{2}:){5}[0-9A-F]{2}")
        private val COLOR = Regex("#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})")
        fun isColor(s: String) = COLOR.matches(s)

        private fun JSONObject.num(k: String, v: Double?) { if (v != null && !v.isNaN() && !v.isInfinite()) put(k, v) }
        private fun JSONObject.s(k: String, d: String) = if (has(k) && !isNull(k)) optString(k, d) else d
        private fun JSONObject.d(k: String): Double? = if (has(k) && !isNull(k)) optDouble(k, Double.NaN).takeIf { !it.isNaN() } else null
        private fun JSONArray?.objects(limit: Int): List<JSONObject> = if (this == null) emptyList() else (0 until minOf(length(), limit)).mapNotNull { optJSONObject(it) }

        /** Deeply nested JSON could exhaust the stack of the recursive parser, so it is refused up front. */
        private fun tooNested(s: String): Boolean {
            var depth = 0
            for (c in s) {
                if (c == '{' || c == '[') { if (++depth > MAX_NESTING) return true } else if (c == '}' || c == ']') depth--
            }
            return false
        }

        fun fromJson(text: String): Parsed {
            if (text.isBlank()) return Parsed(null, emptyList(), "File is empty.")
            if (text.length > MAX_CHARS || tooNested(text)) return Parsed(null, emptyList(), "Not a DriveDeck settings file.")
            return try {
                val o = JSONObject(text)
                if (!o.has("cam") && !o.has("pages") && !o.has("obd")) return Parsed(null, emptyList(), "Not a DriveDeck settings file.")
                val r = Settings()
                o.optJSONObject("cam")?.let { j ->
                    r.cam.apply {
                        enabled = j.optBoolean("enabled", enabled); autoStart = j.optBoolean("autoStart", autoStart); resolution = j.s("resolution", resolution)
                        fps = j.optInt("fps", fps); bitrateMbps = j.optInt("bitrateMbps", bitrateMbps); codec = j.s("codec", codec); storage = j.s("storage", storage); audio = j.optBoolean("audio", audio)
                        segmentSeconds = j.optInt("segmentSeconds", segmentSeconds); unlimited = j.optBoolean("unlimited", unlimited)
                        capGb = j.d("capGb") ?: capGb; reserveGb = j.d("reserveGb") ?: reserveGb; gps = j.optBoolean("gps", gps)
                        stamp = j.optBoolean("stamp", stamp); stampDate = j.optBoolean("stampDate", stampDate); stampSpeed = j.optBoolean("stampSpeed", stampSpeed)
                        stampCoords = j.optBoolean("stampCoords", stampCoords); label = j.s("label", label); srt = j.optBoolean("srt", srt); gpx = j.optBoolean("gpx", gpx)
                        impact = j.optBoolean("impact", impact); sensitivity = j.s("sensitivity", sensitivity); thresholdG = j.d("thresholdG") ?: thresholdG
                        preSeconds = j.optInt("preSeconds", preSeconds); postSeconds = j.optInt("postSeconds", postSeconds)
                        offerCall = j.optBoolean("offerCall", offerCall); emergency = j.s("emergency", emergency)
                    }
                }
                o.optJSONObject("obd")?.let { j ->
                    r.obd.apply {
                        enabled = j.optBoolean("enabled", enabled); address = j.s("address", address); name = j.s("name", name); protocol = j.s("protocol", protocol)
                        fuel = j.s("fuel", fuel); dtcSeconds = j.optInt("dtcSeconds", dtcSeconds); permanent = j.optBoolean("permanent", permanent)
                        custom = j.optJSONArray("custom").objects(MAX_CUSTOM).map { v ->
                            ValueDef(v.s("key", ""), v.s("name", ""), v.s("short", ""), v.s("unit", ""), v.s("request", ""), v.s("header", ""), v.s("formula", ""),
                                v.d("min") ?: 0.0, v.d("max") ?: 100.0, v.optInt("decimals"), v.optInt("priority", 1))
                        }.toMutableList()
                    }
                }
                o.optJSONObject("ui")?.let { j ->
                    r.ui.apply {
                        notices = j.s("notices", notices); collapseSeconds = j.optInt("collapseSeconds", collapseSeconds)
                        keepOn = j.optBoolean("keepOn", keepOn); dim = j.optBoolean("dim", dim); accent = j.s("accent", accent)
                    }
                }
                o.optJSONArray("pages")?.let { a ->
                    r.pages = a.objects(MAX_PAGES).map { j ->
                        DashPage(j.s("name", "Page"), j.optInt("rows", 3), j.optInt("cols", 4), j.s("orientation", "any"),
                            j.optJSONArray("tiles").objects(MAX_TILES).map { t ->
                                Tile(Kind.entries.firstOrNull { it.name == t.s("kind", "") } ?: Kind.NUMBER, t.s("key", ""), t.optInt("row"), t.optInt("col"),
                                    t.optInt("rows", 1), t.optInt("cols", 1), t.s("label", ""), t.s("color", ""), t.d("warnAbove"), t.d("alarmAbove"),
                                    t.d("warnBelow"), t.d("min"), t.d("max"), t.s("id", ""))
                            }.toMutableList())
                    }.toMutableList()
                }
                Parsed(r, r.normalize(), null)
            } catch (_: JSONException) {
                Parsed(null, emptyList(), "The file is not valid JSON.")
            }
        }

        /** Validated copy of a user-defined value, or the reason it is refused. */
        fun cleanCustom(v: ValueDef): Pair<ValueDef?, String?> {
            val name = Stamp.cleanLabel(v.name)
            if (name.isEmpty()) return null to "Name is required."
            CommandGuard.requestError(v.request)?.let { return null to it }
            val header = v.header.trim().uppercase()
            CommandGuard.headerError(header)?.let { return null to it }
            Formula.error(v.formula)?.let { return null to it }
            val lo = if (v.min.isNaN() || v.min.isInfinite()) 0.0 else v.min
            val hi = if (v.max.isNaN() || v.max.isInfinite() || v.max <= lo) lo + 100 else v.max
            val key = if (v.key.startsWith("custom:") && v.key.length in 8..40) v.key else "custom:" + newId()
            return v.copy(key = key, name = name, short = Stamp.cleanLabel(v.short).ifEmpty { name }, unit = Stamp.cleanLabel(v.unit).take(8),
                request = Hex.format(Hex.parse(v.request)!!), header = header, formula = v.formula.trim(), min = lo, max = hi,
                decimals = v.decimals.coerceIn(0, 3), priority = v.priority.coerceIn(0, 2)) to null
        }

        fun defaultPages() = mutableListOf(
            DashPage("Drive", 3, 4, "landscape", mutableListOf(
                Tile(Kind.CAMERA, row = 0, col = 0, rows = 2, cols = 2),
                Tile(Kind.GAUGE, "obd:0C", 0, 2, 2, 1, warnAbove = 5500.0, alarmAbove = 6300.0),
                Tile(Kind.NUMBER, Pids.GPS_SPEED, 0, 3, 2, 1),
                Tile(Kind.NUMBER, "obd:05", 2, 0, warnAbove = 105.0, alarmAbove = 115.0),
                Tile(Kind.NUMBER, Pids.BOOST, 2, 1),
                Tile(Kind.NUMBER, Pids.VOLTAGE, 2, 2, warnBelow = 12.0),
                Tile(Kind.STATUS, row = 2, col = 3))),
            DashPage("Engine", 3, 3, "any", mutableListOf(
                Tile(Kind.GRAPH, Pids.BOOST, 0, 0, 1, 2), Tile(Kind.BAR, "obd:04", 0, 2), Tile(Kind.NUMBER, "obd:0F", 1, 0),
                Tile(Kind.NUMBER, "obd:0E", 1, 1), Tile(Kind.NUMBER, "obd:06", 1, 2), Tile(Kind.NUMBER, Pids.CONSUMPTION, 2, 0),
                Tile(Kind.BAR, "obd:2F", 2, 1, warnBelow = 12.0), Tile(Kind.NUMBER, "obd:46", 2, 2))),
            DashPage("Portrait", 5, 2, "portrait", mutableListOf(
                Tile(Kind.CAMERA, row = 0, col = 0, rows = 2, cols = 2), Tile(Kind.NUMBER, Pids.GPS_SPEED, 2, 0), Tile(Kind.GAUGE, "obd:0C", 2, 1),
                Tile(Kind.NUMBER, "obd:05", 3, 0, warnAbove = 105.0, alarmAbove = 115.0), Tile(Kind.NUMBER, Pids.VOLTAGE, 3, 1),
                Tile(Kind.CLOCK, row = 4, col = 0), Tile(Kind.STATUS, row = 4, col = 1))),
        ).also { it.sortBy { p -> if (p.name == "Engine") 1 else 0 } }
    }
}
