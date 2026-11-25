package io.github.macinek21.drivedeck.core

import java.io.IOException
import kotlin.math.sin

/**
 * Behaves like a clone ELM327 v1.5 on an ISO 15765-4 (CAN 11-bit 500k) car: engine 7E8 and gearbox 7E9, headers, ISO-TP fault codes.
 * Used by the unit tests and, in debug builds only, as a "Simulated car" adapter. Release builds strip it.
 */
class ElmSimulator(private val moving: Boolean = false, private val failAfter: Int = Int.MAX_VALUE) : ObdTransport {
    val sent = mutableListOf<String>()
    @Volatile var mil = true
    @Volatile var dtcReply: String? = null
    private var headers = false
    private var header = "7DF"
    private val t0 = System.currentTimeMillis()

    override fun send(command: String, timeoutMs: Long): String {
        sent += command
        if (sent.size > failAfter) throw IOException("link lost")
        if (moving) Thread.sleep(40)
        return reply(command.trim().uppercase()) + "\r\r>"
    }

    private fun wave(period: Double) = if (moving) (sin((System.currentTimeMillis() - t0) / 1000.0 / period) + 1) / 2 else 0.0

    private fun f(h: String, d: IntArray) = if (headers) "$h ${Hex.format(intArrayOf(d.size))} ${Hex.format(d)}" else Hex.format(d)
    private fun e(vararg d: Int) = f("7E8", d)

    private fun reply(c: String): String = when {
        c == "ATZ" -> { headers = false; header = "7DF"; "\r\rELM327 v1.5" }
        c == "ATH1" -> { headers = true; "OK" }
        c == "ATRV" -> "12.6V"
        c == "ATDPN" -> "A6"
        c.startsWith("ATSH") -> { header = c.substring(4); "OK" }
        c.startsWith("AT") -> "OK"
        header == "7E0" -> if (c == "2211BD") e(0x62, 0x11, 0xBD, 0x00, 0x8C) else "NO DATA"
        c == "0100" -> e(0x41, 0, 0x9E, 0x3F, 0x80, 0x03) + "\r" + f("7E9", intArrayOf(0x41, 0, 0x98, 0x18, 0x80, 0x11))
        c == "0120" -> e(0x41, 0x20, 0, 0x02, 0xA0, 0x01)
        c == "0140" -> e(0x41, 0x40, 0x44, 0x80, 0x00, 0x14)
        c == "0101" -> e(0x41, 0x01, if (mil) 0x83 else 0, 0x07, 0xE5, 0)
        c == "010C" -> (if (moving) ((900 + 4200 * wave(3.0)) * 4).toInt() else 0x1AF8).let { e(0x41, 0x0C, it shr 8, it and 0xFF) }
        c == "010D" -> (if (moving) (20 + 90 * wave(7.0)).toInt() else 0x32).let { e(0x41, 0x0D, it) + "\r" + f("7E9", intArrayOf(0x41, 0x0D, it)) }
        c == "0105" -> e(0x41, 0x05, if (moving) (120 + 45 * wave(20.0)).toInt() else 0x7B)
        c == "010B" -> e(0x41, 0x0B, if (moving) (60 + 120 * wave(3.0)).toInt() else 0x96)
        c == "0104" -> e(0x41, 0x04, (60 + 150 * wave(4.0)).toInt())
        c == "010F" -> e(0x41, 0x0F, 0x41)
        c == "010E" -> e(0x41, 0x0E, 0x98)
        c == "0106" -> e(0x41, 0x06, 0x84)
        c == "0133" -> e(0x41, 0x33, 0x64)
        c == "0110" -> e(0x41, 0x10, 0x05, 0xDC)
        c == "012F" -> e(0x41, 0x2F, 0x90)
        c == "0146" -> e(0x41, 0x46, 0x32)
        c == "03" -> dtcReply ?: if (mil) "7E8 10 08 43 03 01 33 03 01\r7E8 21 06 04 55 55 55 55 55\r7E9 02 43 00" else "7E8 02 43 00\r7E9 02 43 00"
        c == "07" -> "7E8 04 47 01 01 71"
        c == "0A" -> "7E8 02 4A 00"
        else -> "NO DATA"
    }

    override fun close() {}
}
