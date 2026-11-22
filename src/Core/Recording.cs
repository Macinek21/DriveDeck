using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DriveDeck.Core
{
    /// <summary>A finished or in-progress loop segment. Sidecar files (.srt, .gpx) share the base name.</summary>
    public sealed class SegmentInfo
    {
        public string BaseName { get; set; }        // e.g. "20261122_140312"
        public DateTime Start { get; set; }          // local time
        public DateTime? End { get; set; }           // null while recording
        public ulong SizeBytes { get; set; }         // video + sidecars
        public bool InProgress { get; set; }
    }

    public static class SegmentNaming
    {
        public const string Format = "yyyyMMdd_HHmmss";

        public static string NameFor(DateTime start) { return start.ToString(Format, CultureInfo.InvariantCulture); }

        /// <summary>
        /// Accepts exactly "20261122_140312" or "20261122_140312 (2)" (unique-name collision), optionally followed by
        /// "_impact"/"_manual" for incident folders. Anything else is not ours and is never touched by loop cleanup.
        /// </summary>
        public static bool TryParse(string baseName, out DateTime start)
        {
            start = DateTime.MinValue;
            if (baseName == null || baseName.Length < Format.Length) return false;
            string rest = baseName.Substring(Format.Length);
            if (rest.StartsWith("_impact")) rest = rest.Substring(7);
            else if (rest.StartsWith("_manual")) rest = rest.Substring(7);
            if (rest.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(rest, @"^ \(\d{1,3}\)$")) return false;
            return DateTime.TryParseExact(baseName.Substring(0, Format.Length), Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out start);
        }
    }

    /// <summary>Pure loop-recording policy: which old segments to delete so the loop fits the cap and the phone keeps free space.</summary>
    public static class LoopStoragePolicy
    {
        /// <param name="capBytes">Maximum size of the loop folder; null = unlimited (only the free-space reserve applies).</param>
        /// <param name="freeBytes">Free space on the phone right now.</param>
        /// <param name="reserveBytes">Free space that must always remain for the phone itself.</param>
        /// <param name="incomingBytes">Expected size of the next segment, so we make room before it is written.</param>
        public static List<SegmentInfo> SelectForDeletion(IEnumerable<SegmentInfo> segments, ulong? capBytes,
                                                         ulong freeBytes, ulong reserveBytes, ulong incomingBytes)
        {
            var deletable = segments.Where(s => !s.InProgress).OrderBy(s => s.Start).ToList();
            ulong total = 0;
            foreach (var s in segments) total += s.SizeBytes;
            var result = new List<SegmentInfo>();
            ulong free = freeBytes;
            foreach (var s in deletable)
            {
                bool overCap = capBytes.HasValue && total + incomingBytes > capBytes.Value;
                bool lowSpace = free < reserveBytes + incomingBytes;
                if (!overCap && !lowSpace) break;
                result.Add(s);
                total -= Math.Min(total, s.SizeBytes);
                free += s.SizeBytes;
            }
            return result;
        }

        /// <summary>Estimated bytes for a segment, used to make room in advance.</summary>
        public static ulong EstimateSegmentBytes(int videoBitrate, bool audio, int seconds)
        {
            long bits = (long)videoBitrate + (audio ? 128000 : 0);
            return (ulong)(bits / 8 * seconds * 11 / 10); // +10 % container overhead
        }
    }

    /// <summary>Which segments cover an incident window [t - pre, t + post].</summary>
    public static class EventWindow
    {
        public static List<SegmentInfo> SegmentsFor(IEnumerable<SegmentInfo> segments, DateTime eventTime, TimeSpan pre, TimeSpan post)
        {
            DateTime from = eventTime - pre, to = eventTime + post;
            return segments.Where(s =>
            {
                DateTime end = s.End ?? DateTime.MaxValue;
                return s.Start <= to && end >= from;
            }).OrderBy(s => s.Start).ToList();
        }
    }

    /// <summary>
    /// Detects impacts from raw accelerometer samples (in g). Gravity is tracked with a slow low-pass filter and removed,
    /// so mounting angle does not matter. An impact is a short peak of dynamic acceleration above the threshold.
    /// Includes a warm-up and cool-down so that mounting/handling the phone or one long crash fires once.
    /// </summary>
    public sealed class ImpactDetector
    {
        private double _gx, _gy, _gz;
        private bool _init;
        private DateTime _startedAt;
        private DateTime _lastImpact = DateTime.MinValue;

        public double ThresholdG { get; set; }
        public TimeSpan Cooldown { get; set; }
        public TimeSpan WarmUp { get; set; }
        public double LastDynamicG { get; private set; }
        public double PeakDynamicG { get; private set; }

        public ImpactDetector(double thresholdG)
        {
            ThresholdG = thresholdG;
            Cooldown = TimeSpan.FromSeconds(10);
            WarmUp = TimeSpan.FromSeconds(3);
        }

        /// <summary>Feeds one sample; returns true exactly when a new impact is detected.</summary>
        public bool Feed(double x, double y, double z, DateTime timestamp)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z)) return false;
            if (!_init) { _gx = x; _gy = y; _gz = z; _init = true; _startedAt = timestamp; return false; }
            const double alpha = 0.02; // ~1 s time constant at 50 Hz
            _gx += alpha * (x - _gx); _gy += alpha * (y - _gy); _gz += alpha * (z - _gz);
            double dx = x - _gx, dy = y - _gy, dz = z - _gz;
            double dyn = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            LastDynamicG = dyn;
            if (dyn > PeakDynamicG) PeakDynamicG = dyn;
            if (timestamp - _startedAt < WarmUp) return false;
            if (dyn < ThresholdG) return false;
            if (timestamp - _lastImpact < Cooldown) return false;
            _lastImpact = timestamp;
            return true;
        }

        public void ResetPeak() { PeakDynamicG = 0; }

        /// <summary>Sensitivity presets in g of dynamic acceleration. A pothole is ~0.8-1.2 g; a minor collision ~2-4 g.</summary>
        public static double PresetThreshold(string preset)
        {
            switch (preset)
            {
                case "high": return 1.3;
                case "low": return 3.0;
                default: return 2.0; // medium
            }
        }
    }

    public sealed class GpsFix
    {
        public DateTime Time { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double? Altitude { get; set; }
        public double? SpeedKmh { get; set; }
        public double? Accuracy { get; set; }
        public double? Heading { get; set; }
    }

    public static class GeoMath
    {
        public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000;
            double dLat = (lat2 - lat1) * Math.PI / 180, dLon = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        /// <summary>Speed for a fix: the receiver's own Doppler speed when present, otherwise from the distance to the previous fix.</summary>
        public static double? EffectiveSpeedKmh(GpsFix prev, GpsFix cur)
        {
            if (cur == null) return null;
            if (cur.SpeedKmh.HasValue && !double.IsNaN(cur.SpeedKmh.Value) && cur.SpeedKmh.Value >= 0) return cur.SpeedKmh;
            if (prev == null) return null;
            double secs = (cur.Time - prev.Time).TotalSeconds;
            if (secs <= 0.2 || secs > 10) return null;
            double kmh = DistanceMeters(prev.Latitude, prev.Longitude, cur.Latitude, cur.Longitude) / secs * 3.6;
            if (cur.Accuracy.HasValue && cur.Accuracy.Value > 50) return null; // too inaccurate to differentiate
            return kmh > 400 ? (double?)null : kmh;
        }
    }

    public sealed class StampOptions
    {
        public bool DateTime = true;
        public bool Speed = true;
        public bool Coordinates = true;
        public string Label = "";
        public bool Metric = true;
    }

    public static class StampFormatter
    {
        public const int MaxLabelLength = 24;

        public static string Format(DateTime now, GpsFix fix, double? speedKmh, StampOptions o)
        {
            var parts = new List<string>();
            if (o.DateTime) parts.Add(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            if (o.Speed) parts.Add(speedKmh.HasValue ? Math.Round(speedKmh.Value).ToString(CultureInfo.InvariantCulture) + " km/h" : "-- km/h");
            if (o.Coordinates)
            {
                if (fix != null)
                    parts.Add((fix.Latitude >= 0 ? "N" : "S") + Math.Abs(fix.Latitude).ToString("0.00000", CultureInfo.InvariantCulture) + " " +
                              (fix.Longitude >= 0 ? "E" : "W") + Math.Abs(fix.Longitude).ToString("0.00000", CultureInfo.InvariantCulture));
                else parts.Add("no GPS");
            }
            string label = SanitizeLabel(o.Label);
            if (label.Length > 0) parts.Add(label);
            return string.Join("   ", parts);
        }

        /// <summary>Keeps printable characters only, capped in length.</summary>
        public static string SanitizeLabel(string label)
        {
            if (string.IsNullOrEmpty(label)) return "";
            var sb = new StringBuilder();
            foreach (char c in label)
            {
                if (char.IsControl(c)) continue;
                sb.Append(c);
                if (sb.Length >= MaxLabelLength) break;
            }
            return sb.ToString().Trim();
        }
    }

    /// <summary>Builds an .srt subtitle track (one cue per second) so any player can show speed/position under the video.</summary>
    public sealed class SrtBuilder
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private int _index;
        public DateTime SegmentStart { get; private set; }

        public SrtBuilder(DateTime segmentStart) { SegmentStart = segmentStart; }

        public string AddCue(DateTime from, DateTime to, string text)
        {
            if (to <= from) return string.Empty;
            TimeSpan a = from - SegmentStart, b = to - SegmentStart;
            if (a < TimeSpan.Zero) a = TimeSpan.Zero;
            _index++;
            string cue = _index.ToString(CultureInfo.InvariantCulture) + "\r\n" + Ts(a) + " --> " + Ts(b) + "\r\n" +
                         (text ?? "").Replace("\r", " ").Replace("\n", " ") + "\r\n\r\n";
            _sb.Append(cue);
            return cue;
        }

        private static string Ts(TimeSpan t)
        {
            return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") + "," + t.Milliseconds.ToString("000");
        }

        public override string ToString() { return _sb.ToString(); }
    }

    /// <summary>GPX 1.1 track for a segment (opens in Mapy.com, Google Earth, etc.).</summary>
    public static class GpxBuilder
    {
        public static string XmlEscape(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '&': sb.Append("&amp;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: if (!char.IsControl(c)) sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public static string Build(string name, IEnumerable<GpsFix> fixes)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n");
            sb.Append("<gpx version=\"1.1\" creator=\"DriveDeck\" xmlns=\"http://www.topografix.com/GPX/1/1\">\r\n<trk><name>");
            sb.Append(XmlEscape(name ?? ""));
            sb.Append("</name><trkseg>\r\n");
            foreach (var f in fixes)
            {
                sb.Append("<trkpt lat=\"").Append(f.Latitude.ToString("0.000000", CultureInfo.InvariantCulture))
                  .Append("\" lon=\"").Append(f.Longitude.ToString("0.000000", CultureInfo.InvariantCulture)).Append("\">");
                if (f.Altitude.HasValue) sb.Append("<ele>").Append(f.Altitude.Value.ToString("0.0", CultureInfo.InvariantCulture)).Append("</ele>");
                sb.Append("<time>").Append(f.Time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append("</time>");
                if (f.SpeedKmh.HasValue)
                    sb.Append("<extensions><speed>").Append((f.SpeedKmh.Value / 3.6).ToString("0.00", CultureInfo.InvariantCulture)).Append("</speed></extensions>");
                sb.Append("</trkpt>\r\n");
            }
            sb.Append("</trkseg></trk>\r\n</gpx>\r\n");
            return sb.ToString();
        }
    }
}
