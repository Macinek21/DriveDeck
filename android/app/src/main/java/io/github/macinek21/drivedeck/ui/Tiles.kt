package io.github.macinek21.drivedeck.ui

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.view.View
import android.view.ViewGroup
import io.github.macinek21.drivedeck.core.DashPage
import io.github.macinek21.drivedeck.core.Kind
import io.github.macinek21.drivedeck.core.Tile
import io.github.macinek21.drivedeck.core.ValueDef
import io.github.macinek21.drivedeck.svc.DriveService
import io.github.macinek21.drivedeck.svc.Hub
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlin.math.min

object Palette {
    val tile = 0xFF14181E.toInt()
    val track = 0xFF2D343C.toInt()
    val dim = 0xFF96A0AA.toInt()
    val warn = 0xFFFFB900.toInt()
    val alarm = 0xFFE81123.toInt()
    val blue = 0xFF3FA9F5.toInt()
    fun parse(s: String, fallback: Int) = try { if (s.isEmpty()) fallback else Color.parseColor(s) } catch (_: IllegalArgumentException) { fallback }
}

/** One dashboard tile, drawn on a canvas. */
class TileView(c: Context, val tile: Tile, private val def: ValueDef?, accent: Int) : View(c) {
    private val d = resources.displayMetrics.density
    private val sp = d * resources.configuration.fontScale.coerceIn(0.85f, 1.3f)
    private val color = Palette.parse(tile.color, accent)
    private val lo = tile.min ?: def?.min ?: 0.0
    private val hi = (tile.max ?: def?.max ?: 100.0).let { if (it <= lo) lo + 1 else it }
    private val p = Paint(Paint.ANTI_ALIAS_FLAG)
    private val r = RectF()
    private val path = Path()
    private val history = ArrayDeque<Double>()
    private val clock = SimpleDateFormat("HH:mm", Locale.getDefault())
    private val date = SimpleDateFormat("EEE d.M.", Locale.getDefault())
    private val label = tile.label.ifEmpty { if (tile.kind.needsValue) def?.short ?: "?" else tile.kind.label }
    private var value = Double.NaN

    fun refresh() {
        if (tile.kind.needsValue) {
            value = def?.let { Hub.values[it.key] }?.takeIf { !it.stale }?.v ?: Double.NaN
            if (tile.kind == Kind.GRAPH) {
                history.addLast(value)
                while (history.size > 150) history.removeFirst()
            }
            contentDescription = "$label ${text()} ${def?.unit ?: ""}"
        }
        if (tile.kind != Kind.CAMERA) invalidate()
    }

    private fun text() = if (def != null && def.key in Hub.unsupported) "n/a" else if (value.isNaN()) "--" else "%.${def?.decimals ?: 0}f".format(value)

    private fun fit(s: String, w: Float, h: Float): Float {
        p.textSize = 100f
        return min(100f * w / p.measureText(s.ifEmpty { "0" }).coerceAtLeast(1f), h).coerceAtLeast(8f)
    }

    private fun centered(c: Canvas, s: String, cx: Float, cy: Float, w: Float, h: Float, col: Int) {
        p.style = Paint.Style.FILL
        p.color = col
        p.isFakeBoldText = true
        p.textAlign = Paint.Align.CENTER
        p.textSize = fit(s, w, h)
        c.drawText(s, cx, cy - (p.ascent() + p.descent()) / 2, p)
        p.isFakeBoldText = false
    }

    private fun small(c: Canvas, s: String, x: Float, y: Float, align: Paint.Align, size: Float = 13f) {
        p.style = Paint.Style.FILL
        p.color = Palette.dim
        p.textAlign = align
        p.textSize = size * sp
        c.drawText(s, x, y, p)
    }

