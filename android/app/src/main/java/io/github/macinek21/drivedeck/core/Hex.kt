package io.github.macinek21.drivedeck.core

/** Strict hex helpers: anything that is not hex is rejected. */
object Hex {
    fun isHexChar(c: Char) = c in '0'..'9' || c in 'A'..'F' || c in 'a'..'f'
    fun isHexToken(s: String) = s.isNotEmpty() && s.all(::isHexChar)

    /** "41 0C 1A" or "410C1A" to bytes (as ints 0..255); null on non-hex content or odd length. */
    fun parse(s: String?): IntArray? {
        val c = s?.replace(" ", "") ?: return null
        if (c.isEmpty() || c.length % 2 != 0 || !c.all(::isHexChar)) return null
        return IntArray(c.length / 2) { c.substring(it * 2, it * 2 + 2).toInt(16) }
    }

    fun format(b: IntArray, sep: String = " ") = b.joinToString(sep) { it.toString(16).uppercase().padStart(2, '0') }
}
