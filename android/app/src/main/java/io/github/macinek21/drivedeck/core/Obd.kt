package io.github.macinek21.drivedeck.core

import java.io.Closeable
import java.io.IOException

/** Blocking link to an ELM327: send one line, get everything up to the '>' prompt. Throws IOException when the link drops or times out. */
interface ObdTransport : Closeable {
    fun send(command: String, timeoutMs: Long): String
}

class ObdException(m: String) : IOException(m)

class Reading private constructor(val value: Double, val error: String?, val unsupported: Boolean) {
    val ok get() = error == null

    companion object {
        fun of(v: Double) = Reading(v, null, false)
        fun fail(e: String, unsupported: Boolean = false) = Reading(Double.NaN, e, unsupported)
    }
}

/** ELM327 protocol. Read-only: every request passes [CommandGuard]. Use from one thread only. */
class ObdClient(private val t: ObdTransport) {
    var version = ""
        private set
    var protocol = ""
        private set
    val supported = HashSet<Int>()
    private var header = DEFAULT
    private val formulas = HashMap<String, Formula?>()

    private fun at(c: String, ms: Long = TIMEOUT): String {
        if (!CommandGuard.atAllowed(c)) throw ObdException("AT command refused")
        return t.send(c, ms)
    }

    private fun obd(c: String, ms: Long = TIMEOUT): ElmResult {
        CommandGuard.requestError(c)?.let { throw ObdException(it) }
        return ElmParser.parse(t.send(c, ms), c)
    }

    fun init(proto: String) {
        version = at("ATZ", 4000).split('\r', '\n', '>').map { it.trim() }.firstOrNull { it.contains("ELM", true) }?.take(40) ?: "ELM327"
        val p = proto.takeIf { it in listOf("0", "6", "7", "8", "9") } ?: "0"
        for (c in listOf("ATE0", "ATL0", "ATS1", "ATH1", "ATAT1", "ATSP$p")) at(c)
        header = DEFAULT
        val r = obd("0100", 12000)
        if (r.status == ElmStatus.NO_DATA) throw ObdException("The car did not answer. Is the ignition on?")
        if (r.status == ElmStatus.ERROR) throw ObdException("Adapter error: ${r.error}")
        addSupported(r, 0)
        protocol = at("ATDPN").replace(">", "").trim().take(4)
        var base = 0x20
        while (base <= 0xA0 && base in supported) {
            val n = obd("01" + Hex.format(intArrayOf(base)))
            if (n.status != ElmStatus.OK) break
            addSupported(n, base)
            base += 0x20
        }
    }

    private fun addSupported(r: ElmResult, base: Int) {
        for (p in r.messages.map { it.payload }) {
            if (p.size < 6 || p[0] != 0x41 || p[1] != base) continue
            for (i in 0 until 32) if ((p[2 + i / 8] shr (7 - i % 8)) and 1 == 1) supported += base + i + 1
        }
    }

    private fun useHeader(h: String) {
        val want = h.ifEmpty { DEFAULT }.uppercase()
        if (want != header) {
            at("ATSH$want")
            header = want
        }
    }

    fun read(def: ValueDef): Reading {
        CommandGuard.requestError(def.request)?.let { return Reading.fail(it) }
        CommandGuard.headerError(def.header)?.let { return Reading.fail(it) }
        val req = Hex.parse(def.request)!!
        if (req.size == 2 && req[0] == 1 && req[1] !in supported) return Reading.fail("Not supported by car", true)
        val f = formulas.getOrPut(def.formula) { Formula.parseOrNull(def.formula) } ?: return Reading.fail("Invalid formula")
        useHeader(def.header)
        val r = obd(Hex.format(req, ""))
        if (r.status == ElmStatus.NO_DATA) return Reading.fail("No data", true)
        val p = r.messages
            .filter { m -> m.payload.size >= req.size && m.payload[0] == req[0] + 0x40 && (1 until req.size).all { m.payload[it] == req[it] } }
            .minByOrNull { if (it.header == "7E8") 0 else 1 }?.payload
        if (p == null) {
            val negative = r.messages.any { it.payload.size >= 3 && it.payload[0] == 0x7F && it.payload[1] == req[0] }
            return if (negative) Reading.fail("Rejected by ECU", true) else Reading.fail(r.error ?: "Unexpected response")
        }
        val data = p.copyOfRange(req.size, p.size)
        if (f.highestVariable >= data.size) return Reading.fail("Response too short")
        val v = f.evaluate(data)
        return if (v.isNaN() || v.isInfinite()) Reading.fail("Formula gave no number") else Reading.of(v)
    }

