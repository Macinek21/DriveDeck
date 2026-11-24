package io.github.macinek21.drivedeck.core

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.ZoneOffset
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList

class FormulaTest {
    private fun ev(f: String, vararg d: Int) = Formula.parse(f).evaluate(d)

    @Test fun evaluates() {
        assertEquals(1726.0, ev("(A*256+B)/4", 0x1A, 0xF8), 1e-9)
        assertEquals(83.0, ev("A-40", 0x7B), 1e-9)
        assertEquals(-1.0, ev("signed8(A)", 0xFF), 1e-9)
        assertEquals(-10.0, ev("signed16(A*256+B)/10", 0xFF, 0x9C), 1e-9)
        assertEquals(1.0, ev("bit(A,7)", 0x80), 1e-9)
        assertEquals(7.0, ev("max(A,10)-min(B,3)", 5, 9), 1e-9)
        assertEquals(5.0, ev("-A+2*3", 1), 1e-9)
        assertEquals(4.0, ev("2*(3+4)%5"), 1e-9)
        assertEquals(2.0, ev("8-4-2"), 1e-9)
    }

    @Test fun rejects() {
        for (f in listOf("", "A+", "(A", "Z*2", "java.io.File(A)", "A;B", "abs(A,B)", "1..2", "1".repeat(300), "(".repeat(60) + "1" + ")".repeat(60), "-".repeat(60) + "1"))
            assertNotNull(f, Formula.error(f))
    }

    @Test fun badInputIsNaN() {
        assertTrue(ev("A*256+B", 1).isNaN())
        assertTrue(ev("A/B", 1, 0).isNaN())
    }
}

class ElmTest {
    @Test fun multiEcu() {
        val r = ElmParser.parse("7E8 03 41 0D 32\r7E9 03 41 0D 32\r\r>", "010D")
        assertEquals(listOf("7E8", "7E9"), r.messages.map { it.header })
        assertArrayEquals(intArrayOf(0x41, 0x0D, 0x32), r.messages[0].payload)
    }

    @Test fun isoTp() {
        val r = ElmParser.parse("7E8 10 08 43 03 01 33 03 01\r7E8 21 06 04 55 55 55 55 55\r>", "03")
        assertArrayEquals(intArrayOf(0x43, 3, 1, 0x33, 3, 1, 6, 4), r.messages.single().payload)
    }

    @Test fun incompleteOrOutOfOrderIsDropped() {
        assertTrue(ElmParser.parse("7E8 10 08 43 03 01 33 03 01\r>", "03").messages.isEmpty())
        assertTrue(ElmParser.parse("7E8 10 08 43 03 01 33 03 01\r7E8 22 06 04 55 55 55 55 55\r>", "03").messages.isEmpty())
    }

    @Test fun headersOffAnd29Bit() {
        assertEquals(8, ElmParser.parse("008\r0: 43 03 01 33 03 01\r1: 06 04 00 00 00 00 00\r>", "03").messages.single().payload.size)
        assertEquals("18DAF110", ElmParser.parse("18 DA F1 10 03 41 0D 32\r>", "010D").messages.single().header)
    }

    @Test fun statuses() {
        for ((raw, st) in listOf("SEARCHING...\rUNABLE TO CONNECT\r>" to ElmStatus.ERROR, "NO DATA\r>" to ElmStatus.NO_DATA, "CAN ERROR\r>" to ElmStatus.ERROR,
            "?\r>" to ElmStatus.ERROR, "STOPPED\r>" to ElmStatus.ERROR, "" to ElmStatus.NO_DATA)) assertEquals(raw, st, ElmParser.parse(raw, "010C").status)
    }

    @Test fun garbageNeverThrows() {
        assertEquals(1, ElmParser.parse("010C\rSEARCHING...\r\u0001ÿ%%\r7E8 04 41 0C 1A F8\r>", "010C").messages.size)
        val rnd = java.util.Random(1)
        repeat(3000) { ElmParser.parse(String(CharArray(rnd.nextInt(120)) { "0123456789ABCDEF :>\r7E8 10 21?\u0000".random() }), "03") }
        ElmParser.parse("7E8 10 FF 43 01 02 03 04 05\r".repeat(5000), "03")
    }
}

class GuardTest {
    @Test fun requests() {
        for (ok in listOf("01 0C", "22 11 BD", "03", "19 02 08")) assertNull(ok, CommandGuard.requestError(ok))
        for (bad in listOf("04", "14 FF FF FF", "2E 11 BD 00", "27 01", "31 01 02", "10 03", "11 01", "2F 01", "08 01", "19 04 00",
            "01 0C\rATMA", "01 0C;04", "010", "01 02 03 04 05 06 07 08", "")) assertNotNull(bad, CommandGuard.requestError(bad))
    }

