using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace DriveDeck.Core
{
    /// <summary>A readable value: a standard PID, a user-defined request, or a derived/sensor value.</summary>
    [DataContract]
    public sealed class ValueDefinition
    {
        /// <summary>Unique key, e.g. "obd:0C", "custom:oiltemp", "gps:speed", "derived:boost".</summary>
        [DataMember] public string Key { get; set; }
        [DataMember] public string Name { get; set; }
        [DataMember] public string ShortName { get; set; }
        [DataMember] public string Unit { get; set; }
        /// <summary>Request bytes as hex, e.g. "01 0C" or "22 11 BD". Empty for non-OBD values.</summary>
        [DataMember] public string Request { get; set; }
        /// <summary>Optional CAN header to address a specific ECU (e.g. "7E0"). Empty = broadcast/default.</summary>
        [DataMember] public string Header { get; set; }
        [DataMember] public string Formula { get; set; }
        [DataMember] public double Min { get; set; }
        [DataMember] public double Max { get; set; }
        [DataMember] public int Decimals { get; set; }
        /// <summary>Polling class: 0 = every cycle (fast), 1 = medium (~1 s), 2 = slow (~5 s).</summary>
        [DataMember] public int Priority { get; set; }

        public ValueDefinition Clone() { return (ValueDefinition)MemberwiseClone(); }
    }

    public static class PidCatalog
    {
        public const string GpsSpeedKey = "gps:speed";
        public const string GpsAltitudeKey = "gps:altitude";
        public const string AdapterVoltageKey = "adapter:voltage";
        public const string BoostKey = "derived:boost";
        public const string ConsumptionKey = "derived:consumption";
        public const string ImpactGKey = "sensor:gforce";

        private static ValueDefinition Std(string pid, string name, string shortName, string unit, string formula,
                                           double min, double max, int decimals, int priority)
        {
            return new ValueDefinition
            {
                Key = "obd:" + pid, Name = name, ShortName = shortName, Unit = unit, Request = "01 " + pid,
                Header = "", Formula = formula, Min = min, Max = max, Decimals = decimals, Priority = priority
            };
        }

        /// <summary>Standard SAE J1979 mode 01 values that are commonly supported by VW group cars (incl. Leon Mk4).</summary>
        public static readonly IReadOnlyList<ValueDefinition> Standard = new List<ValueDefinition>
        {
            Std("0C", "Engine speed", "RPM", "rpm", "(A*256+B)/4", 0, 7000, 0, 0),
            Std("0D", "Vehicle speed (OBD)", "Speed", "km/h", "A", 0, 240, 0, 0),
            Std("04", "Engine load", "Load", "%", "A*100/255", 0, 100, 0, 0),
            Std("11", "Throttle position", "Throttle", "%", "A*100/255", 0, 100, 0, 0),
            Std("49", "Accelerator pedal", "Pedal", "%", "A*100/255", 0, 100, 0, 0),
            Std("0B", "Intake manifold pressure", "MAP", "kPa", "A", 0, 255, 0, 0),
            Std("10", "Mass air flow", "MAF", "g/s", "(A*256+B)/100", 0, 250, 1, 0),
            Std("0E", "Ignition timing advance", "Timing", "°", "A/2-64", -20, 50, 1, 1),
            Std("05", "Coolant temperature", "Coolant", "°C", "A-40", -40, 130, 0, 1),
            Std("0F", "Intake air temperature", "IAT", "°C", "A-40", -40, 100, 0, 1),
            Std("5C", "Engine oil temperature", "Oil", "°C", "A-40", -40, 150, 0, 1),
            Std("06", "Short term fuel trim B1", "STFT", "%", "A*100/128-100", -25, 25, 1, 1),
            Std("07", "Long term fuel trim B1", "LTFT", "%", "A*100/128-100", -25, 25, 1, 2),
            Std("5E", "Engine fuel rate", "Fuel rate", "L/h", "(A*256+B)/20", 0, 40, 1, 1),
            Std("23", "Fuel rail pressure", "Rail", "bar", "(A*256+B)/10", 0, 2500, 0, 1),
            Std("42", "ECU supply voltage", "ECU V", "V", "(A*256+B)/1000", 10, 16, 2, 2),
            Std("2F", "Fuel tank level", "Fuel", "%", "A*100/255", 0, 100, 0, 2),
            Std("33", "Barometric pressure", "Baro", "kPa", "A", 70, 110, 0, 2),
            Std("46", "Ambient air temperature", "Outside", "°C", "A-40", -40, 60, 0, 2),
            Std("1F", "Run time since start", "Run time", "s", "A*256+B", 0, 36000, 0, 2),
            Std("31", "Distance since codes cleared", "Dist clr", "km", "A*256+B", 0, 65535, 0, 2),
        };

        /// <summary>Values that do not come from a single OBD request.</summary>
        public static readonly IReadOnlyList<ValueDefinition> Special = new List<ValueDefinition>
        {
            new ValueDefinition { Key = GpsSpeedKey, Name = "GPS speed", ShortName = "GPS", Unit = "km/h", Min = 0, Max = 240, Decimals = 0 },
            new ValueDefinition { Key = GpsAltitudeKey, Name = "GPS altitude", ShortName = "Alt", Unit = "m", Min = 0, Max = 3000, Decimals = 0 },
            new ValueDefinition { Key = AdapterVoltageKey, Name = "Battery voltage (at OBD port)", ShortName = "Battery", Unit = "V", Min = 10, Max = 16, Decimals = 1, Priority = 2 },
            new ValueDefinition { Key = BoostKey, Name = "Boost pressure (MAP − baro)", ShortName = "Boost", Unit = "bar", Min = -1, Max = 2, Decimals = 2 },
            new ValueDefinition { Key = ConsumptionKey, Name = "Instant consumption", ShortName = "Cons.", Unit = "L/100km", Min = 0, Max = 30, Decimals = 1 },
            new ValueDefinition { Key = ImpactGKey, Name = "G-force (phone)", ShortName = "G", Unit = "g", Min = 0, Max = 3, Decimals = 2 },
        };

        public static IEnumerable<ValueDefinition> All(IEnumerable<ValueDefinition> custom)
        {
            return Standard.Concat(Special).Concat(custom ?? Enumerable.Empty<ValueDefinition>());
        }

        public static ValueDefinition Find(string key, IEnumerable<ValueDefinition> custom)
        {
            return All(custom).FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }
}
