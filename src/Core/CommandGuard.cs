using System;
using System.Globalization;
using System.Linq;

namespace DriveDeck.Core
{
    /// <summary>
    /// Safety gate for everything sent to the car. DriveDeck is strictly read-only:
    /// only diagnostic READ services are allowed, and requests/headers must be plain hex,
    /// so a user-entered value can never smuggle extra ELM327 commands (e.g. via "\r").
    /// Blocked on purpose: 0x04/0x14 clear codes, 0x08/0x2F actuator control, 0x10 session change,
    /// 0x11 ECU reset, 0x27 security access, 0x2E write data, 0x31 routines, 0x34-0x37 flashing, 0x3D memory write, 0x85, 0x28.
    /// </summary>
    public static class CommandGuard
    {
        /// <summary>Read-only services: OBD modes 01, 02, 03, 07, 09, 0A and UDS/KWP read services 0x21, 0x22, 0x19 (read DTC info).</summary>
        private static readonly byte[] AllowedServices = { 0x01, 0x02, 0x03, 0x07, 0x09, 0x0A, 0x19, 0x21, 0x22 };
        public const int MaxRequestBytes = 7;

        public static bool IsAllowedRequest(string requestHex, out byte[] bytes, out string reason)
        {
            bytes = null;
            reason = null;
            if (string.IsNullOrWhiteSpace(requestHex)) { reason = "Request is empty."; return false; }
            foreach (char c in requestHex)
            {
                if (c != ' ' && !Hex.IsHexChar(c)) { reason = "Request may contain only hex digits and spaces."; return false; }
            }
            bytes = Hex.TryParseBytes(requestHex);
            if (bytes == null) { reason = "Request must be whole bytes, e.g. \"22 11 BD\"."; return false; }
            if (bytes.Length > MaxRequestBytes) { reason = "Request is longer than " + MaxRequestBytes + " bytes."; return false; }
            if (!AllowedServices.Contains(bytes[0]))
            {
                reason = "Service 0x" + bytes[0].ToString("X2") + " is not a read-only service and is blocked.";
                bytes = null;
                return false;
            }
            if (bytes[0] == 0x19 && bytes.Length >= 2 && bytes[1] != 0x01 && bytes[1] != 0x02 && bytes[1] != 0x0A)
            {
                reason = "Only ReadDTCInformation sub-functions 01, 02 and 0A are allowed.";
                bytes = null;
                return false;
            }
            return true;
        }

        /// <summary>Allowed CAN request headers: 11-bit, in the diagnostic range 0x600-0x7FF.</summary>
        public static bool IsAllowedHeader(string header, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(header)) return true;
            if (header.Length != 3 || !Hex.IsHexToken(header)) { reason = "Header must be 3 hex digits, e.g. 7E0."; return false; }
            int v = int.Parse(header, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (v < 0x600 || v > 0x7FF) { reason = "Header must be between 600 and 7FF."; return false; }
            return true;
        }

        /// <summary>The fixed set of AT commands the app itself issues. Anything else is refused.</summary>
        private static readonly string[] AllowedAt =
        {
            "ATZ", "ATWS", "ATE0", "ATL0", "ATS1", "ATH1", "ATH0", "ATAT1", "ATAT2", "ATCAF1", "ATRV", "ATDPN", "ATI", "AT@1",
            "ATSP0", "ATSP6", "ATSP7", "ATSP8", "ATSP9", "ATST32", "ATST64", "ATST96", "ATSH7DF", "ATD"
        };

        public static bool IsAllowedAtCommand(string cmd)
        {
            if (cmd == null) return false;
            string c = cmd.Trim().ToUpperInvariant();
            if (AllowedAt.Contains(c)) return true;
            // ATSHxxx with a validated diagnostic header.
            if (c.StartsWith("ATSH") && c.Length == 7)
            {
                string r;
                return IsAllowedHeader(c.Substring(4), out r);
            }
            return false;
        }
    }
}
