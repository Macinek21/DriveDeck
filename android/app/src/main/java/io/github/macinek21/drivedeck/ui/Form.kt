package io.github.macinek21.drivedeck.ui

import android.graphics.Color
import android.os.Bundle
import android.text.Editable
import android.text.InputFilter
import android.text.InputType
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.SeekBar
import android.widget.Spinner
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.widget.SwitchCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import io.github.macinek21.drivedeck.svc.Hub
import kotlin.math.roundToInt

/** Base for the secondary screens: title bar, scrolling body, small form builder. */
abstract class FormActivity : AppCompatActivity() {
    lateinit var body: LinearLayout
    private lateinit var scroll: ScrollView
    protected lateinit var bar: LinearLayout

    fun dp(v: Int) = (v * resources.displayMetrics.density).roundToInt()

    protected fun frame(title: String) {
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setBackgroundColor(0xFF0B0E12.toInt()) }
        bar = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL; setPadding(dp(16), dp(6), dp(8), dp(6)); setBackgroundColor(Palette.tile) }
        bar.addView(TextView(this).apply { text = title; textSize = 20f; setTextColor(Color.WHITE) }, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
        body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; isFocusableInTouchMode = true; setPadding(dp(16), dp(8), dp(16), dp(48)) }
        scroll = ScrollView(this).apply { addView(body) }
        root.addView(bar, MATCH_PARENT, WRAP_CONTENT)
        root.addView(scroll, LinearLayout.LayoutParams(MATCH_PARENT, 0, 1f))
        setContentView(root)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        ViewCompat.setOnApplyWindowInsetsListener(root) { v, i ->
            val b = i.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout() or WindowInsetsCompat.Type.ime())
            v.setPadding(b.left, b.top, b.right, b.bottom)
            WindowInsetsCompat.CONSUMED
        }
    }

    override fun onCreate(b: Bundle?) {
        super.onCreate(b)
        Hub.load(this)
    }

    protected fun barButton(t: String, click: () -> Unit) = bar.addView(Button(this).apply { text = t; setOnClickListener { click() } }, WRAP_CONTENT, WRAP_CONTENT)

    /** Rebuilds the form without losing the scroll position. */
    protected fun rebuild(build: () -> Unit) {
        val y = scroll.scrollY
        currentFocus?.clearFocus()
        body.removeAllViews()
        build()
        scroll.post { scroll.scrollTo(0, y) }
    }
}

class Form(private val a: FormActivity, private val into: ViewGroup) {
    private fun add(v: View, bottom: Int = 6) = into.addView(v, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { bottomMargin = a.dp(bottom) })

    fun header(t: String) = add(TextView(a).apply { text = t; textSize = 18f; setTextColor(Palette.blue); setPadding(0, a.dp(18), 0, a.dp(4)) })
    fun note(t: String) = TextView(a).apply { text = t; textSize = 13f; setTextColor(Palette.dim) }.also { add(it) }
    fun error() = TextView(a).apply { setTextColor(0xFFFF6E5A.toInt()); visibility = View.GONE }.also { add(it) }

    fun switch(t: String, on: Boolean, set: (Boolean) -> Unit) =
        add(SwitchCompat(a).apply { text = t; textSize = 16f; isChecked = on; minHeight = a.dp(48); setOnCheckedChangeListener { _, v -> set(v) } }, 0)

    fun <T> choice(t: String, items: List<Pair<T, String>>, current: T, set: (T) -> Unit) {
        add(TextView(a).apply { text = t; textSize = 14f; setTextColor(Palette.dim) }, 0)
        val start = items.indexOfFirst { it.first == current }.coerceAtLeast(0)
        add(Spinner(a).apply {
            adapter = ArrayAdapter(a, android.R.layout.simple_spinner_item, items.map { it.second }).apply { setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item) }
            minimumHeight = a.dp(48)
            contentDescription = t
            setSelection(start, false)
            var last = start
            onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
                override fun onNothingSelected(p: AdapterView<*>?) {}
                override fun onItemSelected(p: AdapterView<*>?, v: View?, i: Int, id: Long) { if (i != last) { last = i; set(items[i].first) } }
            }
        })
    }

    /** Slider over min..max in [step]s. */
    fun slider(t: String, min: Double, max: Double, step: Double, value: Double, fmt: (Double) -> String, enabled: Boolean = true, set: (Double) -> Unit) {
        val l = TextView(a).apply { text = "$t: ${fmt(value)}"; textSize = 15f }
        add(l, 0)
        add(SeekBar(a).apply {
            this.max = ((max - min) / step).roundToInt()
            progress = ((value - min) / step).roundToInt().coerceIn(0, this.max)
            isEnabled = enabled
            contentDescription = t
            minimumHeight = a.dp(40)
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(s: SeekBar, p: Int, user: Boolean) { val v = min + p * step; l.text = "$t: ${fmt(v)}"; if (user) set(v) }
                override fun onStartTrackingTouch(s: SeekBar) {}
                override fun onStopTrackingTouch(s: SeekBar) {}
            })
        })
    }

    fun text(t: String, value: String, max: Int, hint: String = "", number: Boolean = false, set: (String) -> Unit) {
        add(TextView(a).apply { text = t; textSize = 14f; setTextColor(Palette.dim) }, 0)
        add(EditText(a).apply {
            setText(value)
            this.hint = hint
            contentDescription = t
            isSingleLine = true
            if (number) inputType = InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_FLAG_DECIMAL or InputType.TYPE_NUMBER_FLAG_SIGNED
            filters = arrayOf(InputFilter.LengthFilter(max))
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
                override fun afterTextChanged(s: Editable) = set(s.toString())
            })
        })
    }

    fun number(t: String, v: Double?, set: (Double?) -> Unit) = text(t, v?.let { if (it % 1.0 == 0.0) it.toLong().toString() else it.toString() } ?: "", 10, "off", true) { s ->
        val x = s.replace(',', '.').toDoubleOrNull()
        if (s.isBlank() || x != null) set(x)
    }

    fun button(t: String, click: () -> Unit) = add(Button(a).apply { text = t; setOnClickListener { click() } })
    fun view(v: View) = add(v)
}