    @Test fun headersAndAt() {
        for (ok in listOf("", "7E0", "714")) assertNull(CommandGuard.headerError(ok))
        for (bad in listOf("7E0\r", "123", "7E0ATMA", "18DA10F1")) assertNotNull(CommandGuard.headerError(bad))
        assertTrue(CommandGuard.atAllowed("ATSH7E0"))
        for (bad in listOf("ATMA", "ATSH100", "ATSH7E\r", "ATPP0CSV00")) assertFalse(CommandGuard.atAllowed(bad))
    }
}

class DtcTest {
    @Test fun decodes() {
        assertEquals(listOf("P0133", "U0100", "C0234", "B1101", "P242F"),
            listOf(0x01 to 0x33, 0xC1 to 0, 0x42 to 0x34, 0x91 to 0x01, 0x24 to 0x2F).map { DtcDecoder.decode(it.first, it.second) })
        assertEquals(listOf("P0133", "P0301", "P0604"), DtcDecoder.response(intArrayOf(0x43, 3, 1, 0x33, 3, 1, 6, 4), DtcKind.Stored, "").map { it.code })
        assertTrue(DtcDecoder.response(intArrayOf(0x43, 0), DtcKind.Stored, "").isEmpty())
        assertEquals("Cylinder 1 misfire detected", DtcText.describe("P0301"))
        assertTrue(DtcText.describe("P1234").startsWith("Manufacturer"))
    }
}

class ObdTest {
    private fun client(s: ElmSimulator) = ObdClient(s).also { it.init("6") }
    private fun pid(k: String) = Pids.std(k)

    @Test fun initAndRead() {
        val s = ElmSimulator()
        val c = client(s)
        assertEquals("ELM327 v1.5", c.version)
        assertTrue(c.supported.containsAll(listOf(0x04, 0x05, 0x0C, 0x11, 0x2F, 0x33, 0x42, 0x46, 0x49, 0x5C, 0x5E)))
        assertTrue("ATSP6" in s.sent)
        assertEquals(1726.0, c.read(pid("0C")).value, 0.0)
        assertEquals(83.0, c.read(pid("05")).value, 0.0)
        assertTrue(c.read(pid("5C")).unsupported)
        assertEquals(12.6, c.readVoltage().value, 1e-9)
    }

    @Test fun customHeaderSwitchesBack() {
        val s = ElmSimulator()
        val c = client(s)
        assertEquals(14.0, c.read(ValueDef("custom:x", "x", "x", "", "22 11 BD", "7E0", "(A*256+B)/10")).value, 1e-9)
        c.read(pid("0C"))
        assertEquals("ATSH7DF", s.sent[s.sent.size - 2])
    }

    @Test fun blockedNeverReachesAdapter() {
        val s = ElmSimulator()
        val c = client(s)
        val n = s.sent.size
        assertFalse(c.read(ValueDef("a", "a", "a", "", "2E 11 BD 00", "7E0", "A")).ok)
        assertFalse(c.read(ValueDef("a", "a", "a", "", "01 0C", "7E0\rATMA", "A")).ok)
        assertEquals(n, s.sent.size)
    }

    @Test fun faults() {
        val s = ElmSimulator()
        val c = client(s)
        val d = c.readDtcs(true)!!
        assertTrue(Dtc("P0133", DtcKind.Stored, "Engine") in d)
        assertTrue(Dtc("P0171", DtcKind.Pending, "Engine") in d)
        assertEquals(true, c.readMil())
        for (bad in listOf("CAN ERROR", "NO DATA", "7E8 10 08 43 03 01 33 03 01")) { s.dtcReply = bad; assertNull(bad, c.readDtcs(false)) }
    }

    private class Rec : ObdPoller.Listener {
        val values = ConcurrentHashMap<String, Double>()
        val states = CopyOnWriteArrayList<ObdState>()
        val dtcEvents = CopyOnWriteArrayList<Pair<Set<Dtc>, Set<Dtc>>>()
        override fun onValue(key: String, v: Double) { values[key] = v }
        override fun onUnsupported(key: String) {}
        override fun onState(s: ObdState, msg: String) { states += s }
        override fun onDtcs(all: Set<Dtc>, fresh: Set<Dtc>) { dtcEvents += all to fresh }
    }

    private fun until(ms: Long, cond: () -> Boolean): Boolean {
        val end = System.currentTimeMillis() + ms
        while (System.currentTimeMillis() < end) { if (cond()) return true; Thread.sleep(20) }
        return cond()
    }

