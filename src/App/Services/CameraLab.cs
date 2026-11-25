using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DriveDeck.Core;
using Windows.Devices.Enumeration;
using Windows.Foundation.Collections;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace DriveDeck.App.Services
{
    /// <summary>
    /// A search the user starts by hand: asks the camera for modes it does not list, checks whether both cameras run at once and which
    /// way of drawing the stamp is fastest. Everything is measured in short test clips, which are deleted. Nothing is believed only
    /// because the phone accepted it: an enlarged 1080p picture or doubled frames do not count.
    /// </summary>
    public static class CameraLab
    {
        private static Action<string> _progress;
        private static readonly List<string> Summary = new List<string>();

        private static void Log(string s) { AppLog.Info("LAB " + s); }
        private static void Step(string s) { Log("--- " + s); var p = _progress; if (p != null) p(s + "…"); }
        private static string Err(Exception ex) { return "0x" + ex.HResult.ToString("X8") + " " + (ex.Message ?? "").Replace("\r\n", " ").Trim(); }

        private sealed class Target
        {
            public int W, H, Fps;
            public override string ToString() { return W + "x" + H + "@" + Fps; }
        }

        private static readonly Target[] Targets =
        {
            new Target { W = 3840, H = 2160, Fps = 30 }, new Target { W = 3840, H = 2160, Fps = 24 }, new Target { W = 2560, H = 1440, Fps = 30 },
            new Target { W = 1920, H = 1080, Fps = 60 }, new Target { W = 1920, H = 1080, Fps = 50 }, new Target { W = 1280, H = 720, Fps = 60 },
        };

        /// <summary>The dashcam must be shut down. Returns lines for the user.</summary>
        public static async Task<List<string>> RunAsync(Action<string> progress)
        {
            _progress = progress;
            Summary.Clear();
            var cams = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            var back = cams.FirstOrDefault(c => c.EnclosureLocation != null && c.EnclosureLocation.Panel == Panel.Back);
            var front = cams.FirstOrDefault(c => c.EnclosureLocation != null && c.EnclosureLocation.Panel == Panel.Front);
            if (back == null) { Summary.Add("No back camera found."); return Summary.ToList(); }
            await Guard("Formats", () => FormatsAsync(back.Id));
            var found = new List<VideoMode>();
            await Guard("Modes", () => ModesAsync(back.Id, found));
            if (found.Count > 0)
            {
                CameraModes.For(false).AddFound(found);
                Summary.Add("New modes: " + string.Join(", ", found.Select(m => m.Resolution + " " + m.Fps + " fps")));
            }
            else Summary.Add("No mode above 1080p 30 fps could be reached.");
            await Guard("Stamp", () => StampAsync(back.Id));
            if (front != null) await Guard("Both cameras", () => BothAsync(back.Id, front.Id));
            Step("Done");
            foreach (var s in Summary) Log("RESULT " + s);
            return Summary.ToList();
        }

        private static async Task Guard(string what, Func<Task> f)
        {
            try { await f(); } catch (Exception ex) { Log(what + " stopped: " + Err(ex)); }
        }

        /// <summary>A driver that never answers must not hang the search.</summary>
        private static async Task<T> Limit<T>(Task<T> t, int seconds)
        {
            if (await Task.WhenAny(t, Task.Delay(seconds * 1000)) != t) throw new TimeoutException("no answer in " + seconds + " s");
            return await t;
        }

        private static async Task Limit(Task t, int seconds)
        {
            if (await Task.WhenAny(t, Task.Delay(seconds * 1000)) != t) throw new TimeoutException("no answer in " + seconds + " s");
            await t;
        }

        private static async Task<MediaCapture> OpenAsync(string id, Action<MediaCaptureInitializationSettings> change = null)
        {
            var s = new MediaCaptureInitializationSettings { VideoDeviceId = id, StreamingCaptureMode = StreamingCaptureMode.Video };
            if (change != null) change(s);
            var mc = new MediaCapture();
            mc.Failed += (c, e) => Log("camera failed: 0x" + e.Code.ToString("X8") + " " + e.Message);
            try { await Limit(mc.InitializeAsync(s).AsTask(), 10); }
            catch { mc.Dispose(); throw; }
            return mc;
        }

        private static double Fps(VideoEncodingProperties p) { return p == null || p.FrameRate.Denominator == 0 ? 0 : (double)p.FrameRate.Numerator / p.FrameRate.Denominator; }
        private static string Text(VideoEncodingProperties p) { return p == null ? "nothing" : p.Width + "x" + p.Height + "@" + Fps(p).ToString("0.##", CultureInfo.InvariantCulture) + " " + p.Subtype; }

        private static VideoEncodingProperties Now(MediaCapture mc)
        {
            return mc.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoRecord) as VideoEncodingProperties;
        }

        private static bool Is(VideoEncodingProperties p, Target t) { return p != null && p.Width == t.W && p.Height == t.H && Math.Abs(Fps(p) - t.Fps) < 1; }

        // ---------------- 1. Does the list change with how the camera is opened or set up? ----------------

        private static async Task FormatsAsync(string id)
        {
            Step("Asking the camera in different ways");
            string usual = null;
            Action<string, MediaCapture> compare = (how, mc) =>
            {
                string l = string.Join(", ", CameraModes.Formats(mc).Select(Text).Distinct());
                if (usual == null) usual = l;
                else if (l == usual) Log(how + ": same formats");
                else Log(how + ": DIFFERENT: " + l);
            };
            var ways = new List<KeyValuePair<string, Action<MediaCaptureInitializationSettings>>>();
            Action<string, Action<MediaCaptureInitializationSettings>> way = (n, a) => ways.Add(new KeyValuePair<string, Action<MediaCaptureInitializationSettings>>(n, a));
            way("usual", s => { });
            way("audio+video", s => s.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo);
            way("communications", s => { s.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo; s.MediaCategory = MediaCategory.Communications; });
            way("media", s => { s.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo; s.MediaCategory = MediaCategory.Media; });
            way("raw audio", s => { s.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo; s.AudioProcessing = Windows.Media.AudioProcessing.Raw; });
            way("exclusive", s => s.SharingMode = MediaCaptureSharingMode.ExclusiveControl);
            way("cpu memory", s => s.MemoryPreference = MediaCaptureMemoryPreference.Cpu);
            foreach (var w in ways)
            {
                try { using (var mc = await OpenAsync(id, w.Value)) compare(w.Key, mc); }
                catch (Exception ex) { Log(w.Key + ": " + Err(ex)); }
            }
            foreach (CaptureUse use in new[] { CaptureUse.Video, CaptureUse.Photo })
                try
                {
                    using (var mc = await OpenAsync(id)) { mc.VideoDeviceController.PrimaryUse = use; compare("primary use " + use, mc); }
                }
                catch (Exception ex) { Log("primary use " + use + ": " + Err(ex)); }
            foreach (MediaCaptureOptimization o in Enum.GetValues(typeof(MediaCaptureOptimization)))
                try
                {
                    using (var mc = await OpenAsync(id)) { mc.VideoDeviceController.DesiredOptimization = o; compare("optimised for " + o, mc); }
                }
                catch (Exception ex) { Log("optimised for " + o + ": " + Err(ex)); }
            // Photo and preview size may limit what is left for video.
            foreach (var stream in new[] { MediaStreamType.Photo, MediaStreamType.VideoPreview })
                try
                {
                    using (var mc = await OpenAsync(id))
                        foreach (var f in mc.VideoDeviceController.GetAvailableMediaStreamProperties(stream).OfType<VideoEncodingProperties>().ToList())
                            try { await Limit(mc.VideoDeviceController.SetMediaStreamPropertiesAsync(stream, f).AsTask(), 8); compare(stream + " " + f.Width + "x" + f.Height, mc); }
                            catch (Exception ex) { Log(stream + " " + f.Width + "x" + f.Height + ": " + Err(ex)); }
                }
                catch (Exception ex) { Log(stream + ": " + Err(ex)); }
        }

        // ---------------- 2. Ask for unlisted modes and record them ----------------

        private static MediaEncodingProfile Profile(Target t)
        {
            var p = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            p.Audio = null;
            p.Video = VideoEncodingProperties.CreateH264();
            p.Video.Width = (uint)t.W;
            p.Video.Height = (uint)t.H;
            p.Video.PixelAspectRatio.Numerator = p.Video.PixelAspectRatio.Denominator = 1;
            p.Video.FrameRate.Numerator = (uint)t.Fps;
            p.Video.FrameRate.Denominator = 1;
            p.Video.Bitrate = (uint)VideoModes.RecommendedMbit(t.W, t.H, t.Fps) * 1000000;
            return p;
        }

        private static async Task<StorageFile> TempAsync()
        {
            return await ApplicationData.Current.TemporaryFolder.CreateFileAsync("lab.mp4", CreationCollisionOption.GenerateUniqueName);
        }

        /// <summary>Records for some seconds; what the camera stream was meanwhile goes to <paramref name="stream"/>.</summary>
        private static async Task<Mp4Info> RecordAsync(MediaCapture mc, MediaEncodingProfile profile, int seconds, Action<VideoEncodingProperties> stream = null)
        {
            var file = await TempAsync();
            try
            {
                await Limit(mc.StartRecordToStorageFileAsync(profile, file).AsTask(), 12);
                await Task.Delay(seconds * 1000);
                if (stream != null) stream(Now(mc));
                await Limit(mc.StopRecordAsync().AsTask(), 12);
                using (var s = await file.OpenStreamForReadAsync()) return Mp4Info.Read(s);
            }
            finally { var _ = DropAsync(file); }
        }

        private static async Task DropAsync(StorageFile f)
        {
            for (int i = 0; i < 4; i++)
            {
                try { await f.DeleteAsync(StorageDeleteOption.PermanentDelete); return; } catch { }
                await Task.Delay(1500);
            }
        }

        private static string Text(Mp4Info i)
        {
            return i == null ? "no usable file" : string.Format(CultureInfo.InvariantCulture, "{0}x{1}, {2:0.0} fps, {3:0}% nearly empty frames", i.Width, i.Height, i.Fps, i.TinyShare * 100);
        }

        private static async Task ModesAsync(string id, List<VideoMode> found)
        {
            foreach (var t in Targets)
            {
                Step("Trying " + t);
                foreach (int source in new[] { VideoMode.Forced, VideoMode.Negotiated })
                {
                    try
                    {
                        using (var mc = await OpenAsync(id))
                        {
                            bool accepted = false;
                            if (source == VideoMode.Forced)
                            {
                                var p = VideoEncodingProperties.CreateUncompressed("NV12", (uint)t.W, (uint)t.H);
                                p.FrameRate.Numerator = (uint)t.Fps;
                                p.FrameRate.Denominator = 1;
                                try { await Limit(mc.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, p).AsTask(), 8); accepted = Is(Now(mc), t); }
                                catch (Exception ex) { Log(t + " forced: " + Err(ex)); continue; }
                                Log(t + " forced: accepted, stream is " + Text(Now(mc)));
                                if (!accepted) continue;
                            }
                            VideoEncodingProperties during = null;
                            var info = await RecordAsync(mc, Profile(t), 4, p => during = p);
                            bool real = Is(during, t) && info != null && info.Fps >= t.Fps * 0.8 && info.TinyShare < 0.3;
                            Log(t + (source == VideoMode.Forced ? " forced" : " negotiated") + ": stream while recording " + Text(during) + ", file " + Text(info) + (real ? " => REAL" : " => not real"));
                            if (real) { VideoModes.Add(found, t.W, t.H, t.Fps, source); break; }
                        }
                    }
                    catch (Exception ex) { Log(t + " source " + source + ": " + Err(ex)); }
                    await Task.Delay(400);
                }
            }
        }

        // ---------------- 3. Which stamp is fastest? ----------------

        private static async Task StampAsync(string id)
        {
            var t = new Target { W = 1920, H = 1080, Fps = 30 };
            double best = 0, plain = 0;
            string name = null;
            foreach (var effect in new[] { null, "OverlayVideoEffect", "StampVideoEffect" })
            {
                Step(effect == null ? "Measuring without the stamp" : "Measuring the stamp (" + effect.Replace("VideoEffect", "") + ")");
                try
                {
                    using (var mc = await OpenAsync(id))
                    {
                        var props = CameraModes.Formats(mc).FirstOrDefault(p => Is(p, t));
                        if (props != null) await Limit(mc.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, props).AsTask(), 8);
                        if (effect != null)
                        {
                            var set = new PropertySet();
                            set["text"] = "2026-11-25 12:34:56   130 km/h";
                            await Limit(mc.AddVideoEffectAsync(new VideoEffectDefinition("DriveDeck.Effects." + effect, set), MediaStreamType.VideoRecord).AsTask(), 8);
                        }
                        var info = await RecordAsync(mc, Profile(t), 8);
                        Log("stamp " + (effect ?? "none") + ": " + Text(info));
                        if (info == null) continue;
                        if (effect == null) plain = info.Fps;
                        else if (info.Fps > best) { best = info.Fps; name = effect; }
                    }
                }
                catch (Exception ex) { Log("stamp " + (effect ?? "none") + ": " + Err(ex)); }
                await Task.Delay(400);
            }
            if (name == null) return;
            CameraModes.SetStampEffect(name);
            Summary.Add(string.Format(CultureInfo.InvariantCulture, "1080p with stamp: {0:0} fps (without: {1:0} fps)", best, plain));
        }

        // ---------------- 4. Both cameras at once ----------------

        private static async Task BothAsync(string backId, string frontId)
        {
            var hd = new Target { W = 1280, H = 720, Fps = 30 };
            foreach (bool backFirst in new[] { true, false })
            {
                Step("Both cameras, " + (backFirst ? "back" : "front") + " first");
                MediaCapture a = null, b = null;
                try
                {
                    a = await OpenAsync(backFirst ? backId : frontId);
                    Log("both: first camera open");
                    b = await OpenAsync(backFirst ? frontId : backId);
                    Log("both: second camera open");
                    var one = RecordAsync(a, Profile(hd), 6);
                    await Task.Delay(1500);
                    var two = RecordAsync(b, Profile(hd), 4);
                    Mp4Info i1 = null, i2 = null;
                    try { i1 = await one; } catch (Exception ex) { Log("both: first recording: " + Err(ex)); }
                    try { i2 = await two; } catch (Exception ex) { Log("both: second recording: " + Err(ex)); }
                    Log("both: first " + Text(i1) + "; second " + Text(i2));
                    if (i1 != null && i2 != null && i1.Seconds > 5 && i2.Seconds > 3 && i1.Fps > 10 && i2.Fps > 10)
                    {
                        CameraModes.SetBoth(true);
                        Summary.Add("Both cameras at once: works");
                        return;
                    }
                }
                catch (Exception ex) { Log("both: " + Err(ex)); }
                finally
                {
                    if (b != null) b.Dispose();
                    if (a != null) a.Dispose();
                }
                await Task.Delay(800);
            }
            CameraModes.SetBoth(false);
            Summary.Add("Both cameras at once: this phone does not allow it");
        }
    }
}
