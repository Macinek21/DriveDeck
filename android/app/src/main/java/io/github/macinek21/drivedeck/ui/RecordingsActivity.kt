package io.github.macinek21.drivedeck.ui

import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.Intent
import android.graphics.Color
import android.os.Bundle
import android.view.Gravity
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.MediaController
import android.widget.TextView
import android.widget.VideoView
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AlertDialog
import androidx.core.content.FileProvider
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.isVisible
import io.github.macinek21.drivedeck.core.Names
import io.github.macinek21.drivedeck.core.Srt
import io.github.macinek21.drivedeck.svc.AppLog
import io.github.macinek21.drivedeck.svc.DriveService
import io.github.macinek21.drivedeck.svc.Hub
import io.github.macinek21.drivedeck.svc.Store
import java.io.File
import java.io.IOException
import java.text.DateFormat
import java.util.Date

class RecordingsActivity : FormActivity() {
    private class Group(val dir: File, val title: String, val clips: List<File>, val bytes: Long)

    private lateinit var player: FrameLayout
    private lateinit var video: VideoView
    private lateinit var subtitle: TextView
    private var cues = emptyList<Srt.Cue>()
    private var loop = false

    override fun onCreate(b: Bundle?) {
        super.onCreate(b)
        frame("Recordings")
        barButton("Incidents") { loop = false; load() }
        barButton("Loop") { loop = true; load() }
        player = FrameLayout(this).apply { setBackgroundColor(Color.BLACK); isVisible = false; isClickable = true }
        video = VideoView(this)
        subtitle = TextView(this).apply { setTextColor(Color.WHITE); textSize = 15f; setBackgroundColor(0x99000000.toInt()); setPadding(dp(8), dp(4), dp(8), dp(4)) }
        player.addView(video, FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT, Gravity.CENTER))
        player.addView(subtitle, FrameLayout.LayoutParams(WRAP_CONTENT, WRAP_CONTENT, Gravity.TOP or Gravity.CENTER_HORIZONTAL).apply { topMargin = dp(12) })
        player.addView(Button(this).apply { text = "Close"; setOnClickListener { closePlayer() } }, FrameLayout.LayoutParams(WRAP_CONTENT, WRAP_CONTENT, Gravity.TOP or Gravity.END).apply { topMargin = dp(4) })
        addContentView(player, FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT))
        ViewCompat.setOnApplyWindowInsetsListener(window.decorView) { v, i ->
            val b = i.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
            player.setPadding(b.left, b.top, b.right, b.bottom)
            ViewCompat.onApplyWindowInsets(v, i)
        }
        video.setMediaController(MediaController(this).apply { setAnchorView(video) })
        video.setOnErrorListener { _, _, _ -> closePlayer(); AlertDialog.Builder(this).setMessage("This video cannot be played (it may be unfinished).").setPositiveButton("OK", null).show(); true }
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) { override fun handleOnBackPressed() { if (player.isVisible) closePlayer() else finish() } })
        load()
    }

    override fun onStop() { closePlayer(); super.onStop() }

    private fun load() {
        val wantLoop = loop
        Store.io.execute {
            val active = DriveService.instance?.cam?.recordingSince
            val places = Store.places()
            val groups = if (wantLoop) places.flatMap { p -> p.loop.listFiles().orEmpty().toList() }.filter { it.extension == "mp4" && Names.parse(it.nameWithoutExtension) != null && it.length() > 0 }
                .sortedByDescending { it.name }.take(300).map { f -> Group(f.parentFile!!, time(f.nameWithoutExtension) + where(f), listOf(f), f.length()) }
            else places.flatMap { p -> p.events.listFiles().orEmpty().toList() }.filter { it.isDirectory && Names.parse(it.name) != null }.sortedByDescending { it.name }.map { d ->
                val all = d.listFiles().orEmpty()
                Group(d, (if ("_manual" in d.name) "Saved manually – " else "Impact – ") + time(d.name) + where(d), all.filter { it.extension == "mp4" }.sortedBy { it.name }, all.sumOf { it.length() })
            }
            runOnUiThread { if (!isDestroyed && wantLoop == loop) show(groups, active != null) }
        }
    }

    private fun where(f: File) = if (f.path.startsWith(Store.device.root.path)) "" else "  (memory card)"
    private fun time(n: String) = Names.parse(n)?.let { DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.MEDIUM).format(Date(it)) } ?: n
    private fun size(b: Long) = if (b > 1_073_741_824) "%.1f GB".format(b / 1073741824.0) else "${b / 1_048_576} MB"

    private fun show(groups: List<Group>, recording: Boolean): Unit = rebuild {
        val f = Form(this, body)
        f.header(if (loop) "Loop clips" else "Saved incidents")
        if (groups.isEmpty()) f.note(if (loop) "No loop clips yet." else "No saved incidents. Impacts and the red button store footage here.")
        groups.forEachIndexed { i, g ->
            val text = TextView(this).apply { text = "${g.title}\n${if (loop) "" else "${g.clips.size} video(s), "}${size(g.bytes)}"; textSize = 15f }
            val row = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; minimumHeight = dp(56) }
            row.addView(text, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
            if (loop && i == 0 && recording) row.addView(TextView(this).apply { this.text = "recording" })
            else row.addView(Button(this).apply { this.text = "Open"; setOnClickListener { open(g) } })
            f.view(row)
        }
    }

    private fun open(g: Group) {
        val items = ArrayList<Pair<String, () -> Unit>>()
        for (c in g.clips) items += (if (g.clips.size == 1) "Play" else "Play ${c.name}") to { play(c) }
        if (g.clips.isNotEmpty()) items += "Share video" to { share(g.clips) }
        items += "Delete" to { delete(g) }
        AlertDialog.Builder(this).setTitle(g.title).setItems(items.map { it.first }.toTypedArray()) { _, i -> items[i].second() }.show()
    }

    private val subtitleTick = object : Runnable {
        override fun run() {
            val p = video.currentPosition
            val t = cues.firstOrNull { p >= it.from && p < it.to }?.text ?: ""
            subtitle.text = t
            subtitle.isVisible = t.isNotEmpty()
            Hub.main.postDelayed(this, 200)
        }
    }

    private fun play(f: File) {
        val srt = File(f.parentFile, f.nameWithoutExtension + ".srt")
        cues = try { if (srt.exists() && srt.length() < 1_000_000) Srt.parse(srt.readText()) else emptyList() } catch (_: IOException) { emptyList() }
        player.isVisible = true
        video.setVideoPath(f.path)
        video.start()
        Hub.main.removeCallbacks(subtitleTick)
        Hub.main.post(subtitleTick)
    }

    private fun closePlayer() {
        Hub.main.removeCallbacks(subtitleTick)
        video.stopPlayback()
        player.isVisible = false
    }

    private fun share(files: List<File>) {
        try {
            val uris = ArrayList(files.map { FileProvider.getUriForFile(this, "$packageName.files", it) })
            val i = Intent(if (uris.size == 1) Intent.ACTION_SEND else Intent.ACTION_SEND_MULTIPLE).setType("video/mp4").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            if (uris.size == 1) i.putExtra(Intent.EXTRA_STREAM, uris[0]) else i.putParcelableArrayListExtra(Intent.EXTRA_STREAM, uris)
            i.clipData = ClipData.newRawUri("", uris[0]).apply { uris.drop(1).forEach { addItem(ClipData.Item(it)) } }
            startActivity(Intent.createChooser(i, "Share video"))
        } catch (e: ActivityNotFoundException) { AppLog.e("share", e) } catch (e: IllegalArgumentException) { AppLog.e("share", e) }
    }

    private fun delete(g: Group) {
        AlertDialog.Builder(this).setTitle("Delete permanently?").setMessage("${g.title}\n\nThis cannot be undone.").setNegativeButton("Cancel", null).setPositiveButton("Delete") { _, _ ->
            Store.io.execute {
                if (loop) g.clips.forEach { c -> listOf("mp4", "srt", "gpx").forEach { File(c.parentFile, c.nameWithoutExtension + "." + it).delete() } }
                else { g.dir.listFiles().orEmpty().filter { it.isFile }.forEach { it.delete() }; g.dir.delete() }
                runOnUiThread { DriveService.instance?.cam?.rescan(); if (!isDestroyed) load() }
            }
        }.show()
    }
}
