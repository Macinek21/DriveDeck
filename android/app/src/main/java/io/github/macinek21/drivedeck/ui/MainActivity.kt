package io.github.macinek21.drivedeck.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Color
import android.graphics.SurfaceTexture
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.view.Gravity
import android.view.Surface
import android.view.TextureView
import android.view.View
import android.view.ViewGroup
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.view.WindowManager
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.PopupMenu
import android.widget.ScrollView
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.core.view.isVisible
import androidx.recyclerview.widget.RecyclerView
import androidx.viewpager2.widget.ViewPager2
import io.github.macinek21.drivedeck.svc.DriveService
import io.github.macinek21.drivedeck.svc.Hub
import io.github.macinek21.drivedeck.svc.Notice
import io.github.macinek21.drivedeck.svc.Severity
import java.text.DateFormat
import java.util.Date
import kotlin.math.roundToInt

class MainActivity : AppCompatActivity() {
    private lateinit var column: LinearLayout
    private lateinit var content: LinearLayout
    private lateinit var tools: LinearLayout
    private lateinit var area: FrameLayout
    private lateinit var pager: ViewPager2
    private lateinit var previewBox: FrameLayout
    private lateinit var preview: TextureView
    private lateinit var rec: TextView
    private lateinit var strip: TextView
    private lateinit var chip: TextView
    private lateinit var panel: LinearLayout
    private lateinit var panelList: LinearLayout
    private lateinit var impact: LinearLayout
    private lateinit var impactText: TextView
    private lateinit var callButton: Button
    private lateinit var dim: View
    private var pages = listOf<PageView>()
    private var offset = 0f
    private var builtFrom = ""
    private var visible = false
    private var dashboardStopped = false
    private var attached: DriveService? = null

