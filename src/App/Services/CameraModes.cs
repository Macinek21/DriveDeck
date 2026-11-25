using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using DriveDeck.Core;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace DriveDeck.App.Services
{
    public enum StampState { Untested, Off, Keep }

    /// <summary>
    /// The video modes of one camera, from what the camera itself reports (stream formats, video profiles, and the formats it adds
    /// when opened in another way), and what recording has shown about them.
    /// </summary>
    public sealed class CameraModes
    {
        private static readonly CameraModes Back = new CameraModes("back"), Front = new CameraModes("front");
        public static CameraModes For(bool front) { return front ? Front : Back; }

        private readonly string _name;
        private CameraModes(string name) { _name = name; }

        private ApplicationDataContainer Store
        {
            get
            {
                var c = ApplicationData.Current.LocalSettings.CreateContainer("camera-" + _name, ApplicationDataCreateDisposition.Always);
                // A new app version or a Windows update may handle modes better, so everything learned is forgotten then.
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                string key = v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision + "|" + Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamilyVersion;
                if (c.Values["for"] as string != key) { c.Values.Clear(); c.Values["for"] = key; }
                return c;
            }
        }

        /// <summary>Everything the camera reported; empty before it was opened for the first time.</summary>
        public List<VideoMode> Reported() { return VideoModes.Parse(Store.Values["modes"] as string); }

        public List<VideoMode> Usable()
        {
            var no = Refused();
            return Reported().Where(m => !no.Contains(m.Id)).ToList();
        }

        public HashSet<string> Refused()
        {
            return new HashSet<string>(((Store.Values["refused"] as string) ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public void Refuse(VideoMode m)
        {
            var r = Refused();
            r.Add(m.Id);
            Store.Values["refused"] = string.Join(";", r);
        }

        public void ClearRefused() { Store.Values.Remove("refused"); }

        /// <summary>Frame rate measured in a recorded clip, 0 = not recorded yet.</summary>
        public double RealFps(VideoMode m) { var o = Store.Values["fps " + m.Id]; return o is double ? (double)o : 0; }
        public void SaveRealFps(VideoMode m, double fps) { Store.Values["fps " + m.Id] = fps; }

        public StampState Stamp(VideoMode m) { var o = Store.Values["stamp " + m.Id]; return o is int ? (StampState)(int)o : StampState.Untested; }
        public void SetStamp(VideoMode m, StampState s) { Store.Values["stamp " + m.Id] = (int)s; }

        public static IReadOnlyList<MediaCaptureVideoProfile> Profiles(string cameraId)
        {
            try { if (MediaCapture.IsVideoProfileSupported(cameraId)) return MediaCapture.FindAllVideoProfiles(cameraId); }
            catch (Exception ex) { AppLog.Error("Video profiles", ex); }
            return new List<MediaCaptureVideoProfile>();
        }

        public static IEnumerable<VideoEncodingProperties> Formats(MediaCapture mc, MediaStreamType stream = MediaStreamType.VideoRecord)
        {
            return mc.VideoDeviceController.GetAvailableMediaStreamProperties(stream).OfType<VideoEncodingProperties>();
        }

        private static double Fps(VideoEncodingProperties p) { return p.FrameRate.Denominator == 0 ? 0 : (double)p.FrameRate.Numerator / p.FrameRate.Denominator; }

        public static bool Matches(VideoEncodingProperties p, VideoMode m)
        {
            return VideoModes.NameOf((int)p.Width, (int)p.Height) == m.Resolution && VideoModes.RoundFps(Fps(p)) == m.Fps;
        }

        public static bool Matches(MediaCaptureVideoProfileMediaDescription d, VideoMode m)
        {
            return VideoModes.NameOf((int)d.Width, (int)d.Height) == m.Resolution && VideoModes.RoundFps(d.FrameRate) == m.Fps;
        }

        /// <summary>The ways a camera can be opened that may show further formats.</summary>
        public static MediaCaptureInitializationSettings Settings(string cameraId, bool audio, VideoMode mode, IReadOnlyList<MediaCaptureVideoProfile> profiles)
        {
            var init = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = cameraId,
                StreamingCaptureMode = audio ? StreamingCaptureMode.AudioAndVideo : StreamingCaptureMode.Video,
            };
            int source = mode == null ? VideoMode.Stream : mode.Source;
            if (source == VideoMode.ViaPreview) init.PhotoCaptureSource = PhotoCaptureSource.VideoPreview;
            if (source == VideoMode.ViaPhoto) init.PhotoCaptureSource = PhotoCaptureSource.Photo;
            if (source == VideoMode.Profile && profiles != null)
                foreach (var p in profiles)
                {
                    var rec = p.SupportedRecordMediaDescription.FirstOrDefault(d => Matches(d, mode));
                    if (rec == null) continue;
                    init.VideoProfile = p;
                    init.RecordMediaDescription = rec;
                    break;
                }
            return init;
        }

        /// <summary>
        /// Asks the camera once (per app version) for everything it offers, opened in each possible way, and writes the answers
        /// to the log. The camera must not be in use.
        /// </summary>
        public async Task LearnAsync(string cameraId)
        {
            if (Store.Values["learned"] is bool) return;
            var inv = CultureInfo.InvariantCulture;
            var list = new List<VideoMode>();
            foreach (int source in new[] { VideoMode.Stream, VideoMode.ViaPreview, VideoMode.ViaPhoto })
            {
                MediaCapture mc = null;
                try
                {
                    mc = new MediaCapture();
                    await mc.InitializeAsync(Settings(cameraId, false, new VideoMode { Source = source }, null));
                    var formats = Formats(mc).ToList();
                    foreach (var p in formats) VideoModes.Add(list, (int)p.Width, (int)p.Height, Fps(p), source);
                    AppLog.Info(_name + " camera, opened " + source + ": " + List(formats));
                    if (source != VideoMode.Stream) continue;
                    AppLog.Info(_name + " preview: " + List(Formats(mc, MediaStreamType.VideoPreview)));
                    AppLog.Info(_name + " photo: " + string.Join(", ", mc.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.Photo)
                        .Select(x => { var v = x as VideoEncodingProperties; var i = x as ImageEncodingProperties; return v != null ? v.Width + "x" + v.Height + " " + v.Subtype : i != null ? i.Width + "x" + i.Height + " " + i.Subtype : "?"; }).Distinct()));
                    AppLog.Info("Streams: " + mc.MediaCaptureSettings.VideoDeviceCharacteristic);
                    foreach (var fs in mc.FrameSources)
                        AppLog.Info(_name + " frame source " + fs.Value.Info.MediaStreamType + "/" + fs.Value.Info.SourceKind + ": " +
                            string.Join(", ", fs.Value.SupportedFormats.Where(f => f.VideoFormat != null)
                                .Select(f => f.VideoFormat.Width + "x" + f.VideoFormat.Height + "@" + (f.FrameRate.Denominator == 0 ? "?" : ((double)f.FrameRate.Numerator / f.FrameRate.Denominator).ToString("0.##", inv))).Distinct()));
                }
                catch (Exception ex) { AppLog.Info(_name + " camera, opened " + source + ": " + ex.Message); if (source == VideoMode.Stream) throw; }
                finally { if (mc != null) mc.Dispose(); }
            }
            try
            {
                AppLog.Info(_name + " video profiles supported: " + MediaCapture.IsVideoProfileSupported(cameraId));
                foreach (var pr in Profiles(cameraId))
                {
                    AppLog.Info("Profile " + pr.Id + ": " + string.Join(", ", pr.SupportedRecordMediaDescription.Select(d => d.Width + "x" + d.Height + "@" + d.FrameRate.ToString("0.##", inv)).Distinct()));
                    foreach (var d in pr.SupportedRecordMediaDescription) VideoModes.Add(list, (int)d.Width, (int)d.Height, d.FrameRate, VideoMode.Profile);
                }
            }
            catch (Exception ex) { AppLog.Error("Profiles", ex); }
            try
            {
                foreach (var g in await MediaFrameSourceGroup.FindAllAsync())
                    AppLog.Info("Source group " + g.DisplayName + ": " + string.Join(", ", g.SourceInfos.Select(i => i.MediaStreamType + "/" + i.SourceKind)));
            }
            catch (Exception ex) { AppLog.Error("Source groups", ex); }
            Store.Values["modes"] = VideoModes.Serialize(VideoModes.Sorted(list));
            Store.Values["learned"] = true;
            AppLog.Info(_name + " camera modes: " + string.Join(" ", VideoModes.Sorted(list)));
        }

        /// <summary>Modes the camera does not list but that were recorded and measured in the search.</summary>
        public void AddFound(IEnumerable<VideoMode> found)
        {
            var list = Reported();
            foreach (var m in found) VideoModes.Add(list, m.Width, m.Height, m.Fps, m.Source);
            Store.Values["modes"] = VideoModes.Serialize(VideoModes.Sorted(list));
            AppLog.Info(_name + " camera modes: " + string.Join(" ", VideoModes.Sorted(list)));
        }

        private static ApplicationDataContainer Common { get { return Back.Store; } }

        /// <summary>The class that draws the stamp; the search finds out which is faster on this phone.</summary>
        public static string StampEffect { get { return (Common.Values["stampEffect"] as string) ?? "OverlayVideoEffect"; } }

        public static void SetStampEffect(string name)
        {
            Common.Values["stampEffect"] = name;
            foreach (var c in new[] { Back, Front }) // earlier measurements were made with the other one
                foreach (var k in c.Store.Values.Keys.Where(x => x.StartsWith("stamp ")).ToList()) c.Store.Values.Remove(k);
        }

        /// <summary>Null until it has been tried.</summary>
        public static bool? Both { get { var o = Common.Values["both"]; return o is bool ? (bool?)o : null; } }
        public static void SetBoth(bool works) { Common.Values["both"] = works; }

        private static string List(IEnumerable<VideoEncodingProperties> l)
        {
            return string.Join(", ", l.Select(p => p.Width + "x" + p.Height + "@" + Fps(p).ToString("0.##", CultureInfo.InvariantCulture) + " " + p.Subtype).Distinct());
        }
    }
}
