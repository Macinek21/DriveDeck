using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace DriveDeck.Core
{
    public enum WidgetKind { Number, Gauge, Bar, Graph, Camera, Clock, Status }

    [DataContract]
    public sealed class WidgetConfig
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public WidgetKind Kind { get; set; }
        [DataMember] public string ValueKey { get; set; }
        [DataMember] public int Row { get; set; }
        [DataMember] public int Column { get; set; }
        [DataMember] public int RowSpan { get; set; }
        [DataMember] public int ColumnSpan { get; set; }
        /// <summary>Optional label override; empty = value's short name.</summary>
        [DataMember] public string Label { get; set; }
        /// <summary>Hex colour such as "#FF3FA9F5"; empty = theme accent.</summary>
        [DataMember] public string Color { get; set; }
        /// <summary>Warn (amber) and alarm (red) thresholds; NaN = off.</summary>
        [DataMember] public double WarnAbove { get; set; }
        [DataMember] public double AlarmAbove { get; set; }
        [DataMember] public double WarnBelow { get; set; }
        /// <summary>Override of gauge range; NaN = use value definition.</summary>
        [DataMember] public double Min { get; set; }
        [DataMember] public double Max { get; set; }

        public WidgetConfig()
        {
            Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            RowSpan = 1; ColumnSpan = 1; Label = ""; Color = "";
            WarnAbove = double.NaN; AlarmAbove = double.NaN; WarnBelow = double.NaN; Min = double.NaN; Max = double.NaN;
        }
    }

    [DataContract]
    public sealed class DashboardPage
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public int Rows { get; set; }
        [DataMember] public int Columns { get; set; }
        /// <summary>"any", "landscape" or "portrait": which orientation shows this page.</summary>
        [DataMember] public string Orientation { get; set; }
        [DataMember] public List<WidgetConfig> Widgets { get; set; }

        public DashboardPage() { Name = "Page"; Rows = 3; Columns = 4; Orientation = "any"; Widgets = new List<WidgetConfig>(); }
    }

    [DataContract]
    public sealed class DashcamSettings
    {
        [DataMember] public bool UseFrontCamera { get; set; }
        /// <summary>"auto" = keeps focusing, "far" = fixed on the distance.</summary>
        [DataMember] public string Focus { get; set; }
        /// <summary>Lets the camera slow down for a brighter picture in the dark.</summary>
        [DataMember] public bool SlowInDark { get; set; }
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public bool AutoStart { get; set; }
        [DataMember] public string Resolution { get; set; }       // see VideoModes.Resolutions
        [DataMember] public int FrameRate { get; set; }           // see VideoModes.FrameRates
        [DataMember] public int BitrateKbps { get; set; }
        [DataMember] public bool RecordAudio { get; set; }
        [DataMember] public int SegmentSeconds { get; set; }
        [DataMember] public bool UnlimitedStorage { get; set; }
        [DataMember] public double StorageCapGb { get; set; }
        [DataMember] public double ReserveFreeGb { get; set; }
        [DataMember] public bool GpsEnabled { get; set; }
        [DataMember] public bool BurnInStamp { get; set; }
        [DataMember] public bool StampDateTime { get; set; }
        [DataMember] public bool StampSpeed { get; set; }
        [DataMember] public bool StampCoordinates { get; set; }
        [DataMember] public string StampLabel { get; set; }
        [DataMember] public bool SubtitleTrack { get; set; }
        [DataMember] public bool GpxTrack { get; set; }
        [DataMember] public bool ImpactDetection { get; set; }
        [DataMember] public string ImpactSensitivity { get; set; } // high | medium | low | custom
        [DataMember] public double ImpactThresholdG { get; set; }
        [DataMember] public int PreEventSeconds { get; set; }
        [DataMember] public int PostEventSeconds { get; set; }
        [DataMember] public bool OfferEmergencyCall { get; set; }
        [DataMember] public string EmergencyNumber { get; set; }

        public static DashcamSettings Defaults()
        {
            return new DashcamSettings
            {
                Focus = "auto", Enabled = true, AutoStart = true, Resolution = "1080p", FrameRate = 30, BitrateKbps = 20000, RecordAudio = false,
                SegmentSeconds = 60, UnlimitedStorage = false, StorageCapGb = 8, ReserveFreeGb = 1.5,
                GpsEnabled = true, BurnInStamp = true, StampDateTime = true, StampSpeed = true, StampCoordinates = false, StampLabel = "",
                SubtitleTrack = true, GpxTrack = true,
                ImpactDetection = true, ImpactSensitivity = "medium", ImpactThresholdG = 2.0, PreEventSeconds = 60, PostEventSeconds = 30,
                OfferEmergencyCall = true, EmergencyNumber = "112"
            };
        }
    }

    [DataContract]
    public sealed class ObdSettings
    {
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public string DeviceId { get; set; }
        [DataMember] public string DeviceName { get; set; }
        [DataMember] public string Protocol { get; set; }
        [DataMember] public string FuelType { get; set; }
        [DataMember] public int DtcIntervalSeconds { get; set; }
        [DataMember] public bool ReadPermanentDtcs { get; set; }
        [DataMember] public List<ValueDefinition> CustomValues { get; set; }

        public static ObdSettings Defaults()
        {
            return new ObdSettings { Enabled = true, DeviceId = "", DeviceName = "", Protocol = "6", FuelType = "petrol",
                                     DtcIntervalSeconds = 30, ReadPermanentDtcs = false, CustomValues = new List<ValueDefinition>() };
        }
    }

    [DataContract]
    public sealed class UiSettings
    {
        [DataMember] public string NotificationPosition { get; set; } // top | bottom
        [DataMember] public int NotificationCollapseSeconds { get; set; }
        [DataMember] public bool KeepScreenOn { get; set; }
        [DataMember] public bool NightMode { get; set; }
        [DataMember] public string Accent { get; set; }

        public static UiSettings Defaults()
        {
            return new UiSettings { NotificationPosition = "top", NotificationCollapseSeconds = 8, KeepScreenOn = true, NightMode = true, Accent = "#FF3FA9F5" };
        }
    }

    [DataContract]
    public sealed class AppSettings
    {
        public const int CurrentVersion = 2;
        public const int MaxPages = 8;
        public const int MaxWidgetsPerPage = 24;
        public const int MaxCustomValues = 40;

        [DataMember] public int Version { get; set; }
        [DataMember] public DashcamSettings Dashcam { get; set; }
        [DataMember] public ObdSettings Obd { get; set; }
        [DataMember] public UiSettings Ui { get; set; }
        [DataMember] public List<DashboardPage> Pages { get; set; }

        public static AppSettings Defaults()
        {
            var s = new AppSettings { Version = CurrentVersion, Dashcam = DashcamSettings.Defaults(), Obd = ObdSettings.Defaults(), Ui = UiSettings.Defaults() };
            s.Pages = DefaultPages();
            return s;
        }

        public static List<DashboardPage> DefaultPages()
        {
            var main = new DashboardPage { Name = "Drive", Rows = 3, Columns = 4, Orientation = "landscape" };
            main.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Camera, Row = 0, Column = 0, RowSpan = 2, ColumnSpan = 2 });
            main.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Gauge, ValueKey = "obd:0C", Row = 0, Column = 2, RowSpan = 2, ColumnSpan = 1, WarnAbove = 5500, AlarmAbove = 6300 });
            main.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Number, ValueKey = PidCatalog.GpsSpeedKey, Row = 0, Column = 3, RowSpan = 2 });
            main.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Number, ValueKey = "obd:05", Row = 2, Column = 0, WarnAbove = 105, AlarmAbove = 115 });
            main.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Number, ValueKey = PidCatalog.BoostKey, Row = 2, Column = 1 });
            main.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Number, ValueKey = PidCatalog.AdapterVoltageKey, Row = 2, Column = 2, WarnBelow = 12.0 });
            main.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Status, Row = 2, Column = 3 });

            var engine = new DashboardPage { Name = "Engine", Rows = 3, Columns = 3, Orientation = "any" };
            engine.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Graph, ValueKey = PidCatalog.BoostKey, Row = 0, Column = 0, ColumnSpan = 2 });
            engine.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Bar, ValueKey = "obd:04", Row = 0, Column = 2 });
            engine.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Number, ValueKey = "obd:0F", Row = 1, Column = 0 });
            engine.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Number, ValueKey = "obd:0E", Row = 1, Column = 1 });
            engine.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Number, ValueKey = "obd:06", Row = 1, Column = 2 });
            engine.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Number, ValueKey = PidCatalog.ConsumptionKey, Row = 2, Column = 0 });
            engine.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Bar, ValueKey = "obd:2F", Row = 2, Column = 1, WarnBelow = 12 });
            engine.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Number, ValueKey = "obd:46", Row = 2, Column = 2 });

            var portrait = new DashboardPage { Name = "Portrait", Rows = 5, Columns = 2, Orientation = "portrait" };
            portrait.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Camera, Row = 0, Column = 0, RowSpan = 2, ColumnSpan = 2 });
            portrait.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Number, ValueKey = PidCatalog.GpsSpeedKey, Row = 2, Column = 0 });
            portrait.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Gauge, ValueKey = "obd:0C", Row = 2, Column = 1 });
            portrait.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Number, ValueKey = "obd:05", Row = 3, Column = 0 });
            portrait.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Number, ValueKey = PidCatalog.AdapterVoltageKey, Row = 3, Column = 1 });
            portrait.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Clock, Row = 4, Column = 0 });
            portrait.Widgets.Add(new WidgetConfig { Kind = WidgetKind.Status, Row = 4, Column = 1 });

            return new List<DashboardPage> { main, engine, portrait };
        }

        // ---------------- Validation ----------------

        private static double Clamp(double v, double min, double max, double fallback)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return fallback;
            return Math.Max(min, Math.Min(max, v));
        }
        private static int Clamp(int v, int min, int max) { return Math.Max(min, Math.Min(max, v)); }
        private static string OneOf(string v, string fallback, params string[] allowed) { return allowed.Contains(v) ? v : fallback; }
        private static double OptionalFinite(double v) { return double.IsInfinity(v) ? double.NaN : v; }

        /// <summary>
        /// Repairs any settings object (including one imported from a file) so every value is within safe bounds.
        /// Unknown or hostile content is replaced by defaults; nothing from settings is ever executed.
        /// Returns the list of corrections made.
        /// </summary>
        public List<string> Normalize()
        {
            var notes = new List<string>();
            var d = DashcamSettings.Defaults();
            if (Dashcam == null) { Dashcam = d; notes.Add("Dashcam settings reset"); }
            var c = Dashcam;
            c.Focus = OneOf(c.Focus, "auto", "auto", "far");
            c.Resolution = OneOf(c.Resolution, "1080p", VideoModes.Resolutions);
            c.FrameRate = VideoModes.FrameRates.Contains(c.FrameRate) ? c.FrameRate : 30;
            c.BitrateKbps = Clamp(c.BitrateKbps, 2000, VideoModes.MaxMbit * 1000);
            if (Version < 2) // earlier versions recommended too little
            {
                if (c.Resolution == "720p" && c.BitrateKbps < 11000) c.BitrateKbps = 11000;
                if (c.Resolution == "1080p" && c.BitrateKbps < 15000) c.BitrateKbps = 20000;
            }
            c.SegmentSeconds = Clamp(c.SegmentSeconds, 30, 300);
            c.StorageCapGb = Clamp(c.StorageCapGb, 1, 256, d.StorageCapGb);
            c.ReserveFreeGb = Clamp(c.ReserveFreeGb, 0.5, 8, d.ReserveFreeGb);
            c.StampLabel = StampFormatter.SanitizeLabel(c.StampLabel);
            c.ImpactSensitivity = OneOf(c.ImpactSensitivity, "medium", "high", "medium", "low", "custom");
            c.ImpactThresholdG = Clamp(c.ImpactThresholdG, 0.8, 8, 2.0);
            c.PreEventSeconds = Clamp(c.PreEventSeconds, 10, 180);
            c.PostEventSeconds = Clamp(c.PostEventSeconds, 10, 120);
            c.EmergencyNumber = OneOf(c.EmergencyNumber, "112", "112", "150", "155", "158", "911", "999");

            if (Obd == null) { Obd = ObdSettings.Defaults(); notes.Add("OBD settings reset"); }
            var o = Obd;
            o.DeviceId = o.DeviceId ?? ""; o.DeviceName = o.DeviceName ?? "";
            if (o.DeviceId.Length > 512) { o.DeviceId = ""; notes.Add("Invalid adapter id removed"); }
            o.Protocol = OneOf(o.Protocol, "6", "0", "6", "7", "8", "9");
            o.FuelType = OneOf(o.FuelType, "petrol", "petrol", "diesel");
            o.DtcIntervalSeconds = Clamp(o.DtcIntervalSeconds, 10, 600);
            var customs = new List<ValueDefinition>();
            foreach (var v in (o.CustomValues ?? new List<ValueDefinition>()).Take(MaxCustomValues))
            {
                string err;
                if (ValidateCustom(v, out err)) customs.Add(v);
                else notes.Add("Custom value '" + (v == null ? "?" : v.Name) + "' removed: " + err);
            }
            o.CustomValues = customs;

            if (Ui == null) { Ui = UiSettings.Defaults(); }
            Ui.NotificationPosition = OneOf(Ui.NotificationPosition, "top", "top", "bottom");
            Ui.NotificationCollapseSeconds = Clamp(Ui.NotificationCollapseSeconds, 0, 120);
            if (!IsColor(Ui.Accent)) Ui.Accent = "#FF3FA9F5";

            if (Pages == null || Pages.Count == 0) { Pages = DefaultPages(); notes.Add("Dashboard layout reset"); }
            Pages = Pages.Where(p => p != null).Take(MaxPages).ToList();
            var knownKeys = new HashSet<string>(PidCatalog.All(o.CustomValues).Select(v => v.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var p in Pages)
            {
                p.Name = StampFormatter.SanitizeLabel(p.Name);
                if (p.Name.Length == 0) p.Name = "Page";
                p.Rows = Clamp(p.Rows, 1, 6);
                p.Columns = Clamp(p.Columns, 1, 6);
                p.Orientation = OneOf(p.Orientation, "any", "any", "landscape", "portrait");
                p.Widgets = (p.Widgets ?? new List<WidgetConfig>()).Where(w => w != null).Take(MaxWidgetsPerPage).ToList();
                foreach (var w in p.Widgets)
                {
                    if (!Enum.IsDefined(typeof(WidgetKind), w.Kind)) w.Kind = WidgetKind.Number;
                    w.Row = Clamp(w.Row, 0, p.Rows - 1);
                    w.Column = Clamp(w.Column, 0, p.Columns - 1);
                    w.RowSpan = Clamp(w.RowSpan, 1, p.Rows - w.Row);
                    w.ColumnSpan = Clamp(w.ColumnSpan, 1, p.Columns - w.Column);
                    w.Label = StampFormatter.SanitizeLabel(w.Label);
                    if (!string.IsNullOrEmpty(w.Color) && !IsColor(w.Color)) w.Color = "";
                    w.Color = w.Color ?? "";
                    w.WarnAbove = OptionalFinite(w.WarnAbove); w.AlarmAbove = OptionalFinite(w.AlarmAbove);
                    w.WarnBelow = OptionalFinite(w.WarnBelow); w.Min = OptionalFinite(w.Min); w.Max = OptionalFinite(w.Max);
                    bool needsValue = w.Kind == WidgetKind.Number || w.Kind == WidgetKind.Gauge || w.Kind == WidgetKind.Bar || w.Kind == WidgetKind.Graph;
                    if (needsValue && (w.ValueKey == null || !knownKeys.Contains(w.ValueKey)))
                    {
                        notes.Add("Widget on page '" + p.Name + "' had an unknown value and was set to speed");
                        w.ValueKey = PidCatalog.GpsSpeedKey;
                    }
                    if (string.IsNullOrEmpty(w.Id) || w.Id.Length > 16) w.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
                }
            }
            Version = CurrentVersion;
            return notes;
        }

        public static bool IsColor(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '#' || (s.Length != 7 && s.Length != 9)) return false;
            for (int i = 1; i < s.Length; i++) if (!Hex.IsHexChar(s[i])) return false;
            return true;
        }

        public static bool ValidateCustom(ValueDefinition v, out string error)
        {
            error = null;
            if (v == null) { error = "empty"; return false; }
            v.Name = StampFormatter.SanitizeLabel(v.Name);
            v.ShortName = StampFormatter.SanitizeLabel(string.IsNullOrEmpty(v.ShortName) ? v.Name : v.ShortName);
            v.Unit = StampFormatter.SanitizeLabel(v.Unit ?? "");
            if (v.Unit.Length > 8) v.Unit = v.Unit.Substring(0, 8);
            if (v.Name.Length == 0) { error = "name is required"; return false; }
            if (string.IsNullOrEmpty(v.Key) || !v.Key.StartsWith("custom:") || v.Key.Length > 40) v.Key = "custom:" + Guid.NewGuid().ToString("N").Substring(0, 8);
            byte[] req;
            if (!CommandGuard.IsAllowedRequest(v.Request, out req, out error)) return false;
            v.Request = Hex.ToHex(req);
            v.Header = (v.Header ?? "").Trim().ToUpperInvariant();
            if (!CommandGuard.IsAllowedHeader(v.Header, out error)) return false;
            Formula f;
            if (!Formula.TryParse(v.Formula, out f, out error)) return false;
            if (double.IsNaN(v.Min) || double.IsInfinity(v.Min)) v.Min = 0;
            if (double.IsNaN(v.Max) || double.IsInfinity(v.Max) || v.Max <= v.Min) v.Max = v.Min + 100;
            v.Decimals = Clamp(v.Decimals, 0, 3);
            v.Priority = Clamp(v.Priority, 0, 2);
            return true;
        }

        // ---------------- Serialization ----------------
        public const int MaxSettingsBytes = 256 * 1024;

        public string ToJson()
        {
            var ser = new DataContractJsonSerializer(typeof(AppSettings), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, this);
                var bytes = ms.ToArray();
                return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
            }
        }

        /// <summary>Parses and normalizes settings JSON. Oversized or malformed input is rejected with an error instead of throwing.</summary>
        public static AppSettings FromJson(string json, out List<string> notes, out string error)
        {
            notes = new List<string>();
            error = null;
            if (string.IsNullOrEmpty(json)) { error = "File is empty."; return null; }
            if (json.Length > MaxSettingsBytes) { error = "File is too large to be a DriveDeck settings file."; return null; }
            try
            {
                var ser = new DataContractJsonSerializer(typeof(AppSettings), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var s = ser.ReadObject(ms) as AppSettings;
                    if (s == null) { error = "Not a DriveDeck settings file."; return null; }
                    notes = s.Normalize();
                    return s;
                }
            }
            catch (Exception ex)
            {
                error = "Could not read settings: " + ex.GetType().Name;
                return null;
            }
        }
    }
}
