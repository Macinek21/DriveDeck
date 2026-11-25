package io.github.macinek21.drivedeck.core

enum class DtcKind { Stored, Pending, Permanent }
data class Dtc(val code: String, val kind: DtcKind, val ecu: String) { val description get() = DtcText.describe(code) }

object DtcDecoder {
    fun decode(hi: Int, lo: Int) = "${"PCBU"[hi shr 6]}${(hi shr 4) and 3}${(hi and 0xF).toString(16).uppercase()}${Hex.format(intArrayOf(lo))}"

    /** Payload of mode 03/07/0A starting with 43/47/4A; CAN replies carry a count byte. */
    fun response(p: IntArray, kind: DtcKind, ecu: String): List<Dtc> {
        val start = if (p.size >= 2 && p.size % 2 == 0 && p[1] == (p.size - 2) / 2) 2 else 1
        return (start until p.size - 1 step 2).filter { p[it] != 0 || p[it + 1] != 0 }.map { Dtc(decode(p[it], p[it + 1]), kind, ecu) }
    }
}

object DtcText {
    private val known = """
P0011=Camshaft timing over-advanced (bank 1)
P0016=Crankshaft/camshaft position correlation
P0087=Fuel rail pressure too low
P0088=Fuel rail pressure too high
P0101=Mass air flow sensor range/performance
P0102=Mass air flow sensor low input
P0106=Manifold pressure sensor range/performance
P0112=Intake air temperature sensor low
P0113=Intake air temperature sensor high
P0117=Coolant temperature sensor low
P0118=Coolant temperature sensor high
P0121=Throttle/pedal position sensor range
P0128=Coolant below thermostat regulating temperature
P0130=O2 sensor circuit (bank 1 sensor 1)
P0133=O2 sensor slow response (bank 1 sensor 1)
P0171=System too lean (bank 1)
P0172=System too rich (bank 1)
P0234=Turbocharger overboost
P0299=Turbocharger underboost
P0300=Random/multiple cylinder misfire
P0301=Cylinder 1 misfire detected
P0302=Cylinder 2 misfire detected
P0303=Cylinder 3 misfire detected
P0304=Cylinder 4 misfire detected
P0325=Knock sensor circuit
P0335=Crankshaft position sensor circuit
P0340=Camshaft position sensor circuit
P0380=Glow plug circuit
P0401=EGR flow insufficient
P0403=EGR control circuit
P0420=Catalyst efficiency below threshold (bank 1)
P0441=EVAP incorrect purge flow
P0455=EVAP large leak
P0500=Vehicle speed sensor
P0506=Idle speed lower than expected
P0507=Idle speed higher than expected
P0562=System voltage low
P0563=System voltage high
P0606=Control module processor fault
P0700=Transmission control system (see gearbox codes)
P2002=Particulate filter efficiency below threshold
P2187=System too lean at idle
P242F=Particulate filter restriction, ash
P2463=Particulate filter restriction, soot
U0100=Lost communication with engine control module
U0101=Lost communication with transmission control module
U0121=Lost communication with ABS module
U0155=Lost communication with instrument cluster
""".trim().lines().associate { it.substringBefore('=') to it.substringAfter('=') }

    fun describe(c: String): String {
        if (c.length != 5) return "Unknown code"
        known[c]?.let { return it }
        val pre = if (c[1] == '1' || c[1] == '3') "Manufacturer-specific: " else ""
        return pre + when (c[0]) {
            'P' -> when (c[2]) {
                '0' -> "Fuel/air metering or emissions control"
                '1' -> "Fuel and air metering"
                '2' -> "Injector circuit"
                '3' -> "Ignition system or misfire"
                '4' -> "Auxiliary emission controls"
                '5' -> "Vehicle speed or idle control"
                '6' -> "Computer or output circuit"
                '7', '8', '9' -> "Transmission"
                'A', 'B', 'C' -> "Hybrid/electric propulsion"
                else -> "Powertrain"
            }
            'C' -> "Chassis (brakes, steering, suspension)"
            'B' -> "Body (airbags, lighting, comfort)"
            'U' -> "Network/communication between modules"
            else -> "Unknown code"
        }
    }
}