    override fun onDraw(c: Canvas) {
        val w = width.toFloat()
        val h = height.toFloat()
        val m = 2 * d
        r.set(m, m, w - m, h - m)
        p.style = Paint.Style.FILL
        p.color = Palette.tile
        c.drawRoundRect(r, 6 * d, 6 * d, p)
        val v = value
        val state = when {
            v.isNaN() -> Color.WHITE
            tile.alarmAbove?.let { v >= it } == true -> Palette.alarm
            tile.warnAbove?.let { v >= it } == true || tile.warnBelow?.let { v <= it } == true -> Palette.warn
            else -> Color.WHITE
        }
        val fill = if (state == Color.WHITE) color else state
        val ratio = if (v.isNaN()) 0f else ((v - lo) / (hi - lo)).coerceIn(0.0, 1.0).toFloat()
        val top = 18 * sp
        when (tile.kind) {
            Kind.CAMERA -> small(c, "Camera", w / 2, h / 2, Paint.Align.CENTER)
            Kind.CLOCK -> {
                val now = Date()
                centered(c, clock.format(now), w / 2, h / 2, w * 0.8f, h * 0.5f, Color.WHITE)
                small(c, date.format(now), w - 8 * d, h - 8 * d, Paint.Align.RIGHT)
            }
            Kind.STATUS -> status(c, w, h)
            Kind.NUMBER, Kind.BAR -> {
                val bar = if (tile.kind == Kind.BAR) 18 * d else 0f
                val bottom = h - bar - 20 * sp
                centered(c, text(), w / 2, (top + bottom) / 2, w * 0.82f, (bottom - top).coerceAtLeast(12 * d), state)
                if (bar > 0) {
                    r.set(10 * d, h - bar - 4 * d, w - 10 * d, h - bar + 4 * d)
                    p.color = Palette.track
                    c.drawRoundRect(r, 4 * d, 4 * d, p)
                    r.right = r.left + (r.right - r.left) * ratio
                    p.color = fill
                    if (ratio > 0) c.drawRoundRect(r, 4 * d, 4 * d, p)
                }
                small(c, def?.unit ?: "", w - 8 * d, h - bar - 8 * d, Paint.Align.RIGHT, 12f)
            }
            Kind.GAUGE -> {
                val rad = (min(w, h - top) / 2 - 12 * d).coerceAtLeast(8 * d)
                val cx = w / 2
                val cy = top + (h - top) / 2 + rad * 0.1f
                r.set(cx - rad, cy - rad, cx + rad, cy + rad)
                p.style = Paint.Style.STROKE
                p.strokeCap = Paint.Cap.ROUND
                p.strokeWidth = (rad * 0.14f).coerceAtLeast(3 * d)
                p.color = Palette.track
                c.drawArc(r, 150f, 240f, false, p)
                p.color = fill
                if (!v.isNaN()) c.drawArc(r, 150f, (240f * ratio).coerceAtLeast(0.5f), false, p)
                centered(c, text(), cx, cy, rad * 1.3f, rad * 0.6f, state)
                small(c, def?.unit ?: "", cx, cy + rad * 0.75f, Paint.Align.CENTER, 12f)
            }
            Kind.GRAPH -> {
                path.reset()
                var pen = false
                val gx = 8 * d
                val gy = top + 6 * d
                val gw = w - 16 * d
                val gh = h - gy - 8 * d
                history.forEachIndexed { i, x ->
                    if (x.isNaN()) { pen = false; return@forEachIndexed }
                    val px = gx + gw * i / 149f
                    val py = gy + gh * (1 - ((x - lo) / (hi - lo)).coerceIn(0.0, 1.0).toFloat())
                    if (pen) path.lineTo(px, py) else path.moveTo(px, py)
                    pen = true
                }
                p.style = Paint.Style.STROKE
                p.strokeWidth = 2 * d
                p.color = fill
                c.drawPath(path, p)
                p.style = Paint.Style.FILL
                p.color = state
                p.textAlign = Paint.Align.RIGHT
                p.textSize = 18 * sp
                c.drawText(text() + " " + (def?.unit ?: ""), w - 8 * d, 20 * sp, p)
            }
        }
        if (tile.kind != Kind.CAMERA) {
            p.textSize = 13 * sp
            val room = if (tile.kind == Kind.GRAPH) w * 0.5f else w - 16 * d
            val n = p.breakText(label, true, room, null)
            small(c, label.take(n), 8 * d, 16 * sp, Paint.Align.LEFT)
        }
    }

    private fun status(c: Canvas, w: Float, h: Float) {
        val s = DriveService.instance
        val lines = mutableListOf(
            if (s == null) "Stopped" else if (s.cam.paused) "Paused (hot)" else if (s.cam.recordingSince != null) "● Recording" else "Not recording",
            "OBD: " + if (Hub.obdConnected) "connected" else Hub.obdStatus, "GPS: " + Hub.gpsStatus,
            "Loop: %.1f GB".format((s?.cam?.loopBytes ?: 0) / 1073741824.0))
        if (Hub.dtcs.isNotEmpty()) lines += "Faults: ${Hub.dtcs.size}"
        val step = min(17 * sp, (h - 24 * sp) / lines.size)
        p.style = Paint.Style.FILL
        p.textAlign = Paint.Align.LEFT
        p.textSize = step * 0.78f
        lines.forEachIndexed { i, l ->
            p.color = if (l.startsWith("Faults") || l.startsWith("●")) Palette.alarm else Color.WHITE
            c.drawText(l.take(p.breakText(l, true, w - 14 * d, null)), 8 * d, 20 * sp + step * (i + 0.8f), p)
        }
    }
}

/** Lays tiles out in the page's grid. */
class PageView(c: Context, val page: DashPage, accent: Int) : ViewGroup(c) {
    val tiles: List<TileView>

    init {
        val defs = Hub.allValues().associateBy { it.key }
        tiles = page.tiles.map { TileView(c, it, defs[it.key], accent) }
        tiles.forEach(::addView)
    }

    fun camera(): TileView? = tiles.firstOrNull { it.tile.kind == Kind.CAMERA }

    override fun onMeasure(ws: Int, hs: Int) {
        val w = MeasureSpec.getSize(ws)
        val h = MeasureSpec.getSize(hs)
        setMeasuredDimension(w, h)
        for (t in tiles) t.measure(MeasureSpec.makeMeasureSpec(w * t.tile.cols / page.cols, MeasureSpec.EXACTLY), MeasureSpec.makeMeasureSpec(h * t.tile.rows / page.rows, MeasureSpec.EXACTLY))
    }

    override fun onLayout(changed: Boolean, l: Int, t: Int, r: Int, b: Int) {
        for (v in tiles) {
            val x = (r - l) * v.tile.col / page.cols
            val y = (b - t) * v.tile.row / page.rows
            v.layout(x, y, x + v.measuredWidth, y + v.measuredHeight)
        }
    }
}