    /** null when the codes could not be read, so an adapter hiccup never looks like "no faults". */
    fun readDtcs(permanent: Boolean): Set<Dtc>? {
        useHeader("")
        val out = LinkedHashSet<Dtc>()
        for ((m, kind) in listOf("03" to DtcKind.Stored, "07" to DtcKind.Pending, "0A" to DtcKind.Permanent)) {
            if (kind == DtcKind.Permanent && !permanent) continue
            val r = obd(m, 4000)
            if (r.status == ElmStatus.ERROR) return null
            val reply = r.messages.filter { it.payload.isNotEmpty() && it.payload[0] == m.toInt(16) + 0x40 }
            if (kind == DtcKind.Stored && reply.isEmpty()) return null
            reply.forEach { out += DtcDecoder.response(it.payload, kind, ecuName(it.header)) }
        }
        return out
    }

    /** Check-engine lamp: true if any ECU reports it on; null when unknown. */
    fun readMil(): Boolean? {
        useHeader("")
        val m = obd("0101").messages.filter { it.payload.size >= 3 && it.payload[0] == 0x41 && it.payload[1] == 1 }
        return if (m.isEmpty()) null else m.any { it.payload[2] and 0x80 != 0 }
    }

    fun readVoltage(): Reading = parseVoltage(at("ATRV"))

    companion object {
        const val TIMEOUT = 2500L
        private const val DEFAULT = "7DF"

        fun parseVoltage(raw: String): Reading {
            val v = raw.replace(">", "").trim().trimEnd('V', 'v').trim().toDoubleOrNull()
            return if (v != null && v > 0 && v < 40) Reading.of(v) else Reading.fail("Unreadable voltage")
        }

        fun ecuName(h: String) = when (h.uppercase()) { "7E8" -> "Engine"; "7E9" -> "Gearbox"; else -> h }
    }
}

enum class ObdState { OFF, CONNECTING, CONNECTED, RECONNECTING, FAILED }

