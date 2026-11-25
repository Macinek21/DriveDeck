package io.github.macinek21.drivedeck.core

enum class ElmStatus { OK, NO_DATA, ERROR }
class EcuMessage(val header: String, val payload: IntArray)
class ElmResult(val status: ElmStatus, val error: String?, val messages: List<EcuMessage>)

/** Parses raw ELM327 text up to the prompt; tolerant of echo and garbage, never throws, reassembles ISO-TP per ECU. */
object ElmParser {
    const val MAX_CHARS = 16 * 1024
    private val ERRORS = listOf("UNABLE TO CONNECT", "CAN ERROR", "BUS ERROR", "BUS BUSY", "BUFFER FULL", "FB ERROR",
        "DATA ERROR", "RX ERROR", "LV RESET", "ACT ALERT", "STOPPED", "ERROR")

    fun parse(rawIn: String?, sent: String): ElmResult {
        val raw = (rawIn ?: "").take(MAX_CHARS)
        val echo = sent.replace(" ", "").uppercase()
        var status = ElmStatus.OK
        var err: String? = null
        var text = false
        val data = mutableListOf<String>()
        for (l in raw.split('\r', '\n', '>').map { it.replace("\u0000", "").trim() }.filter { it.isNotEmpty() }) {
            val u = l.uppercase()
            when {
                echo.isNotEmpty() && u.replace(" ", "") == echo -> {}
                u.startsWith("SEARCHING") -> {}
                u == "OK" -> text = true
                u == "NO DATA" -> if (status == ElmStatus.OK) status = ElmStatus.NO_DATA
                u == "?" || ERRORS.any { u.contains(it) } -> { status = ElmStatus.ERROR; err = l.take(60) }
                u.startsWith("BUS INIT") -> {}
                isData(u) -> data += u
                else -> text = true
            }
        }
        val msgs = if (data.isEmpty()) emptyList() else reassemble(data)
        if (msgs.isNotEmpty() && status == ElmStatus.NO_DATA) status = ElmStatus.OK
        if (data.isEmpty() && status == ElmStatus.OK && !text) status = ElmStatus.NO_DATA
        return ElmResult(status, err, msgs)
    }

    private fun isData(u: String): Boolean {
        val c = u.indexOf(':')
        val s = if (c in 1..2) u.substring(c + 1) else u
        return s.any { it != ' ' } && s.all { it == ' ' || Hex.isHexChar(it) }
    }

    private class Partial(val len: Int, val bytes: MutableList<Int>) { var nextSeq = 1 }

    private fun reassemble(lines: List<String>): List<EcuMessage> {
        val out = mutableListOf<EcuMessage>()
        if (lines.any { it.indexOf(':') > 0 }) { // headers-off multi-frame: "008", "0: ..", "1: .."
            var declared = -1
            val buf = mutableListOf<Int>()
            for (l in lines) {
                val c = l.indexOf(':')
                if (c < 0) {
                    val t = l.replace(" ", "")
                    if (t.length == 3 && declared < 0) declared = t.toInt(16) else Hex.parse(l)?.let { out += EcuMessage("", it) }
                } else Hex.parse(l.substring(c + 1))?.let { buf += it.toList() }
            }
            if (buf.isNotEmpty() && buf.size >= declared) out += EcuMessage("", (if (declared > 0) buf.take(declared) else buf).toIntArray())
            return out
        }
        val pending = HashMap<String, Partial>()
        for (l in lines) {
            val t = l.split(' ').filter { it.isNotEmpty() }
            val h: String
            val d: IntArray?
            if (t.size >= 2 && t[0].length == 3) {
                h = t[0]; d = Hex.parse(t.drop(1).joinToString(""))
            } else if (t.size >= 6 && t[0] == "18" && (t[1] == "DA" || t[1] == "DB")) {
                h = t.take(4).joinToString(""); d = Hex.parse(t.drop(4).joinToString(""))
            } else {
                Hex.parse(l)?.let { out += EcuMessage("", it) }
                continue
            }
            if (d == null || d.isEmpty()) continue
            when (d[0] shr 4) {
                0 -> { val n = d[0] and 0x0F; if (n in 1 until d.size) out += EcuMessage(h, d.copyOfRange(1, 1 + n)) }
                1 -> if (d.size >= 2) {
                    val n = ((d[0] and 0x0F) shl 8) or d[1]
                    if (n > 0) pending[h] = Partial(n, d.drop(2).take(n).toMutableList())
                }
                2 -> pending[h]?.let { p ->
                    if ((d[0] and 0x0F) != p.nextSeq) pending.remove(h) // lost frame: a partial fault list would mislead
                    else {
                        p.bytes += d.drop(1).take(p.len - p.bytes.size)
                        p.nextSeq = (p.nextSeq + 1) and 0x0F
                        if (p.bytes.size >= p.len) { out += EcuMessage(h, p.bytes.toIntArray()); pending.remove(h) }
                    }
                }
            }
        }
        return out
    }
}
