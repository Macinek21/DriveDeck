package io.github.macinek21.drivedeck.core

/** A value that can be shown: OBD PID, user-defined request, or derived/sensor value (no request). priority: 0 fast, 1 ~1 s, 2 ~5 s. */
data class ValueDef(
    val key: String, val name: String, val short: String, val unit: String, val request: String = "", val header: String = "",
    val formula: String = "", val min: Double = 0.0, val max: Double = 100.0, val decimals: Int = 0, val priority: Int = 0,
)

object Pids {
    const val GPS_SPEED = "gps:speed"
    const val GPS_ALT = "gps:altitude"
    const val VOLTAGE = "adapter:voltage"
    const val BOOST = "derived:boost"
    const val CONSUMPTION = "derived:consumption"
    const val GFORCE = "sensor:gforce"

    private fun s(pid: String, name: String, short: String, unit: String, f: String, min: Number, max: Number, dec: Int, prio: Int) =
        ValueDef("obd:$pid", name, short, unit, "01 $pid", "", f, min.toDouble(), max.toDouble(), dec, prio)

    val standard = listOf(
        s("0C", "Engine speed", "RPM", "rpm", "(A*256+B)/4", 0, 7000, 0, 0),
        s("0D", "Vehicle speed (OBD)", "Speed", "km/h", "A", 0, 240, 0, 0),
        s("04", "Engine load", "Load", "%", "A*100/255", 0, 100, 0, 0),
        s("11", "Throttle position", "Throttle", "%", "A*100/255", 0, 100, 0, 0),
        s("49", "Accelerator pedal", "Pedal", "%", "A*100/255", 0, 100, 0, 0),
        s("0B", "Intake manifold pressure", "MAP", "kPa", "A", 0, 255, 0, 0),
        s("10", "Mass air flow", "MAF", "g/s", "(A*256+B)/100", 0, 250, 1, 0),
        s("0E", "Ignition timing advance", "Timing", "°", "A/2-64", -20, 50, 1, 1),
        s("05", "Coolant temperature", "Coolant", "°C", "A-40", -40, 130, 0, 1),
        s("0F", "Intake air temperature", "IAT", "°C", "A-40", -40, 100, 0, 1),
        s("5C", "Engine oil temperature", "Oil", "°C", "A-40", -40, 150, 0, 1),
        s("06", "Short term fuel trim B1", "STFT", "%", "A*100/128-100", -25, 25, 1, 1),
        s("07", "Long term fuel trim B1", "LTFT", "%", "A*100/128-100", -25, 25, 1, 2),
        s("5E", "Engine fuel rate", "Fuel rate", "L/h", "(A*256+B)/20", 0, 40, 1, 1),
        s("23", "Fuel rail pressure", "Rail", "bar", "(A*256+B)/10", 0, 2500, 0, 1),
        s("42", "ECU supply voltage", "ECU V", "V", "(A*256+B)/1000", 10, 16, 2, 2),
        s("2F", "Fuel tank level", "Fuel", "%", "A*100/255", 0, 100, 0, 2),
        s("33", "Barometric pressure", "Baro", "kPa", "A", 70, 110, 0, 2),
        s("46", "Ambient air temperature", "Outside", "°C", "A-40", -40, 60, 0, 2),
        s("1F", "Run time since start", "Run time", "s", "A*256+B", 0, 36000, 0, 2),
        s("31", "Distance since codes cleared", "Dist clr", "km", "A*256+B", 0, 65535, 0, 2),
    )
    val special = listOf(
        ValueDef(GPS_SPEED, "GPS speed", "GPS", "km/h", max = 240.0),
        ValueDef(GPS_ALT, "GPS altitude", "Alt", "m", max = 3000.0),
        ValueDef(VOLTAGE, "Battery voltage (OBD port)", "Battery", "V", min = 10.0, max = 16.0, decimals = 1, priority = 2),
        ValueDef(BOOST, "Boost pressure (MAP - baro)", "Boost", "bar", min = -1.0, max = 2.0, decimals = 2),
        ValueDef(CONSUMPTION, "Instant consumption", "Cons.", "L/100km", max = 30.0, decimals = 1),
        ValueDef(GFORCE, "G-force (phone)", "G", "g", max = 3.0, decimals = 2),
    )

    fun all(custom: List<ValueDef>) = standard + special + custom
    fun std(pid: String) = standard.first { it.key == "obd:$pid" }
}

object Derived {
    fun boostBar(mapKpa: Double, baroKpa: Double) = (mapKpa - baroKpa) / 100.0

    /** L/100 km from the fuel rate, or for petrol only from MAF. NaN when unknown or nearly stopped. */
    fun consumption(speed: Double, fuelRate: Double, maf: Double, petrol: Boolean): Double {
        if (speed.isNaN() || speed < 5) return Double.NaN
        val lh = if (!fuelRate.isNaN()) fuelRate else if (petrol && !maf.isNaN()) maf * 3600.0 / (14.7 * 745.0) else return Double.NaN
        return minOf(lh / speed * 100.0, 99.9)
    }
}
