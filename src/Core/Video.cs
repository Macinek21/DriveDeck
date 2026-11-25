using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DriveDeck.Core
{
    /// <summary>One recording mode the camera reports.</summary>
    public sealed class VideoMode
    {
        public string Resolution { get; set; }   // "720p", "1080p", "1440p", "2160p"
        public int Width { get; set; }
        public int Height { get; set; }
        public int Fps { get; set; }
        public const int Stream = 0, Profile = 1, ViaPreview = 2, ViaPhoto = 3, Forced = 4, Negotiated = 5;
        /// <summary>How the camera must be opened for this mode: normally, with a video profile, with another photo source, by asking for an unlisted format, or by leaving the choice to the recorder.</summary>
        public int Source { get; set; }
        public bool Special { get { return Source != Stream; } }

        public string Id { get { return Resolution + "@" + Fps.ToString(CultureInfo.InvariantCulture); } }
        /// <summary>More than Full HD 30: needs checking, because older phones may list more than they can keep up with.</summary>
        public bool Demanding { get { return Height > 1080 || Fps > 30; } }
        public override string ToString() { return Id + (Special ? "/" + Source : ""); }
    }

    public static class VideoModes
    {
        public const int MaxMbit = 60;
        public static readonly string[] Resolutions = { "480p", "720p", "1080p", "1440p", "2160p" };
        public static readonly int[] FrameRates = { 15, 20, 24, 25, 30, 48, 50, 60, 90, 120 };

        /// <summary>The common name of a 16:9 recording size; null for sizes a dashcam has no use for.</summary>
        public static string NameOf(int width, int height)
        {
            if (width == 1280 && height == 720) return "720p";
            if (width == 1920 && (height == 1080 || height == 1088)) return "1080p";
            if (width == 2560 && height == 1440) return "1440p";
            if (width == 3840 && height == 2160) return "2160p";
            if ((width == 848 || width == 854 || width == 864) && height == 480) return "480p";
            return null;
        }

        public static string Label(VideoMode m)
        {
            string n = m.Resolution == "2160p" ? "4K" : m.Resolution == "1440p" ? "1440p (2K)" : m.Resolution;
            return n + "  " + m.Width + "×" + m.Height;
        }

        /// <summary>29.97 counts as 30; rates nobody would choose (7.5, 14.3…) are left out.</summary>
        public static int RoundFps(double fps)
        {
            foreach (int f in FrameRates) if (Math.Abs(fps - f) <= 0.1) return f;
            return 0;
        }

        /// <summary>Adds a reported format; the first way of reaching it wins.</summary>
        public static void Add(List<VideoMode> list, int width, int height, double fps, int source)
        {
            string name = NameOf(width, height);
            int f = RoundFps(fps);
            if (name == null || f == 0) return;
            if (list.Any(m => m.Resolution == name && m.Fps == f)) return;
            list.Add(new VideoMode { Resolution = name, Width = width, Height = height == 1088 ? 1080 : height, Fps = f, Source = source });
        }

        public static List<VideoMode> Sorted(IEnumerable<VideoMode> l)
        {
            return l.OrderBy(m => m.Height).ThenBy(m => m.Fps).ToList();
        }

        /// <summary>The wanted mode, or the closest one: same resolution first, then the nearest smaller one.</summary>
        public static VideoMode Pick(IEnumerable<VideoMode> modes, string resolution, int fps)
        {
            var list = modes.ToList();
            if (list.Count == 0) return null;
            int want = Array.IndexOf(Resolutions, resolution);
            var best = list.Select(m => Array.IndexOf(Resolutions, m.Resolution)).Distinct().OrderBy(i => i <= want ? want - i : 100 + i).First();
            var same = list.Where(m => Array.IndexOf(Resolutions, m.Resolution) == best).ToList();
            return same.FirstOrDefault(m => m.Fps == fps)
                   ?? same.Where(m => m.Fps <= fps && !m.Special).OrderByDescending(m => m.Fps).FirstOrDefault()
                   ?? same.OrderBy(m => Math.Abs(m.Fps - fps)).First();
        }

        /// <summary>
        /// Sensible H.264 bitrate in Mbit/s for a phone's hardware encoder: 20 for Full HD 30 (what the Lumia's own camera app uses),
        /// smaller pictures get more per pixel because they are the first to look blurry.
        /// </summary>
        public static int RecommendedMbit(int width, int height, int fps)
        {
            double mbit = 20 * Math.Pow(width * (double)height / (1920 * 1080), 0.75) * (Math.Min(fps, 30) / 30.0 + Math.Max(0, fps - 30) / 60.0);
            return (int)Math.Max(4, Math.Min(MaxMbit, Math.Round(mbit)));
        }

        // The last list the camera reported is stored, so that Settings can show it without opening the camera.
        public static string Serialize(IEnumerable<VideoMode> l)
        {
            return string.Join(";", l.Select(m => string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", m.Width, m.Height, m.Fps, m.Source)));
        }

        public static List<VideoMode> Parse(string s)
        {
            var list = new List<VideoMode>();
            foreach (var part in (s ?? "").Split(';').Take(200))
            {
                var p = part.Split(',');
                int w, h, f, src;
                if (p.Length == 4 && int.TryParse(p[0], out w) && int.TryParse(p[1], out h) && int.TryParse(p[2], out f) && int.TryParse(p[3], out src) && src >= 0 && src <= 5) Add(list, w, h, f, src);
            }
            return Sorted(list);
        }
    }

    /// <summary>What a recorded MP4 really contains, read from its index only (the video data is skipped).</summary>
    public sealed class Mp4Info
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Frames { get; private set; }
        public double Seconds { get; private set; }
        public double Fps { get { return Seconds > 0 ? Frames / Seconds : 0; } }

        /// <summary>Share of frames that carry almost no data: copies of the frame before, used to pad out a frame rate.</summary>
        public double TinyShare { get; private set; }

        private const int MaxIndexBytes = 32 * 1024 * 1024;

        /// <summary>Returns null for anything that is not a complete MP4 with a video track.</summary>
        public static Mp4Info Read(Stream s)
        {
            try
            {
                long pos = 0, length = s.Length;
                var head = new byte[16];
                while (pos + 8 <= length)
                {
                    s.Position = pos;
                    if (!Fill(s, head, 8)) return null;
                    long size = U32(head, 0);
                    int header = 8;
                    if (size == 1) { var big = new byte[8]; if (!Fill(s, big, 8)) return null; size = (long)(((ulong)U32(big, 0) << 32) | (ulong)U32(big, 4)); header = 16; }
                    if (size == 0) size = length - pos;
                    if (size < header || pos + size > length) return null;
                    if (Type(head, 4) == "moov")
                    {
                        if (size > MaxIndexBytes) return null;
                        var moov = new byte[size - header];
                        return Fill(s, moov, moov.Length) ? FromMoov(moov) : null;
                    }
                    pos += size;
                }
            }
            catch (IOException) { }
            return null;
        }

        private static Mp4Info FromMoov(byte[] b)
        {
            foreach (var trak in Boxes(b, 0, b.Length, "trak"))
            {
                var mdia = Boxes(b, trak.Item1, trak.Item2, "mdia").FirstOrDefault();
                if (mdia == null) continue;
                var hdlr = Boxes(b, mdia.Item1, mdia.Item2, "hdlr").FirstOrDefault();
                if (hdlr == null || hdlr.Item2 - hdlr.Item1 < 12 || Type(b, hdlr.Item1 + 8) != "vide") continue;
                var mdhd = Boxes(b, mdia.Item1, mdia.Item2, "mdhd").FirstOrDefault();
                var minf = Boxes(b, mdia.Item1, mdia.Item2, "minf").FirstOrDefault();
                if (mdhd == null || minf == null) return null;
                bool v1 = b[mdhd.Item1] == 1;
                if (mdhd.Item2 - mdhd.Item1 < (v1 ? 32 : 20)) return null;
                double scale = U32(b, mdhd.Item1 + (v1 ? 20 : 12));
                double dur = v1 ? U32(b, mdhd.Item1 + 24) * 4294967296.0 + U32(b, mdhd.Item1 + 28) : U32(b, mdhd.Item1 + 16);
                var stbl = Boxes(b, minf.Item1, minf.Item2, "stbl").FirstOrDefault();
                if (stbl == null || scale <= 0) return null;
                var stsz = Boxes(b, stbl.Item1, stbl.Item2, "stsz").FirstOrDefault();
                if (stsz == null || stsz.Item2 - stsz.Item1 < 12) return null;
                var info = new Mp4Info { Frames = (int)U32(b, stsz.Item1 + 8), Seconds = dur / scale };
                if (U32(b, stsz.Item1 + 4) == 0 && info.Frames > 0 && info.Frames <= (stsz.Item2 - stsz.Item1 - 12) / 4)
                {
                    var sizes = new uint[info.Frames];
                    for (int i = 0; i < sizes.Length; i++) sizes[i] = U32(b, stsz.Item1 + 12 + i * 4);
                    double average = sizes.Average(x => (double)x);
                    info.TinyShare = sizes.Count(x => x < average * 0.1) / (double)sizes.Length;
                }
                var stsd = Boxes(b, stbl.Item1, stbl.Item2, "stsd").FirstOrDefault();
                if (stsd != null && stsd.Item2 - stsd.Item1 >= 8 + 36)
                {
                    int e = stsd.Item1 + 8; // first sample entry
                    info.Width = b[e + 32] << 8 | b[e + 33];
                    info.Height = b[e + 34] << 8 | b[e + 35];
                }
                return info;
            }
            return null;
        }

        /// <summary>(content start, content end) of every child box of the given type.</summary>
        private static IEnumerable<Tuple<int, int>> Boxes(byte[] b, int from, int to, string type)
        {
            int p = from;
            while (p + 8 <= to)
            {
                long size = U32(b, p);
                if (size == 0) size = to - p;
                if (size < 8 || p + size > to) yield break;
                if (Type(b, p + 4) == type) yield return Tuple.Create(p + 8, (int)(p + size));
                p += (int)size;
            }
        }

        private static bool Fill(Stream s, byte[] buf, int count)
        {
            int n = 0;
            while (n < count) { int r = s.Read(buf, n, count - n); if (r <= 0) return false; n += r; }
            return true;
        }

        private static uint U32(byte[] b, int i) { return (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]); }
        private static string Type(byte[] b, int i) { return new string(new[] { (char)b[i], (char)b[i + 1], (char)b[i + 2], (char)b[i + 3] }); }
    }
}