    @Test fun pollerPublishes() {
        val r = Rec()
        val p = ObdPoller({ ElmSimulator() }, "6", 30, false, true, r, 10)
        p.setWanted(listOf(pid("0C")) + Pids.special)
        p.start()
        assertTrue(until(5000) { r.dtcEvents.isNotEmpty() && r.values.containsKey(Pids.CONSUMPTION) && r.values.containsKey(Pids.VOLTAGE) })
        p.stop()
        assertEquals(1726.0, r.values["obd:0C"]!!, 0.0)
        assertEquals(0.5, r.values[Pids.BOOST]!!, 1e-9)
        assertTrue(r.dtcEvents[0].second.any { it.code == "P0133" })
    }

    @Test fun pollerReconnectsAndDoesNotRepeatOldFaults() {
        var n = 0
        val r = Rec()
        val p = ObdPoller({ n++; ElmSimulator(failAfter = if (n == 1) 40 else Int.MAX_VALUE) }, "6", 30, false, true, r, 10)
        p.setWanted(listOf(pid("0C")))
        p.start()
        assertTrue(until(8000) { r.states.count { it == ObdState.CONNECTED } == 2 })
        Thread.sleep(300)
        p.stop()
        assertTrue(ObdState.RECONNECTING in r.states)
        assertEquals(1, r.dtcEvents.size)
    }
}

class RecordingTest {
    private val mb = 1024L * 1024
    private val t0 = 1_800_000_000_000L
    private fun segs(n: Int, size: Long = 100 * mb) = List(n) { Segment("s$it", t0 + it * 60_000, t0 + (it + 1) * 60_000, size) }

    @Test fun loopPolicy() {
        val s = segs(10)
        assertEquals(s.take(3), LoopPolicy.toDelete(s, 800 * mb, 10_000 * mb, 1000 * mb, 100 * mb))
        assertTrue(LoopPolicy.toDelete(s, null, 5000 * mb, 1000 * mb, 100 * mb).isEmpty())
        assertEquals(1, LoopPolicy.toDelete(s, null, 1000 * mb, 1000 * mb, 100 * mb).size)
        s[0].inProgress = true
        assertFalse(s[0] in LoopPolicy.toDelete(s, 10 * mb, 0, 1000 * mb, 100 * mb))
    }

    @Test fun eventCoversFullMinuteBefore() {
        val s = segs(6)
        val t = t0 + 3 * 60_000 + 20_000
        assertEquals(listOf("s2", "s3"), LoopPolicy.forEvent(s, t, 60_000, t + 30_000).map { it.name })
        assertEquals(listOf("s2", "s3", "s4"), LoopPolicy.forEvent(s, t, 60_000, t + 50_000).map { it.name })
    }

    @Test fun names() {
        val n = Names.of(t0, ZoneOffset.UTC)
        assertEquals(t0, Names.parse(n, ZoneOffset.UTC))
        assertNotNull(Names.parse("20261122_140312 (2)"))
        assertNotNull(Names.parse("20261122_140312_impact"))
        for (bad in listOf("../evil", "20261122_140312 wedding", "20261122_140312_impact/../x", "20261399_140312", "")) assertNull(bad, Names.parse(bad))
    }

    private fun rest(d: ImpactDetector, from: Int, n: Int, stepMs: Int = 20): Int {
        for (i in from until from + n) assertFalse(d.feed(0.0, 0.0, 1.0, i.toLong() * stepMs))
        return from + n
    }

    @Test fun impactOnceWithCooldown() {
        val d = ImpactDetector(2.0)
        var i = rest(d, 0, 500)
        assertTrue(d.feed(3.5, 0.0, 1.0, i++ * 20L))
        assertFalse(d.feed(-3.5, 0.0, 1.0, i++ * 20L))
        i = rest(d, i, 600)
        assertTrue(d.feed(0.0, 4.0, 1.0, i * 20L))
    }

    @Test fun impactIgnoresTiltPotholeAndMounting() {
        val d = ImpactDetector(ImpactDetector.preset("medium"))
        for (i in 0 until 1000) assertFalse(d.feed(0.5, -0.7, 0.5, i * 20L))
        assertFalse(d.feed(0.5, -0.7, 1.5, 20_020))
        val m = ImpactDetector(1.0)
        m.feed(0.0, 0.0, 1.0, 0)
        assertFalse(m.feed(5.0, 5.0, 5.0, 500))
    }

    @Test fun impactWorksAtFastSampleRates() {
        val d = ImpactDetector(2.0)
        val i = rest(d, 0, 4000, 2)
        assertTrue(d.feed(3.0, 0.0, 1.0, i * 2L))
    }

