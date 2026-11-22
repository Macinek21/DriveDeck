using System;
using System.Collections.Generic;
using System.Linq;

namespace DriveDeck.Core
{
    public enum DtcKind { Stored, Pending, Permanent }

    public sealed class Dtc : IEquatable<Dtc>
    {
        public string Code { get; set; }
        public DtcKind Kind { get; set; }
        public string Ecu { get; set; }
        public string Description { get { return DtcDescriptions.Describe(Code); } }

        public bool Equals(Dtc other)
        {
            return other != null && other.Code == Code && other.Kind == Kind && other.Ecu == Ecu;
        }
        public override bool Equals(object obj) { return Equals(obj as Dtc); }
        public override int GetHashCode() { return ((Code ?? "") + "|" + Kind + "|" + (Ecu ?? "")).GetHashCode(); }
        public override string ToString() { return Code + " (" + Kind + ")"; }
    }

    public static class DtcDecoder
    {
        private static readonly char[] Systems = { 'P', 'C', 'B', 'U' };

        public static string Decode(byte hi, byte lo)
        {
            char sys = Systems[hi >> 6];
            int d1 = (hi >> 4) & 0x03;
            return sys.ToString() + d1 + (hi & 0x0F).ToString("X") + lo.ToString("X2");
        }

        /// <summary>Decodes a mode 03/07/0A response payload (starting with 0x43/0x47/0x4A).</summary>
        public static List<Dtc> DecodeResponse(byte[] payload, DtcKind kind, string ecu)
        {
            var list = new List<Dtc>();
            if (payload == null || payload.Length < 1) return list;
            int start = 1;
            // CAN responses carry a count byte after the service id.
            if (payload.Length >= 2 && (payload.Length - 2) % 2 == 0 && payload[1] == (payload.Length - 2) / 2) start = 2;
            for (int i = start; i + 1 < payload.Length; i += 2)
            {
                if (payload[i] == 0 && payload[i + 1] == 0) continue;
                list.Add(new Dtc { Code = Decode(payload[i], payload[i + 1]), Kind = kind, Ecu = ecu ?? "" });
            }
            return list;
        }

        /// <summary>Mode 01 PID 01 byte A: bit 7 = MIL (check-engine lamp) on, bits 0-6 = number of stored codes.</summary>
        public static void DecodeMonitorStatus(byte a, out bool milOn, out int count)
        {
            milOn = (a & 0x80) != 0;
            count = a & 0x7F;
        }
    }

    /// <summary>Plain-language descriptions: a table of frequent generic codes plus SAE J2012 group names as fallback.</summary>
    public static class DtcDescriptions
    {
        private static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "P0010", "Camshaft position actuator circuit (bank 1)" },
            { "P0011", "Camshaft timing over-advanced (bank 1)" },
            { "P0016", "Crankshaft/camshaft position correlation" },
            { "P0087", "Fuel rail pressure too low" },
            { "P0088", "Fuel rail pressure too high" },
            { "P0101", "Mass air flow sensor range/performance" },
            { "P0102", "Mass air flow sensor low input" },
            { "P0106", "Manifold pressure sensor range/performance" },
            { "P0112", "Intake air temperature sensor low" },
            { "P0113", "Intake air temperature sensor high" },
            { "P0115", "Coolant temperature sensor circuit" },
            { "P0117", "Coolant temperature sensor low" },
            { "P0118", "Coolant temperature sensor high" },
            { "P0121", "Throttle/pedal position sensor range/performance" },
            { "P0128", "Coolant below thermostat regulating temperature" },
            { "P0130", "O2 sensor circuit (bank 1 sensor 1)" },
            { "P0133", "O2 sensor slow response (bank 1 sensor 1)" },
            { "P0171", "System too lean (bank 1)" },
            { "P0172", "System too rich (bank 1)" },
            { "P0234", "Turbocharger overboost" },
            { "P0299", "Turbocharger underboost" },
            { "P0300", "Random/multiple cylinder misfire detected" },
            { "P0301", "Cylinder 1 misfire detected" },
            { "P0302", "Cylinder 2 misfire detected" },
            { "P0303", "Cylinder 3 misfire detected" },
            { "P0304", "Cylinder 4 misfire detected" },
            { "P0325", "Knock sensor circuit" },
            { "P0335", "Crankshaft position sensor circuit" },
            { "P0340", "Camshaft position sensor circuit" },
            { "P0380", "Glow plug circuit" },
            { "P0401", "EGR flow insufficient" },
            { "P0403", "EGR control circuit" },
            { "P0420", "Catalyst efficiency below threshold (bank 1)" },
            { "P0441", "EVAP system incorrect purge flow" },
            { "P0455", "EVAP system large leak" },
            { "P0500", "Vehicle speed sensor" },
            { "P0506", "Idle speed lower than expected" },
            { "P0507", "Idle speed higher than expected" },
            { "P0562", "System voltage low" },
            { "P0563", "System voltage high" },
            { "P0606", "Control module processor fault" },
            { "P0700", "Transmission control system (see gearbox codes)" },
            { "P2002", "Diesel particulate filter efficiency below threshold" },
            { "P2015", "Intake manifold runner position sensor range" },
            { "P2096", "Post-catalyst fuel trim too lean" },
            { "P2187", "System too lean at idle" },
            { "P242F", "Diesel particulate filter restriction – ash accumulation" },
            { "P2463", "Diesel particulate filter restriction – soot accumulation" },
            { "U0100", "Lost communication with engine control module" },
            { "U0101", "Lost communication with transmission control module" },
            { "U0121", "Lost communication with ABS module" },
            { "U0155", "Lost communication with instrument cluster" },
        };

        public static string Describe(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length != 5) return "Unknown code";
            string d;
            if (Known.TryGetValue(code, out d)) return d;
            char sys = char.ToUpperInvariant(code[0]);
            bool manufacturer = code[1] == '1' || code[1] == '3';
            string prefix = manufacturer ? "Manufacturer-specific: " : "";
            switch (sys)
            {
                case 'P':
                    switch (code[2])
                    {
                        case '0': return prefix + "Fuel/air metering or emissions control";
                        case '1': return prefix + "Fuel and air metering";
                        case '2': return prefix + "Fuel and air metering (injector circuit)";
                        case '3': return prefix + "Ignition system or misfire";
                        case '4': return prefix + "Auxiliary emission controls";
                        case '5': return prefix + "Vehicle speed, idle control or auxiliary inputs";
                        case '6': return prefix + "Computer or output circuit";
                        case '7': case '8': case '9': return prefix + "Transmission";
                        case 'A': case 'B': case 'C': return prefix + "Hybrid/electric propulsion";
                        default: return prefix + "Powertrain";
                    }
                case 'C': return prefix + "Chassis (brakes, steering, suspension)";
                case 'B': return prefix + "Body (airbags, lighting, comfort)";
                case 'U': return prefix + "Network/communication between modules";
                default: return "Unknown code";
            }
        }
    }
}
