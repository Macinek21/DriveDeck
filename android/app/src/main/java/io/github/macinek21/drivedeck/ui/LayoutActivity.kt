package io.github.macinek21.drivedeck.ui

import android.graphics.Color
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.widget.LinearLayout
import android.widget.TextView
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AlertDialog
import io.github.macinek21.drivedeck.core.DashPage
import io.github.macinek21.drivedeck.core.Kind
import io.github.macinek21.drivedeck.core.Pids
import io.github.macinek21.drivedeck.core.Settings
import io.github.macinek21.drivedeck.core.Tile
import io.github.macinek21.drivedeck.svc.DriveService
import io.github.macinek21.drivedeck.svc.Hub

class LayoutActivity : FormActivity() {
    private lateinit var work: Settings
    private lateinit var original: String
    private var index = 0
    private var selected: String? = null

    override fun onCreate(b: Bundle?) {
        super.onCreate(b)
        work = b?.getString("draft")?.let { Settings.fromJson(it).settings } ?: Hub.settings.copy()
        original = Hub.settings.copy().toJson()
        frame("Dashboard layout")
        barButton("Cancel") { leave() }
        barButton("Save") { save() }
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) { override fun handleOnBackPressed() = leave() })
        build()
    }

    override fun onSaveInstanceState(o: Bundle) {
        super.onSaveInstanceState(o)
        o.putString("draft", work.toJson())
    }

    private fun leave() {
        if (work.toJson() == original) { finish(); return }
        AlertDialog.Builder(this).setTitle("Discard changes?").setPositiveButton("Discard") { _, _ -> finish() }.setNegativeButton("Keep editing", null).show()
    }

    private fun save() {
        val previous = Hub.settings.pages
        Hub.settings.pages = work.pages
        if (!Hub.save(this)) {
            Hub.settings.pages = previous
            AlertDialog.Builder(this).setMessage("The layout could not be written.").setPositiveButton("OK", null).show()
            return
        }
        DriveService.instance?.updateWanted()
        finish()
    }

    private fun clamp(p: DashPage) = p.tiles.forEach {
        it.row = minOf(it.row, p.rows - 1)
        it.col = minOf(it.col, p.cols - 1)
        it.rows = it.rows.coerceIn(1, p.rows - it.row)
        it.cols = it.cols.coerceIn(1, p.cols - it.col)
    }

    private fun build(): Unit = rebuild {
        val f = Form(this, body)
        index = index.coerceIn(0, work.pages.lastIndex)
        val p = work.pages[index]
        f.choice("Page", work.pages.mapIndexed { i, x -> i to "${i + 1}. ${x.name}" }, index) { index = it; selected = null; build() }
        val row = LinearLayout(this)
        val add = android.widget.Button(this).apply { text = "Add page"; isEnabled = work.pages.size < Settings.MAX_PAGES; setOnClickListener { work.pages.add(DashPage("Page ${work.pages.size + 1}")); index = work.pages.lastIndex; selected = null; build() } }
        val del = android.widget.Button(this).apply { text = "Delete page"; isEnabled = work.pages.size > 1; setOnClickListener { work.pages.removeAt(index); selected = null; build() } }
        row.addView(add)
        row.addView(del)
        f.view(row)
        f.text("Page name", p.name, 24) { p.name = it }
        f.choice("Show when the phone is", listOf("any" to "Any orientation", "landscape" to "Landscape", "portrait" to "Portrait"), p.orientation) { p.orientation = it; build() }
        f.choice("Rows", (1..6).map { it to "$it" }, p.rows) { p.rows = it; clamp(p); build() }
        f.choice("Columns", (1..6).map { it to "$it" }, p.cols) { p.cols = it; clamp(p); build() }
        f.note("Tap an empty cell to add a tile, or a tile to edit it.")
        val portrait = p.orientation == "portrait"
        body.addView(GridPreview(p), LinearLayout.LayoutParams(if (portrait) dp(240) else MATCH_PARENT, dp(if (portrait) 400 else 240)))
        p.tiles.firstOrNull { it.id == selected }?.let { edit(f, p, it) }
    }

    private fun edit(f: Form, p: DashPage, t: Tile) {
        f.header("Selected tile")
        f.choice("Type", Kind.entries.map { it to it.label }, t.kind) { t.kind = it; if (it.needsValue && t.key.isEmpty()) t.key = Pids.GPS_SPEED; build() }
        if (t.kind.needsValue) f.choice("Shows", Pids.all(work.obd.custom).map { it.key to it.name + if (it.unit.isEmpty()) "" else " (${it.unit})" }, t.key) { t.key = it; build() }
        f.choice("Row", (1..p.rows).map { it - 1 to "$it" }, t.row) { t.row = it; clamp(p); build() }
        f.choice("Column", (1..p.cols).map { it - 1 to "$it" }, t.col) { t.col = it; clamp(p); build() }
        f.choice("Height in rows", (1..p.rows - t.row).map { it to "$it" }, t.rows) { t.rows = it; build() }
        f.choice("Width in columns", (1..p.cols - t.col).map { it to "$it" }, t.cols) { t.cols = it; build() }
        if (t.kind.needsValue) {
            f.text("Label (empty = default)", t.label, 24) { t.label = it }
            f.choice("Colour", SettingsActivity.COLORS, t.color) { t.color = it; build() }
            f.note("Thresholds: amber = warning, red = alarm. Empty = off.")
            f.number("Warn above", t.warnAbove) { t.warnAbove = it }
            f.number("Alarm above (with notification)", t.alarmAbove) { t.alarmAbove = it }
            f.number("Warn below (with notification)", t.warnBelow) { t.warnBelow = it }
            if (t.kind != Kind.NUMBER) {
                f.number("Scale minimum", t.min) { t.min = it }
                f.number("Scale maximum", t.max) { t.max = it }
            }
        }
        f.button("Delete tile") { p.tiles.remove(t); selected = null; build() }
        f.button("Done") { selected = null; build() }
    }

    private inner class GridPreview(val p: DashPage) : ViewGroup(this@LayoutActivity) {
        private val slots = ArrayList<Pair<View, IntArray>>()

        init {
            setBackgroundColor(Color.BLACK)
            val names = Pids.all(work.obd.custom).associate { it.key to it.short }
            fun put(text: String, stroke: Int, fill: Int, at: IntArray, click: () -> Unit) {
                val v = TextView(context).apply {
                    this.text = text; gravity = Gravity.CENTER; textSize = 12f; maxLines = 3
                    setTextColor(if (text == "+") Palette.track else Color.WHITE)
                    background = GradientDrawable().apply { setColor(fill); setStroke(dp(if (stroke == Color.WHITE) 3 else 1), stroke); cornerRadius = dp(4).toFloat() }
                    setOnClickListener { click() }
                    contentDescription = if (text == "+") "Empty cell row ${at[0] + 1} column ${at[1] + 1}" else text
                }
                slots += v to at
                addView(v)
            }
            for (r in 0 until p.rows) for (c in 0 until p.cols) put("+", Palette.track, 0, intArrayOf(r, c, 1, 1)) {
                if (p.tiles.size < Settings.MAX_TILES) { val t = Tile(Kind.NUMBER, Pids.GPS_SPEED, r, c); p.tiles += t; selected = t.id; build() }
            }
            val accent = Palette.parse(work.ui.accent, Palette.blue)
            for (t in p.tiles) put((if (t.kind.needsValue) t.label.ifEmpty { names[t.key] ?: "?" } + "\n" else "") + t.kind.label,
                if (t.id == selected) Color.WHITE else Palette.parse(t.color, accent), Palette.tile, intArrayOf(t.row, t.col, t.rows, t.cols)) { selected = t.id; build() }
        }

        override fun onMeasure(ws: Int, hs: Int) {
            val w = MeasureSpec.getSize(ws)
            val h = MeasureSpec.getSize(hs)
            setMeasuredDimension(w, h)
            for ((v, a) in slots) v.measure(MeasureSpec.makeMeasureSpec(w * a[3] / p.cols - dp(2), MeasureSpec.EXACTLY), MeasureSpec.makeMeasureSpec(h * a[2] / p.rows - dp(2), MeasureSpec.EXACTLY))
        }

        override fun onLayout(ch: Boolean, l: Int, t: Int, r: Int, b: Int) {
            for ((v, a) in slots) {
                val x = width * a[1] / p.cols
                val y = height * a[0] / p.rows
                v.layout(x, y, x + v.measuredWidth, y + v.measuredHeight)
            }
        }
    }
}
