package io.github.macinek21.drivedeck.core

/**
 * Everything sent to the car passes here. DriveDeck is read-only: only diagnostic READ services, plain hex, fixed AT commands.
 * Blocked on purpose: 04/14 clear codes, 08/2F actuators, 10 sessions, 11 reset, 27 security access, 2E write, 31 routines, 34-37 flashing.
 */
object CommandGuard {
    private val SERVICES = setOf(0x01, 0x02, 0x03, 0x07, 0x09, 0x0A, 0x19, 0x21, 0x22)
    const val MAX_BYTES = 7
    private val AT = setOf("ATZ", "ATE0", "ATL0", "ATS1", "ATH1", "ATAT1", "ATRV", "ATDPN", "ATSP0", "ATSP6", "ATSP7", "ATSP8", "ATSP9")

    /** null when the request is allowed, otherwise the reason it is refused. */
    fun requestError(hex: String?): String? {
        if (hex.isNullOrBlank()) return "Request is empty."
        if (hex.any { it != ' ' && !Hex.isHexChar(it) }) return "Request may contain only hex digits and spaces."
        val b = Hex.parse(hex) ?: return "Request must be whole bytes, e.g. \"22 11 BD\"."
        if (b.size > MAX_BYTES) return "Request is longer than $MAX_BYTES bytes."
        if (b[0] !in SERVICES) return "Service 0x${Hex.format(intArrayOf(b[0]))} is not read-only and is blocked."
        if (b[0] == 0x19 && b.size >= 2 && b[1] !in setOf(0x01, 0x02, 0x0A)) return "Only ReadDTCInformation 01, 02 and 0A are allowed."
        return null
    }

    fun headerError(h: String): String? {
        if (h.isEmpty()) return null
        if (h.length != 3 || !Hex.isHexToken(h)) return "Header must be 3 hex digits, e.g. 7E0."
        return if (h.toInt(16) in 0x600..0x7FF) null else "Header must be between 600 and 7FF."
    }

    fun atAllowed(c: String) = c in AT || (c.length == 7 && c.startsWith("ATSH") && headerError(c.substring(4)) == null)
}