    private val askPermissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        if (it.values.any { ok -> !ok }) Hub.post("perm", Severity.WARNING, "Some permissions are off", "Allow them in Android settings > Apps > DriveDeck if you want those features")
        DriveService.start(this)
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).roundToInt()
    private fun round(color: Int, stroke: Int = 0) = GradientDrawable().apply { setColor(color); cornerRadius = dp(8).toFloat(); if (stroke != 0) setStroke(dp(1), stroke) }
    private fun label(size: Float, color: Int = Color.WHITE) = TextView(this).apply { textSize = size; setTextColor(color) }

    override fun onCreate(state: Bundle?) {
        super.onCreate(state)
        Hub.load(this)
        column = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(Color.BLACK) }
        area = FrameLayout(this)
        pager = ViewPager2(this)
        pager.registerOnPageChangeCallback(object : ViewPager2.OnPageChangeCallback() {
            override fun onPageScrolled(position: Int, o: Float, px: Int) { offset = position + o; placePreview() }
            override fun onPageSelected(position: Int) { pageChanged() }
        })
        // One live preview laid over the camera tile; it follows swipes and lets touches through.
        preview = TextureView(this).apply {
            isClickable = false
            surfaceTextureListener = object : TextureView.SurfaceTextureListener {
                override fun onSurfaceTextureAvailable(t: SurfaceTexture, w: Int, h: Int) = attach()
                override fun onSurfaceTextureSizeChanged(t: SurfaceTexture, w: Int, h: Int) = attach()
                override fun onSurfaceTextureDestroyed(t: SurfaceTexture): Boolean { attached?.cam?.gl?.show(null, 0, 0); return true }
                override fun onSurfaceTextureUpdated(t: SurfaceTexture) {}
            }
        }
        rec = label(14f, Palette.alarm).apply { setShadowLayer(4f, 0f, 0f, Color.BLACK); setPadding(dp(10), dp(6), 0, 0) }
        previewBox = FrameLayout(this).apply { visibility = View.INVISIBLE; addView(preview, MATCH_PARENT, MATCH_PARENT); addView(rec, WRAP_CONTENT, WRAP_CONTENT) }
        strip = label(15f).apply { visibility = View.GONE; maxLines = 1; setPadding(dp(12), dp(8), dp(12), dp(8)); setOnClickListener { collapse(); openPanel() } }
        chip = label(14f).apply { visibility = View.GONE; gravity = Gravity.CENTER; setOnClickListener { openPanel() } }
        buildPanel()
        area.addView(pager, MATCH_PARENT, MATCH_PARENT)
        area.addView(previewBox, 1, 1)
        val save = label(22f).apply { text = "⚠"; gravity = Gravity.CENTER; contentDescription = "Save incident"; background = round(Palette.alarm); setOnClickListener { DriveService.instance?.saveIncident() ?: Hub.post("svc", Severity.WARNING, "Not running", "Start DriveDeck from the menu first") } }
        val menu = label(22f).apply { text = "⋮"; gravity = Gravity.CENTER; contentDescription = "Menu"; background = round(0xFF202630.toInt()); setOnClickListener(::showMenu) }
        area.addView(panel, FrameLayout.LayoutParams(dp(340), WRAP_CONTENT, Gravity.TOP or Gravity.END).apply { setMargins(dp(8), dp(8), dp(8), dp(8)) })
        // Buttons and the notification badge have their own narrow bar, so they never cover a tile.
        tools = LinearLayout(this).apply { gravity = Gravity.CENTER }
        for (v in listOf(chip, View(this), menu, save)) tools.addView(v)
        content = LinearLayout(this)
        content.addView(area)
        content.addView(tools)
        column.addView(strip, MATCH_PARENT, WRAP_CONTENT)
        column.addView(content, LinearLayout.LayoutParams(MATCH_PARENT, 0, 1f))
        orient()
        buildImpact()
        dim = View(this).apply { setBackgroundColor(Color.BLACK); isClickable = false }
        val root = FrameLayout(this)
        root.addView(column, MATCH_PARENT, MATCH_PARENT)
        root.addView(impact, MATCH_PARENT, MATCH_PARENT)
        root.addView(dim, MATCH_PARENT, MATCH_PARENT)
        setContentView(root)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        if (Build.VERSION.SDK_INT >= 28) window.attributes = window.attributes.apply { layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES }
        ViewCompat.setOnApplyWindowInsetsListener(column) { v, i ->
            val c = i.getInsets(WindowInsetsCompat.Type.displayCutout())
            v.setPadding(c.left, c.top, c.right, c.bottom)
            i
        }
        area.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> area.post(::placePreview) }
        val need = missing()
        if (state == null && need.isNotEmpty()) askPermissions.launch(need.toTypedArray())
    }

    private fun missing(): List<String> {
        val p = mutableListOf(Manifest.permission.CAMERA, Manifest.permission.ACCESS_COARSE_LOCATION, Manifest.permission.ACCESS_FINE_LOCATION)
        if (Build.VERSION.SDK_INT >= 31) p += Manifest.permission.BLUETOOTH_CONNECT
        if (Build.VERSION.SDK_INT >= 33) p += Manifest.permission.POST_NOTIFICATIONS
        if (Build.VERSION.SDK_INT <= 29) p += listOf(Manifest.permission.READ_EXTERNAL_STORAGE, Manifest.permission.WRITE_EXTERNAL_STORAGE)
        return p.filter { ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED }
    }

    override fun onNewIntent(i: Intent) {
        super.onNewIntent(i)
        dashboardStopped = false
    }

    override fun onStart() {
        super.onStart()
        visible = true
        Hub.onNotice = ::showNotice
        Hub.onNoticesChanged = { updateChip(); fillPanel() }
        Hub.onImpact = ::showImpact
        applyUi()
        if (builtFrom != buildKey()) buildPages()
        if (!dashboardStopped) DriveService.start(this)
        updateChip()
        tick()
    }

    override fun onStop() {
        visible = false
        Hub.main.removeCallbacks(tickRunnable)
        Hub.onNotice = null
        Hub.onNoticesChanged = null
        Hub.onImpact = null
        Hub.visibleKeys = emptySet()
        DriveService.instance?.updateWanted()
        attached?.cam?.gl?.show(null, 0, 0)
        attached = null
        super.onStop()
    }

    override fun onConfigurationChanged(c: Configuration) {
        super.onConfigurationChanged(c)
        orient()
        buildPages()
        rotation()
    }

    override fun onWindowFocusChanged(has: Boolean) {
        super.onWindowFocusChanged(has)
        if (has) WindowInsetsControllerCompat(window, window.decorView).run {
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            hide(WindowInsetsCompat.Type.systemBars())
        }
    }

    private fun applyUi() {
        val u = Hub.settings.ui
        if (u.keepOn) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        dim.alpha = if (u.dim) 0.45f else 0f
        val bottom = u.notices == "bottom"
        column.removeView(strip)
        column.addView(strip, if (bottom) 1 else 0, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT))
    }

    // ---------------- Pages ----------------

    private fun landscape() = resources.configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
    private fun buildKey() = landscape().toString() + Hub.settings.toJson()

    /** Tool bar at the right in landscape, at the bottom in portrait. */
    private fun orient() {
        val land = landscape()
        content.orientation = if (land) LinearLayout.HORIZONTAL else LinearLayout.VERTICAL
        area.layoutParams = if (land) LinearLayout.LayoutParams(0, MATCH_PARENT, 1f) else LinearLayout.LayoutParams(MATCH_PARENT, 0, 1f)
        tools.layoutParams = if (land) LinearLayout.LayoutParams(dp(60), MATCH_PARENT) else LinearLayout.LayoutParams(MATCH_PARENT, dp(60))
        tools.orientation = if (land) LinearLayout.VERTICAL else LinearLayout.HORIZONTAL
        for (i in 0 until tools.childCount) tools.getChildAt(i).layoutParams =
            if (i == 1) LinearLayout.LayoutParams(0, 0, 1f) else LinearLayout.LayoutParams(dp(52), dp(52)).apply { setMargins(dp(4), dp(4), dp(4), dp(4)) }
    }

    private fun buildPages() {
        builtFrom = buildKey()
        val o = if (landscape()) "landscape" else "portrait"
        val list = Hub.settings.pages.filter { it.orientation == "any" || it.orientation == o }.ifEmpty { Hub.settings.pages.take(1) }
        val accent = Palette.parse(Hub.settings.ui.accent, Palette.blue)
        pages = list.map { PageView(this, it, accent).apply { layoutParams = ViewGroup.LayoutParams(MATCH_PARENT, MATCH_PARENT) } }
        pager.adapter = object : RecyclerView.Adapter<RecyclerView.ViewHolder>() {
            override fun getItemCount() = pages.size
            override fun getItemViewType(i: Int) = i
            override fun onCreateViewHolder(p: ViewGroup, i: Int): RecyclerView.ViewHolder {
                (pages[i].parent as? ViewGroup)?.removeView(pages[i])
                return object : RecyclerView.ViewHolder(pages[i]) {}
            }
            override fun onBindViewHolder(h: RecyclerView.ViewHolder, i: Int) {}
        }
        pager.offscreenPageLimit = maxOf(1, pages.size)
        offset = 0f
        pageChanged()
        area.post(::placePreview)
    }

    private fun pageChanged() {
        Hub.visibleKeys = pages.getOrNull(pager.currentItem)?.page?.tiles.orEmpty().filter { it.kind.needsValue }.map { it.key }.toSet()
        DriveService.instance?.updateWanted()
        placePreview()
    }

    private fun placePreview() {
        val w = area.width
        var best: Pair<TileView, Float>? = null
        pages.forEachIndexed { i, p ->
            val t = p.camera()
            val dx = (i - offset) * w
            if (t != null && t.width > 0 && kotlin.math.abs(dx) < w && (best == null || kotlin.math.abs(dx) < kotlin.math.abs(best!!.second))) best = t to dx
        }
        val b = best
        if (b == null || !Hub.settings.cam.enabled) { previewBox.visibility = View.INVISIBLE; return }
        val t = b.first
        val lp = previewBox.layoutParams
        val m = dp(2)
        if (lp.width != t.width - 2 * m || lp.height != t.height - 2 * m) { lp.width = t.width - 2 * m; lp.height = t.height - 2 * m; previewBox.layoutParams = lp }
        previewBox.translationX = t.left + b.second + m
        previewBox.translationY = (t.top + m).toFloat()
        previewBox.isVisible = true
    }

    @Suppress("DEPRECATION")
    private fun rotation() {
        val c = DriveService.instance?.cam ?: return
        c.rotation = windowManager.defaultDisplay.rotation
        c.gl.rotation = c.rotation
    }

    /** Shows the running camera's picture in the tile; the camera itself does not notice. */
    private fun attach() {
        val st = preview.surfaceTexture ?: return
        if (visible && preview.isAvailable) attached?.cam?.gl?.show(Surface(st), preview.width, preview.height)
    }


    private val tickRunnable = Runnable { tick() }

    private fun tick() {
        if (!visible) return
        val s = DriveService.instance
        if (s !== attached) { attached = s; attach(); s?.updateWanted() }
        rotation()
        pages.forEach { p -> p.tiles.forEach { it.refresh() } }
        val since = s?.cam?.recordingSince
        rec.text = when {
            s == null -> "stopped"
            s.cam.paused -> "HOT – paused"
            since == null -> "not recording"
            else -> { val sec = (System.currentTimeMillis() - since) / 1000; (if (sec % 2 == 0L) "● REC " else "○ REC ") + "%d:%02d".format(sec / 60, sec % 60) }
        }
        Hub.main.removeCallbacks(tickRunnable)
        Hub.main.postDelayed(tickRunnable, 150)
    }

    // ---------------- Notifications ----------------

    private fun color(s: Severity) = when (s) { Severity.ALARM -> Palette.alarm; Severity.WARNING -> Palette.warn; else -> Palette.blue }
    private fun bg(s: Severity) = when (s) { Severity.ALARM -> 0xFF4A1218.toInt(); Severity.WARNING -> 0xFF3A3010.toInt(); else -> 0xFF14283A.toInt() }
    private val collapseRunnable = Runnable { collapse() }

    private fun showNotice(n: Notice) {
        // Never replace a more serious message that is still on show.
        val current = strip.tag as? Severity
        if (strip.isVisible && current != null && current > n.severity) return
        strip.text = "${n.title}   ${n.detail}"
        strip.setBackgroundColor(bg(n.severity))
        strip.tag = n.severity
        strip.isVisible = true
        updateChip()
        Hub.main.removeCallbacks(collapseRunnable)
        val secs = if (n.severity == Severity.INFO) 4 else Hub.settings.ui.collapseSeconds
        if (secs > 0) Hub.main.postDelayed(collapseRunnable, secs * 1000L)
    }

    private fun collapse() {
        strip.isVisible = false
        updateChip()
    }

    private fun updateChip() {
        val n = Hub.notices
        chip.isVisible = n.isNotEmpty() && !strip.isVisible && !panel.isVisible
        val worst = n.maxOfOrNull { it.severity } ?: return
        chip.text = "! ${n.size}"
        chip.contentDescription = "${n.size} notifications"
        chip.setTextColor(color(worst))
        chip.background = round(bg(worst), color(worst))
    }

    private fun buildPanel() {
        panel = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; background = round(0xF5101418.toInt(), 0xFF333A44.toInt()); setPadding(dp(10), dp(6), dp(10), dp(8)); visibility = View.GONE; isClickable = true }
        val head = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
        head.addView(label(16f).apply { text = "Notifications" }, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
        head.addView(Button(this).apply { text = "Clear"; setOnClickListener { Hub.remove { !it.key.startsWith("dtc:") } } }, WRAP_CONTENT, WRAP_CONTENT)
        head.addView(Button(this).apply { text = "Close"; setOnClickListener { panel.isVisible = false; updateChip() } }, WRAP_CONTENT, WRAP_CONTENT)
        panel.addView(head)
        panelList = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        panel.addView(ScrollView(this).apply { addView(panelList) }, LinearLayout.LayoutParams(MATCH_PARENT, dp(180)))
    }

    private fun openPanel() {
        panel.isVisible = true
        fillPanel()
        updateChip()
    }

    private fun fillPanel() {
        if (!panel.isVisible) return
        panelList.removeAllViews()
        val f = DateFormat.getTimeInstance(DateFormat.SHORT)
        if (Hub.notices.isEmpty()) panelList.addView(label(14f, Palette.dim).apply { text = "Nothing to report." })
        for (n in Hub.notices) {
            panelList.addView(label(15f, color(n.severity)).apply { text = "${f.format(Date(n.time))}  ${n.title}" })
            panelList.addView(label(13f, 0xFFBBBBBB.toInt()).apply { text = n.detail; setPadding(0, 0, 0, dp(8)) })
        }
    }

    // ---------------- Impact ----------------

    private fun buildImpact() {
        impact = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER; setBackgroundColor(0xE6000000.toInt()); setPadding(dp(24), dp(16), dp(24), dp(16)); visibility = View.GONE; isClickable = true }
        impact.addView(label(30f, Palette.alarm).apply { text = "Impact detected"; gravity = Gravity.CENTER })
        impactText = label(16f).apply { gravity = Gravity.CENTER; setPadding(0, dp(8), 0, dp(16)) }
        impact.addView(impactText)
        callButton = Button(this).apply { textSize = 22f; setTextColor(Color.WHITE); backgroundTintList = android.content.res.ColorStateList.valueOf(Palette.alarm); setOnClickListener { call() } }
        impact.addView(callButton, LinearLayout.LayoutParams(dp(360), dp(72)))
        impact.addView(Button(this).apply { text = "I'm OK – dismiss"; textSize = 18f; setOnClickListener { impact.isVisible = false } }, LinearLayout.LayoutParams(dp(360), dp(60)))
    }

    private val hideImpact = Runnable { impact.isVisible = false }

    private fun showImpact(g: Double) {
        val c = Hub.settings.cam
        impactText.text = "%.1f g. Video from %d s before to %d s after is being saved.".format(g, c.preSeconds, c.postSeconds)
        callButton.isVisible = c.offerCall
        callButton.text = "Call ${c.emergency}"
        impact.isVisible = true
        Hub.main.removeCallbacks(hideImpact)
        Hub.main.postDelayed(hideImpact, 90_000)
    }

    /** Opens the dialler with the number filled in; the user presses call. The number can only be a listed emergency number. */
    private fun call() {
        try { startActivity(Intent(Intent.ACTION_DIAL, Uri.fromParts("tel", Hub.settings.cam.emergency, null))) }
        catch (_: android.content.ActivityNotFoundException) { Hub.post("dial", Severity.WARNING, "No dialler", "This device cannot make calls") }
    }

    // ---------------- Menu ----------------

    private fun showMenu(anchor: View) {
        val s = DriveService.instance
        val m = PopupMenu(this, anchor)
        fun item(t: String, f: () -> Unit) { m.menu.add(t).setOnMenuItemClickListener { f(); true } }
        if (s == null) item("Start DriveDeck") {
            dashboardStopped = false
            val need = missing()
            if (need.isNotEmpty()) askPermissions.launch(need.toTypedArray()) else DriveService.start(this)
        } else if (Hub.settings.cam.enabled) item(if (s.cam.isWanted) "Stop recording" else "Start recording") { if (s.cam.isWanted) s.cam.stop() else s.cam.start() }
        item("Fault codes", ::showFaults)
        item("Recordings") { startActivity(Intent(this, RecordingsActivity::class.java)) }
        item("Edit dashboard layout") { startActivity(Intent(this, LayoutActivity::class.java)) }
        item("Settings") { startActivity(Intent(this, SettingsActivity::class.java)) }
        if (s != null) item("Stop and close") { dashboardStopped = true; s.shutdown(); finishAndRemoveTask() }
        m.show()
    }

    private fun showFaults() {
        val t = StringBuilder()
        if (!Hub.obdConnected) t.append("OBD adapter is not connected (${Hub.obdStatus}).\n\n")
        if (Hub.dtcs.isEmpty()) t.append(if (Hub.obdConnected) "No emission-related fault codes reported by the engine or gearbox." else "")
        for (d in Hub.dtcs) t.append("${d.code}  •  ${d.kind}${if (d.ecu.isEmpty()) "" else "  •  " + d.ecu}\n${d.description}\n\n")
        t.append("\nDriveDeck only reads codes. Use OBDeleven to see all control units and to clear codes.")
        AlertDialog.Builder(this).setTitle("Fault codes").setMessage(t.trim()).setPositiveButton("Close", null).show()
    }
}