/** Background thread: connect, poll wanted values, check faults every [dtcSeconds] and when the lamp turns on, reconnect with back-off. */
class ObdPoller(
    private val connect: () -> ObdTransport, private val protocol: String, private val dtcSeconds: Int,
    private val permanent: Boolean, private val petrol: Boolean, private val listener: Listener, private val minCycleMs: Long = 50,
) {
    interface Listener {
        fun onValue(key: String, v: Double)
        fun onUnsupported(key: String)
        fun onState(s: ObdState, msg: String)
        fun onDtcs(all: Set<Dtc>, fresh: Set<Dtc>)
    }

    private class Stamped(val v: Double, val at: Long)

    @Volatile private var wanted: List<ValueDef> = emptyList()
    @Volatile private var running = false
    @Volatile private var transport: ObdTransport? = null
    private var thread: Thread? = null
    private var lastDtcs: Set<Dtc> = emptySet()
    private val last = HashMap<String, Stamped>()
    private fun now() = System.nanoTime() / 1_000_000

    fun setWanted(defs: Collection<ValueDef>) {
        wanted = defs.flatMap {
            when (it.key) {
                Pids.BOOST -> listOf(Pids.std("0B"), Pids.std("33"))
                Pids.CONSUMPTION -> listOf(Pids.std("0D"), Pids.std("5E"), Pids.std("10"))
                else -> if (it.request.isNotEmpty() || it.key == Pids.VOLTAGE) listOf(it) else emptyList()
            }
        }.distinctBy { it.key }
    }

    fun start() {
        if (running) return
        running = true
        thread = Thread(::run, "obd").also { it.isDaemon = true; it.start() }
    }

    fun stop() {
        running = false
        runCatching { transport?.close() }
        thread?.interrupt()
    }

    private fun run() {
        var attempt = 0
        while (running) {
            try {
                listener.onState(if (attempt == 0) ObdState.CONNECTING else ObdState.RECONNECTING, "Connecting to adapter…")
                connect().use { t ->
                    transport = t
                    val c = ObdClient(t)
                    c.init(protocol)
                    attempt = 0
                    listener.onState(ObdState.CONNECTED, "${c.version}, protocol ${c.protocol}")
                    poll(c)
                }
            } catch (_: InterruptedException) {
                break
            } catch (e: Exception) {
                if (!running) break
                attempt++
                listener.onState(if (attempt >= 3) ObdState.FAILED else ObdState.RECONNECTING, e.message ?: "Connection lost")
            }
            transport = null
            try {
                if (running) Thread.sleep(minOf(30, 2 * attempt + 1) * 1000L)
            } catch (_: InterruptedException) {
                break
            }
        }
        listener.onState(ObdState.OFF, "Off")
    }

    private fun poll(c: ObdClient) {
        val unsupported = HashSet<String>()
        var tMed = Long.MIN_VALUE / 2
        var tSlow = Long.MIN_VALUE / 2
        var tDtc = Long.MIN_VALUE / 2
        var errors = 0
        var mil: Boolean? = null
        while (running) {
            val start = now()
            val med = start - tMed >= 1000
            val slow = start - tSlow >= 5000
            if (med) tMed = start
            if (slow) tSlow = start
            for (d in wanted) {
                if (!running) return
                if (d.key in unsupported || (d.priority == 1 && !med) || (d.priority >= 2 && !slow)) continue
                val r = if (d.key == Pids.VOLTAGE) c.readVoltage() else c.read(d)
                if (r.ok) {
                    errors = 0
                    put(d.key, r.value)
                } else if (r.unsupported) {
                    unsupported += d.key
                    listener.onUnsupported(d.key)
                } else if (++errors > 12) throw ObdException("Too many errors from adapter")
            }
            derived()
            if (slow) {
                val m = c.readMil()
                val turnedOn = m == true && mil != true
                if (m != null) mil = m
                if (turnedOn || start - tDtc >= dtcSeconds * 1000L) {
                    tDtc = start
                    c.readDtcs(permanent)?.let { all ->
                        if (all != lastDtcs) {
                            val fresh = all - lastDtcs
                            lastDtcs = all
                            listener.onDtcs(all, fresh)
                        }
                    }
                }
            }
            val spent = now() - start
            if (spent < minCycleMs) Thread.sleep(minCycleMs - spent)
        }
    }

    private fun put(k: String, v: Double) {
        last[k] = Stamped(v, now())
        listener.onValue(k, v)
    }

    private fun get(k: String, maxAge: Long) = last[k]?.takeIf { now() - it.at <= maxAge }?.v ?: Double.NaN

    /** Derived values use only recent inputs, so an old reading never looks current. */
    private fun derived() {
        val map = get("obd:0B", 3000)
        if (!map.isNaN()) listener.onValue(Pids.BOOST, Derived.boostBar(map, get("obd:33", 600_000).let { if (it.isNaN()) 101.3 else it }))
        val c = Derived.consumption(get("obd:0D", 3000), get("obd:5E", 4000), get("obd:10", 3000), petrol)
        if (!c.isNaN()) listener.onValue(Pids.CONSUMPTION, c)
    }
}