    @Test fun stampSrtGpx() {
        val fix = Fix(t0, 50.081234, 14.423111, speedKmh = 36.0)
        assertTrue(Stamp.text(t0, fix, 87.4, true, true, true, "1AB 2345\r\nX", ZoneOffset.UTC).endsWith("87 km/h   N50.08123 E14.42311   1AB 2345X"))
        assertEquals("-- km/h   no GPS", Stamp.text(t0, null, null, false, true, true, ""))
        val s = Srt(t0)
        s.add(t0 + 1000, t0 + 2000, "a\r\nb")
        s.add(t0 - 500, t0 - 100, "before video: dropped")
        assertEquals("1\r\n00:00:01,000 --> 00:00:02,000\r\na  b\r\n\r\n", s.toString())
        val cue = Srt.parse(s.toString()).single()
        assertEquals(1000L, cue.from); assertEquals("a  b", cue.text)
        assertTrue(Srt.parse("garbage\n\n99 --> x").isEmpty())
        val g = Gpx.build("<x>&", listOf(fix))
        assertTrue("&lt;x&gt;&amp;" in g && "<speed>10.00</speed>" in g)
    }

    @Test fun gpsSpeed() {
        val a = Fix(t0, 50.0, 14.0)
        assertEquals(40.0, Geo.speed(a, Fix(t0 + 1000, 50.0001, 14.0, accuracy = 5.0))!!, 1.0)
        assertEquals(55.0, Geo.speed(a, Fix(t0 + 1000, 50.0, 14.0, speedKmh = 55.0))!!, 0.0)
        assertNull(Geo.speed(null, a))
    }
}

class SettingsTest {
    @Test fun defaultsRoundTrip() {
        val s = Settings()
        assertTrue(s.normalize().isEmpty())
        val p = Settings.fromJson(s.toJson())
        assertNull(p.error)
        assertTrue(p.notes.isEmpty())
        assertEquals(s.toJson(), p.settings!!.toJson())
        assertNull(p.settings!!.pages[0].tiles[0].warnAbove)
        assertEquals(5500.0, p.settings!!.pages[0].tiles[1].warnAbove!!, 0.0)
    }

    @Test fun hostileImportIsSanitised() {
        val s = Settings()
        s.cam.bitrateMbps = Int.MAX_VALUE; s.cam.segmentSeconds = -5; s.cam.capGb = 1e300; s.cam.emergency = "+420900123456"
        s.ui.accent = "red"; s.obd.address = "'; rm -rf"; s.pages[0].rows = 1000; s.pages[0].tiles[0].cols = 99; s.pages[0].tiles[2].key = "obd:ZZ"
        s.pages[0].tiles[3].id = s.pages[0].tiles[4].id
        fun v(n: String, r: String, h: String) = ValueDef("", n, "", "", r, h, "(A*256+B)/10")
        s.obd.custom.addAll(listOf(v("clear", "04", ""), v("inject", "01 0C", "7E0\rATMA"), v("ok", "22 11 bd", "7e0")))
        val r = Settings.fromJson(s.toJson())
        val q = r.settings!!
        assertEquals(30, q.cam.bitrateMbps); assertEquals(30, q.cam.segmentSeconds); assertEquals(1024.0, q.cam.capGb, 0.0)
        assertEquals("112", q.cam.emergency); assertEquals("#FF3FA9F5", q.ui.accent); assertEquals("", q.obd.address)
        assertEquals(6, q.pages[0].rows)
        assertTrue(q.pages[0].tiles.all { it.col + it.cols <= q.pages[0].cols && it.row + it.rows <= q.pages[0].rows })
        assertEquals(Pids.GPS_SPEED, q.pages[0].tiles[2].key)
        assertEquals(q.pages[0].tiles.size, q.pages[0].tiles.map { it.id }.toSet().size)
        assertEquals("22 11 BD", q.obd.custom.single().request)
        assertTrue(r.notes.isNotEmpty())
    }

    @Test fun malformedIsRefused() {
        for (bad in listOf("", "{not json", "[1,2,3]", "{}", "{\"a\":1}", " ".repeat(Settings.MAX_CHARS + 1), "{\"cam\":" + "[".repeat(50_000)))
            assertNotNull(Settings.fromJson(bad).error)
        for (odd in listOf("{\"cam\":5}", "{\"pages\":[null,1,{\"tiles\":[{\"kind\":\"EVIL\"},7]}]}", "{\"obd\":{\"custom\":[{}]}}"))
            assertNotNull(Settings.fromJson(odd).settings)
    }
}
