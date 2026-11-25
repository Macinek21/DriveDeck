package io.github.macinek21.drivedeck.core

import kotlin.math.abs
import kotlin.math.floor

/**
 * Small arithmetic language turning reply bytes A..H into a number: + - * / %, parentheses, unary minus,
 * abs, round, signed8, signed16, min, max, bit(x,n). It can only compute a number; length and nesting are bounded.
 */
class Formula private constructor(val source: String, private val root: Node, val highestVariable: Int) {
    fun interface Node { fun eval(v: DoubleArray): Double }

    /** Missing bytes evaluate as NaN so the caller can reject the value. */
    fun evaluate(data: IntArray): Double =
        root.eval(DoubleArray(VARS) { if (it < data.size) data[it].toDouble() else Double.NaN })

    class ParseException(message: String) : Exception(message)

    companion object {
        const val MAX_LENGTH = 200
        const val MAX_DEPTH = 24
        const val VARS = 8
        private val ARITY = mapOf("abs" to 1, "round" to 1, "signed8" to 1, "signed16" to 1, "min" to 2, "max" to 2, "bit" to 2)

        fun parse(source: String): Formula {
            if (source.isBlank()) throw ParseException("Formula is empty.")
            if (source.length > MAX_LENGTH) throw ParseException("Formula is longer than $MAX_LENGTH characters.")
            val p = Parser(source)
            val root = p.expression(0, 0)
            p.expectEnd()
            return Formula(source.trim(), root, p.highest)
        }

        fun parseOrNull(s: String): Formula? = try { parse(s) } catch (_: ParseException) { null }
        fun error(s: String): String? = try { parse(s); null } catch (e: ParseException) { e.message }
    }

    private class Parser(val s: String) {
        var pos = 0
        var highest = -1
        fun peek(): Char {
            while (pos < s.length && s[pos].isWhitespace()) pos++
            return if (pos < s.length) s[pos] else '\u0000'
        }
        fun fail(m: String): Nothing = throw ParseException(m)
        fun expectEnd() { if (peek() != '\u0000') fail("Unexpected '${s[pos]}' at position ${pos + 1}.") }
        fun prec(c: Char) = when (c) { '+', '-' -> 1; '*', '/', '%' -> 2; else -> -1 }

        fun expression(minPrec: Int, depth: Int): Node {
            if (depth > MAX_DEPTH) fail("Formula is nested too deeply.")
            var left = unary(depth)
            while (true) {
                val op = peek()
                val pr = prec(op)
                if (pr < 0 || pr < minPrec) return left
                pos++
                val l = left
                val r = expression(pr + 1, depth + 1)
                left = when (op) {
                    '+' -> Node { l.eval(it) + r.eval(it) }
                    '-' -> Node { l.eval(it) - r.eval(it) }
                    '*' -> Node { l.eval(it) * r.eval(it) }
                    '/' -> Node { v -> r.eval(v).let { if (it == 0.0) Double.NaN else l.eval(v) / it } }
                    else -> Node { v -> r.eval(v).let { if (it == 0.0) Double.NaN else l.eval(v) % it } }
                }
            }
        }

        fun unary(depth: Int): Node {
            if (depth > MAX_DEPTH) fail("Formula is nested too deeply.")
            return when (peek()) {
                '-' -> { pos++; val n = unary(depth + 1); Node { -n.eval(it) } }
                '+' -> { pos++; unary(depth + 1) }
                else -> primary(depth)
            }
        }

        fun primary(depth: Int): Node {
            val c = peek()
            if (c == '(') {
                pos++
                val n = expression(0, depth + 1)
                if (peek() != ')') fail("Missing ')'.")
                pos++
                return n
            }
            if (c in '0'..'9' || c == '.') {
                val st = pos
                while (pos < s.length && (s[pos] in '0'..'9' || s[pos] == '.')) pos++
                val d = s.substring(st, pos).toDoubleOrNull() ?: fail("Invalid number at position ${st + 1}.")
                return Node { d }
            }
            if (c.isLetter()) {
                val st = pos
                while (pos < s.length && s[pos].isLetterOrDigit()) pos++
                val id = s.substring(st, pos)
                if (id.length == 1 && id[0].uppercaseChar() in 'A' until 'A' + VARS) {
                    val i = id[0].uppercaseChar() - 'A'
                    highest = maxOf(highest, i)
                    return Node { it[i] }
                }
                val name = id.lowercase()
                val arity = ARITY[name] ?: fail("Unknown name '$id'. Use A-H, abs, round, min, max, signed8, signed16, bit.")
                if (peek() != '(') fail("Expected '(' after $id.")
                pos++
                val a = mutableListOf(expression(0, depth + 1))
                while (peek() == ',') { pos++; a += expression(0, depth + 1) }
                if (peek() != ')') fail("Missing ')' after arguments of $id.")
                pos++
                if (a.size != arity) fail("$id takes $arity argument(s).")
                return call(name, a)
            }
            if (c == '\u0000') fail("Formula ends unexpectedly.")
            fail("Unexpected '$c' at position ${pos + 1}.")
        }

        private fun signed(x: Double, bits: Int): Double {
            if (x.isNaN()) return x
            val m = x.toLong() and ((1L shl bits) - 1)
            return (if (m >= (1L shl (bits - 1))) m - (1L shl bits) else m).toDouble()
        }

        fun call(name: String, a: List<Node>): Node = when (name) {
            "abs" -> Node { abs(a[0].eval(it)) }
            "round" -> Node { v -> a[0].eval(v).let { if (it < 0) -floor(-it + 0.5) else floor(it + 0.5) } }
            "signed8" -> Node { signed(a[0].eval(it), 8) }
            "signed16" -> Node { signed(a[0].eval(it), 16) }
            "min" -> Node { minOf(a[0].eval(it), a[1].eval(it)) }
            "max" -> Node { maxOf(a[0].eval(it), a[1].eval(it)) }
            else -> Node { v ->
                val x = a[0].eval(v)
                val n = a[1].eval(v)
                if (x.isNaN() || n.isNaN() || n < 0 || n > 31) Double.NaN else ((x.toLong() shr n.toInt()) and 1L).toDouble()
            }
        }
    }
}
