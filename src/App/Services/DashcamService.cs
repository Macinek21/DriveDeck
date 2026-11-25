using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DriveDeck.Core;
using Windows.Devices.Enumeration;
using Windows.Foundation.Collections;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace DriveDeck.App.Services
{
    /// <summary>
    /// Loop dashcam: records fixed-length MP4 segments into Videos\DriveDeck\Loop, deletes the oldest segments to respect
    /// the storage cap and the phone's free-space reserve, burns in a GPS/time stamp (optional), writes .srt/.gpx sidecars
    /// (optional) and moves segments around an incident into Videos\DriveDeck\Events, where the loop never deletes them.
    /// </summary>
    public sealed class DashcamService
    {
        private const ulong GB = 1024UL * 1024 * 1024;
        private static readonly string[] SidecarExtensions = { ".mp4", ".srt", ".gpx" };

        private sealed class PendingEvent
        {
            public DateTime Time, Until;
            public string Reason;
            public double PeakG;
            public GpsFix Fix;
            public double? SpeedKmh;
        }

        private MediaCapture _mc;
        private MediaEncodingProfile _profile;
        private readonly PropertySet _fx = new PropertySet();
        private bool _effectAdded;
        private StorageFolder _loop, _events;
        private readonly List<SegmentInfo> _segments = new List<SegmentInfo>();
        private readonly List<PendingEvent> _pending = new List<PendingEvent>();
        private readonly object _lock = new object();
        private CancellationTokenSource _cts;
        private Task _loopTask;
        private volatile bool _rollover;
        private DashcamSettings _s;
        private VideoRotation _rotation = VideoRotation.None;
        private bool _overheated;
        private volatile bool _captureFailed;
        private int _failuresWithEffect;
        private VideoMode _mode;
        private MediaCapture _focusing;
        private CameraModes _modes = CameraModes.For(false);
        private bool _front;
        private bool _safeMode;          // after repeated failures: Full HD 30 or less until settings change
        private int _modeFailures;
        private bool _measured;

        /// <summary>The mode being recorded now; null when the camera is off.</summary>
        public bool IsFront { get { return _front; } }
        public VideoMode CurrentMode { get { return _mc != null ? _mode : null; } }
        /// <summary>Set when the camera should be re-created at once instead of at the next watchdog check.</summary>
        public volatile bool RestartSoon;

        /// <summary>Set when the burned-in stamp effect is not usable on this phone; speed then goes to the .srt track only.</summary>
        public static bool EffectDisabled { get; private set; }

        public Func<GpsFix> CurrentFix { get; set; }
        public Func<double?> CurrentSpeedKmh { get; set; }

        public MediaCapture Capture { get { return _mc; } }
        /// <summary>False after a camera failure, so the caller re-creates the camera instead of reusing a broken one.</summary>
        public bool IsInitialized { get { return _mc != null && !_captureFailed; } }
        public bool IsRecording { get { return _loopTask != null; } }
        public DateTime? SegmentStart { get; private set; }
        public ulong LoopBytes { get { lock (_lock) return (ulong)_segments.Sum(s => (long)s.SizeBytes); } }
        public string LastError { get; private set; }

        /// <summary>(message, isError). Raised on a background thread.</summary>
        public event Action<string, bool> StatusChanged;
        /// <summary>(folder name, segment count). Raised on a background thread.</summary>
        public event Action<string, int> EventSaved;

        private void Status(string msg, bool error)
        {
            AppLog.Info("Dashcam " + (error ? "ERROR: " : "") + msg);
            if (error) LastError = msg;
            var h = StatusChanged; if (h != null) h(msg, error);
        }

        /// <summary>Must be called on the UI thread (camera consent).</summary>
        public async Task<bool> InitializeAsync(DashcamSettings settings)
        {
            _s = settings;
            bool setting = false; // a failure now is about the mode, not about the camera being busy
            try
            {
                var videos = KnownFolders.VideosLibrary;
                var root = await videos.CreateFolderAsync("DriveDeck", CreationCollisionOption.OpenIfExists);
                _loop = await root.CreateFolderAsync("Loop", CreationCollisionOption.OpenIfExists);
                _events = await root.CreateFolderAsync("Events", CreationCollisionOption.OpenIfExists);
                await ScanLoopFolderAsync();

                var cams = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
                var want = settings.UseFrontCamera ? Windows.Devices.Enumeration.Panel.Front : Windows.Devices.Enumeration.Panel.Back;
                var cam = cams.FirstOrDefault(c => c.EnclosureLocation != null && c.EnclosureLocation.Panel == want)
                          ?? cams.FirstOrDefault();
                if (cam == null) { Status("No camera found", true); return false; }

                AppLog.Info("Camera: " + cam.Name + ", " + cams.Count + " camera(s)");
                _front = settings.UseFrontCamera && cam.EnclosureLocation != null && cam.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front;
                _modes = CameraModes.For(_front);
                _captureFailed = false;
                RestartSoon = false;
                _measured = false;
                await _modes.LearnAsync(cam.Id);
                _mode = Choose(_modes.Usable());
                if (_mode == null) { Status("The camera reports no usable video format", true); return false; }
                _mc = new MediaCapture();
                await _mc.InitializeAsync(CameraModes.Settings(cam.Id, settings.RecordAudio, _mode, CameraModes.Profiles(cam.Id)));
                // The front camera has fewer modes, so a different one is expected there.
                if (!_safeMode && !_front && (_mode.Resolution != settings.Resolution || _mode.Fps != settings.FrameRate))
                    Status("Not available: " + settings.Resolution + " " + settings.FrameRate + " fps. Recording " + _mode.Resolution + " " + _mode.Fps + " fps.", true);
                _mc.Failed += OnCaptureFailed;
                _mc.RecordLimitationExceeded += s => { AppLog.Info("RecordLimitationExceeded"); RequestRollover(); };
                if (ApiInformation.IsEventPresent("Windows.Media.Capture.MediaCapture", "ThermalStatusChanged"))
                    _mc.ThermalStatusChanged += OnThermal;

                setting = true;
                await ConfigureStreamAsync();
                setting = false;
                _profile = BuildProfile();
                AppLog.Info("Profile: " + _mode + " " + _profile.Video.Width + "x" + _profile.Video.Height + " @" + _profile.Video.FrameRate.Numerator + "/" +
                            _profile.Video.FrameRate.Denominator + " " + _profile.Video.Bitrate / 1000 + " kbit/s, audio " + (_profile.Audio != null));
                _effectAdded = false;
                if (settings.BurnInStamp && !EffectDisabled && _modes.Stamp(_mode) != StampState.Off)
                {
                    try
                    {
                        _fx["text"] = "";
                        await _mc.AddVideoEffectAsync(new VideoEffectDefinition("DriveDeck.Effects." + CameraModes.StampEffect, _fx), MediaStreamType.VideoRecord);
                        _effectAdded = true;
                        AppLog.Info("Burn-in stamp added: " + CameraModes.StampEffect);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("AddVideoEffect", ex);
                        DisableEffect();
                    }
                }
                UpdateRotation(DisplayInformation.GetForCurrentView().CurrentOrientation);
                Status("Camera ready", false);
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLog.Error("InitializeAsync", ex);
                Status("Camera or Videos access denied. Allow it in Settings > Privacy.", true);
                await DisposeCaptureAsync();
                return false;
            }
            catch (Exception ex)
            {
                AppLog.Error("InitializeAsync", ex);
                if (setting || (_mode != null && _mode.Special)) ModeFailed(ex.Message);
                Status("Camera could not start: " + ex.Message, true);
                await DisposeCaptureAsync();
                return false;
            }
        }

        private void DisableEffect()
        {
            if (EffectDisabled) return;
            EffectDisabled = true;
            Status("The burned-in speed stamp does not work with this camera. Speed and position are still saved in the subtitle track (.srt).", true);
        }

        private void OnCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs e)
        {
            AppLog.Info("MediaCapture.Failed code 0x" + e.Code.ToString("X8") + ": " + e.Message + (_effectAdded ? " (stamp effect on)" : ""));
            _captureFailed = true;
            ModeFailed(e.Message);
            // A failure with the GPU stamp effect active is retried once with it; a second one turns the effect off.
            if (_effectAdded && (_mode == null || !_mode.Demanding) && ++_failuresWithEffect >= 2) DisableEffect();
            Status("Camera error: " + e.Message + " – restarting the camera", true);
            RequestRollover();
        }

        private void OnThermal(MediaCapture sender, object args)
        {
            bool hot = sender.ThermalStatus == MediaCaptureThermalStatus.Overheated;
            if (hot == _overheated) return;
            _overheated = hot;
            if (hot) { Status("Phone is overheating – recording paused until it cools down", true); var _ = StopAsync(); }
            else Status("Phone cooled down", false);
        }

        public bool IsOverheated { get { return _overheated; } }

        private VideoMode Choose(List<VideoMode> modes)
        {
            return _safeMode ? VideoModes.Pick(modes.Where(m => !m.Demanding), "1080p", 30)
                             : VideoModes.Pick(modes, _s.Resolution, _s.FrameRate);
        }

        private async Task ConfigureStreamAsync()
        {
            var formats = CameraModes.Formats(_mc).ToList();
            // 720p is scaled down from the Full HD picture: the camera's own 720p stream is visibly softer.
            var fullHd = new VideoMode { Resolution = "1080p", Fps = _mode.Fps };
            var props = _mode.Resolution == "720p" ? formats.FirstOrDefault(p => CameraModes.Matches(p, fullHd)) : null;
            if (_mode.Source == VideoMode.Negotiated) return; // the recorder sets the stream
            if (_mode.Source == VideoMode.Forced)
            {
                props = VideoEncodingProperties.CreateUncompressed("NV12", (uint)_mode.Width, (uint)_mode.Height);
                props.FrameRate.Numerator = (uint)_mode.Fps;
                props.FrameRate.Denominator = 1;
            }
            if (props == null && _mode.Source != VideoMode.Profile)
                props = formats.Where(p => CameraModes.Matches(p, _mode)).OrderBy(p => p.Subtype == "NV12" ? 0 : 1).FirstOrDefault();
            if (props == null) return; // set by the profile
            await _mc.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, props);
            AppLog.Info("Camera stream: " + props.Width + "x" + props.Height);
            try
            {
                var ep = _mc.VideoDeviceController.ExposurePriorityVideoControl;
                AppLog.Info("Slower in the dark: supported " + ep.Supported + (ep.Supported ? ", was " + ep.Enabled : ""));
                if (ep.Supported) ep.Enabled = _s.SlowInDark;
            }
            catch (Exception ex) { AppLog.Error("ExposurePriority", ex); }
        }

        /// <summary>Sets the focus. The preview must be running. Called again by a tap on the camera picture.</summary>
        public async Task FocusAsync()
        {
            var mc = _mc;
            if (mc == null || _focusing == mc) return;
            _focusing = mc;
            try
            {
                var fc = mc.VideoDeviceController.FocusControl;
                if (!fc.Supported) { AppLog.Info("Focus: this camera has none to set"); return; }
                var modes = fc.SupportedFocusModes.ToList();
                var ranges = fc.SupportedFocusRanges.ToList();
                var distances = fc.SupportedFocusDistances.ToList();
                AppLog.Info("Focus: modes " + string.Join("/", modes) + ", ranges " + string.Join("/", ranges) + ", distances " + string.Join("/", distances) + ", now " + fc.Mode + " " + fc.FocusState);
                bool infinity = modes.Contains(FocusMode.Manual) && distances.Contains(ManualFocusDistance.Infinity);
                if (_s.Focus != "far" || !infinity)
                {
                    foreach (var mode in new[] { FocusMode.Continuous, FocusMode.Auto, FocusMode.Single })
                        foreach (var range in new[] { AutoFocusRange.Normal, AutoFocusRange.FullRange })
                        {
                            if (!modes.Contains(mode) || !ranges.Contains(range)) continue;
                            try
                            {
                                fc.Configure(new FocusSettings { Mode = mode, AutoFocusRange = range, WaitForFocus = false, DisableDriverFallback = false });
                                await fc.FocusAsync();
                            }
                            catch (Exception ex) { AppLog.Info("Focus " + mode + "/" + range + ": " + ex.Message); continue; }
                            for (int i = 0; i < 12 && _mc == mc && fc.FocusState != MediaCaptureFocusState.Focused && fc.FocusState != MediaCaptureFocusState.Failed; i++) await Task.Delay(200);
                            if (_mc != mc) return;
                            AppLog.Info("Focus " + mode + "/" + range + ": " + fc.FocusState);
                            if (fc.FocusState == MediaCaptureFocusState.Focused || (mode == FocusMode.Continuous && fc.FocusState == MediaCaptureFocusState.Searching)) return;
                        }
                    if (!infinity) return;
                    AppLog.Info("Focus did not succeed, set to the distance");
                }
                fc.Configure(new FocusSettings { Mode = FocusMode.Manual, Distance = ManualFocusDistance.Infinity, DisableDriverFallback = false });
                await fc.FocusAsync();
                if (_mc == mc) AppLog.Info("Focus set to " + fc.Mode + ", state " + fc.FocusState);
            }
            catch (Exception ex) { AppLog.Error("Focus", ex); }
            finally { if (_focusing == mc) _focusing = null; }
        }

        private MediaEncodingProfile BuildProfile()
        {
            var p = MediaEncodingProfile.CreateMp4(_mode.Height <= 720 ? VideoEncodingQuality.HD720p : VideoEncodingQuality.HD1080p);
            if (_mode.Demanding || _mode.Height < 720)
            {
                // The presets carry an encoder level that is too low for these; a bare description lets the encoder choose.
                p.Video = VideoEncodingProperties.CreateH264();
                p.Video.Width = (uint)_mode.Width;
                p.Video.Height = (uint)_mode.Height;
                p.Video.PixelAspectRatio.Numerator = 1;
                p.Video.PixelAspectRatio.Denominator = 1;
            }
            p.Video.Bitrate = (uint)_s.BitrateKbps * 1000;
            p.Video.FrameRate.Numerator = (uint)_mode.Fps;
            p.Video.FrameRate.Denominator = 1;
            if (!_s.RecordAudio) p.Audio = null;
            return p;
        }

        /// <summary>
        /// Takes over changed settings. Returns true when the camera has to be restarted for them; everything else applies at once.
        /// </summary>
        public bool ApplySettings(DashcamSettings n)
        {
            var o = _s;
            bool camera = o == null || o.Enabled != n.Enabled || o.Resolution != n.Resolution || o.FrameRate != n.FrameRate ||
                          o.BitrateKbps != n.BitrateKbps || o.RecordAudio != n.RecordAudio || o.BurnInStamp != n.BurnInStamp ||
                          o.UseFrontCamera != n.UseFrontCamera || o.Focus != n.Focus || o.SlowInDark != n.SlowInDark;
            if (o != null) _s = n;
            if (camera) { _safeMode = false; _modeFailures = 0; _failuresWithEffect = 0; EffectDisabled = false; }
            return camera;
        }

        /// <summary>
        /// A mode beyond Full HD 30 failed. First it is tried without the stamp; then, if it never worked, it is struck off the list,
        /// otherwise the app falls back to Full HD 30 after three failures in a row.
        /// </summary>
        private void ModeFailed(string why)
        {
            var m = _mode;
            if (m == null || !m.Demanding || _safeMode) return;
            AppLog.Info("Mode " + m + " failed (" + why + "), stamp " + _effectAdded);
            RestartSoon = true;
            if (_effectAdded) { _modes.SetStamp(m, StampState.Off); return; }
            if (_modes.RealFps(m) <= 0)
            {
                _modes.Refuse(m);
                Status(m.Resolution + " " + m.Fps + " fps does not work for this app (" + why + "). Recording the closest mode that works.", true);
            }
            else if (++_modeFailures >= 3)
            {
                _safeMode = true;
                Status(m.Resolution + " " + m.Fps + " fps does not work now. Recording at most Full HD 30 fps until a video setting is changed.", true);
            }
        }

        /// <summary>Is the first good clip of this session what the mode promises? Reads only the clip's index.</summary>
        private async Task CheckClipAsync(StorageFile file, double seconds)
        {
            var m = _mode;
            if (_measured || m == null || seconds < 4) return;
            _measured = true;
            Mp4Info info;
            using (var s = await file.OpenStreamForReadAsync()) info = Mp4Info.Read(s);
            if (info == null || info.Fps <= 0) { AppLog.Info("Mode " + m + ": the clip could not be measured"); return; }
            double fps = info.Fps;
            AppLog.Info(string.Format(CultureInfo.InvariantCulture, "Mode {0}: file has {1:0.0} fps, {2}x{3}, {4:0.0} of {5:0.0} s, stamp {6}",
                m, fps, info.Width, info.Height, info.Seconds, seconds, _effectAdded));
            _modeFailures = 0;
            double before = _modes.RealFps(m);
            if (fps < m.Fps * 0.5 && m.Demanding && !_effectAdded) { ModeFailed("it delivered only " + Math.Round(fps) + " fps"); return; }
            if (fps < m.Fps * 0.85 && _effectAdded && _modes.Stamp(m) == StampState.Untested)
            {
                // Too slow with the stamp: compare without it.
                _modes.SaveRealFps(m, fps);
                _modes.SetStamp(m, StampState.Off);
                Status("The burned-in stamp slows " + m.Resolution + " " + m.Fps + " fps down to " + Math.Round(fps) + " fps, trying without it. Speed stays in the subtitle file.", false);
                RestartSoon = true;
                _captureFailed = true;
                RequestRollover();
                return;
            }
            if (!_effectAdded && _modes.Stamp(m) == StampState.Off && before > 0 && fps < before * 1.25)
                _modes.SetStamp(m, StampState.Keep); // it was not the stamp (darkness, for example): bring it back next time
            _modes.SaveRealFps(m, fps);
            if (_effectAdded && _modes.Stamp(m) == StampState.Untested && fps >= m.Fps * 0.85) _modes.SetStamp(m, StampState.Keep);
            if (fps < m.Fps * 0.9) Status("The phone manages about " + Math.Round(fps) + " fps in this mode.", false);
        }

        /// <summary>Back camera on a portrait-native phone: the sensor is aligned with landscape.</summary>
        public void UpdateRotation(DisplayOrientations o)
        {
            switch (o)
            {
                case DisplayOrientations.Portrait: _rotation = _front ? VideoRotation.Clockwise270Degrees : VideoRotation.Clockwise90Degrees; break;
                case DisplayOrientations.LandscapeFlipped: _rotation = VideoRotation.Clockwise180Degrees; break;
                case DisplayOrientations.PortraitFlipped: _rotation = _front ? VideoRotation.Clockwise90Degrees : VideoRotation.Clockwise270Degrees; break;
                default: _rotation = VideoRotation.None; break;
            }
            try { if (_mc != null) _mc.SetPreviewRotation(_rotation); } catch { }
        }

        private async Task ScanLoopFolderAsync()
        {
            var files = await _loop.GetFilesAsync();
            var groups = files.GroupBy(f => System.IO.Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase);
            var list = new List<SegmentInfo>();
            foreach (var g in groups)
            {
                DateTime start;
                if (!SegmentNaming.TryParse(g.Key, out start)) continue; // not our file: never touched
                var video = g.FirstOrDefault(f => f.FileType.Equals(".mp4", StringComparison.OrdinalIgnoreCase));
                if (video == null)
                {
                    // Our own leftover .srt/.gpx without a video (e.g. after a camera failure): remove it.
                    foreach (var f in g.Where(f => f.FileType == ".srt" || f.FileType == ".gpx"))
                        try { await f.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                    continue;
                }
                ulong size = 0;
                DateTime end = start;
                if (DateTime.Now - start > TimeSpan.FromMinutes(1) && (await video.GetBasicPropertiesAsync()).Size == 0)
                {
                    foreach (var f in g) // an empty clip, left when the app was closed at a bad moment
                        try { await f.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                    continue;
                }
                foreach (var f in g)
                {
                    BasicProperties bp = await f.GetBasicPropertiesAsync();
                    size += bp.Size;
                    if (f == video) end = bp.DateModified.LocalDateTime;
                }
                list.Add(new SegmentInfo { BaseName = g.Key, Start = start, End = end < start ? start : end, SizeBytes = size });
            }
            lock (_lock) { _segments.Clear(); _segments.AddRange(list.OrderBy(s => s.Start)); }
        }

        public void Start()
        {
            if (_mc == null || _captureFailed || _loopTask != null || _overheated) return;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loopTask = Task.Run(() => LoopAsync(ct));
        }

        public async Task StopAsync()
        {
            var cts = _cts; var task = _loopTask;
            if (cts == null) return;
            cts.Cancel();
            try { if (task != null) await task; } catch { }
            _cts = null; _loopTask = null;
            cts.Dispose();
        }

        public async Task ShutdownAsync()
        {
            await StopAsync();
            await DisposeCaptureAsync();
        }

        private async Task DisposeCaptureAsync()
        {
            var mc = _mc;
            _mc = null;
            if (mc == null) return;
            try { if (_effectAdded) await mc.ClearEffectsAsync(MediaStreamType.VideoRecord); } catch { }
            _effectAdded = false;
            try { mc.Dispose(); } catch { }
        }

        public void RequestRollover() { _rollover = true; }

        private static ulong Gb(double gb) { return (ulong)(gb * GB); }

        private async Task<ulong> FreeBytesAsync()
        {
            try
            {
                var props = await ApplicationData.Current.LocalFolder.Properties.RetrievePropertiesAsync(new[] { "System.FreeSpace" });
                object v;
                if (props.TryGetValue("System.FreeSpace", out v) && v is ulong) return (ulong)v;
            }
            catch { }
            return ulong.MaxValue / 4; // unknown: rely on the cap only
        }

        private async Task<bool> MakeRoomAsync()
        {
            ulong incoming = LoopStoragePolicy.EstimateSegmentBytes(_s.BitrateKbps * 1000, _s.RecordAudio, _s.SegmentSeconds);
            ulong free = await FreeBytesAsync();
            ulong? cap = _s.UnlimitedStorage ? (ulong?)null : Gb(_s.StorageCapGb);
            List<SegmentInfo> doomed;
            lock (_lock)
            {
                // Footage that an incident may still need is never overwritten.
                DateTime keepFrom = (_pending.Count > 0 ? _pending.Min(p => p.Time) : DateTime.Now).AddSeconds(-_s.PreEventSeconds);
                doomed = LoopStoragePolicy.SelectForDeletion(_segments, cap, free, Gb(_s.ReserveFreeGb), incoming, keepFrom);
            }
            foreach (var seg in doomed)
            {
                foreach (var ext in SidecarExtensions)
                {
                    try
                    {
                        var item = await _loop.TryGetItemAsync(seg.BaseName + ext);
                        if (item != null) await item.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    catch { }
                }
                lock (_lock) _segments.Remove(seg);
                free += seg.SizeBytes;
            }
            if (free < Gb(_s.ReserveFreeGb) + incoming)
            {
                Status("Phone storage is full (saved incidents or other files). Recording stopped.", true);
                return false;
            }
            return true;
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            Status("Recording", false);
            DateTime? lastEnd = null;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!await MakeRoomAsync()) break;
                    DateTime start = DateTime.Now;
                    StorageFile file = await _loop.CreateFileAsync(SegmentNaming.NameFor(start) + ".mp4", CreationCollisionOption.GenerateUniqueName);
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                    _mc.SetRecordRotation(_rotation);
                    var rec = await _mc.PrepareLowLagRecordToStorageFileAsync(_profile, file);
                    await rec.StartAsync();
                    start = DateTime.Now; // the subtitle clock starts with the video
                    if (!lastEnd.HasValue) { var _ = FocusAsync(); } // starting to record may reset it
                    if (lastEnd.HasValue) AppLog.Info("Gap since previous clip: " + (int)(start - lastEnd.Value).TotalMilliseconds + " ms");
                    SegmentStart = start;
                    _rollover = false;

                    var seg = new SegmentInfo { BaseName = baseName, Start = start, InProgress = true };
                    lock (_lock) _segments.Add(seg);
                    var srt = new SrtBuilder(start);
                    var track = new List<GpsFix>();
                    DateTime end = start.AddSeconds(_s.SegmentSeconds);
                    DateTime cueStart = start;
                    string cueText = null;
                    GpsFix lastLogged = null;

                    try
                    {
                        while (!ct.IsCancellationRequested && !_rollover && DateTime.Now < end && !EventDue())
                        {
                            DateTime now = DateTime.Now;
                            GpsFix fix = CurrentFix != null ? CurrentFix() : null;
                            double? speed = CurrentSpeedKmh != null ? CurrentSpeedKmh() : null;
                            if (_effectAdded)
                                _fx["text"] = StampFormatter.Format(now, fix, speed, new StampOptions
                                {
                                    DateTime = _s.StampDateTime, Speed = _s.StampSpeed && _s.GpsEnabled,
                                    Coordinates = _s.StampCoordinates && _s.GpsEnabled, Label = _s.StampLabel
                                });
                            if (now.Second != cueStart.Second || (now - cueStart).TotalSeconds >= 1)
                            {
                                if (cueText != null && _s.SubtitleTrack) srt.AddCue(cueStart, now, cueText);
                                cueStart = now;
                                cueText = StampFormatter.Format(now, fix, speed, new StampOptions
                                { DateTime = true, Speed = _s.GpsEnabled, Coordinates = _s.GpsEnabled, Label = "" });
                                if (_s.GpxTrack && fix != null && fix != lastLogged) { track.Add(fix); lastLogged = fix; }
                            }
                            await Task.Delay(200);
                        }
                    }
                    finally
                    {
                        lastEnd = DateTime.Now;
                        try { await rec.FinishAsync(); }
                        catch (Exception ex) { AppLog.Error("FinishAsync", ex); Status("Could not finish segment: " + ex.Message, true); }
                        SegmentStart = null;
                    }

                    if (cueText != null && _s.SubtitleTrack) srt.AddCue(cueStart, lastEnd.Value, cueText);
                    var videoFile = await _loop.TryGetItemAsync(file.Name) as StorageFile;
                    ulong videoSize = videoFile != null ? (await videoFile.GetBasicPropertiesAsync()).Size : 0;
                    AppLog.Info("Segment " + baseName + " finished: " + (lastEnd.Value - start).TotalSeconds.ToString("0.0") + " s, " + videoSize / 1024 + " KB");
                    if (videoSize == 0)
                    {
                        // Nothing usable was recorded: drop the empty file, keep no sidecars, and let the camera restart.
                        lock (_lock) _segments.Remove(seg);
                        try { if (videoFile != null) await videoFile.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                        _captureFailed = true;
                        if (!ct.IsCancellationRequested || (lastEnd.Value - start).TotalSeconds > 4) ModeFailed("no video arrived");
                        Status("The camera produced no video. Restarting the camera.", true);
                        break;
                    }
                    ulong size = videoSize;
                    if (_s.SubtitleTrack && srt.ToString().Length > 0)
                        size += await WriteSidecarAsync(baseName + ".srt", srt.ToString());
                    if (_s.GpxTrack && track.Count > 0)
                        size += await WriteSidecarAsync(baseName + ".gpx", GpxBuilder.Build(baseName, track));
                    seg.End = lastEnd;
                    seg.SizeBytes = size;
                    seg.InProgress = false;

                    try { await CheckClipAsync(videoFile, (lastEnd.Value - start).TotalSeconds); } catch (Exception ex) { AppLog.Error("CheckClip", ex); }
                    await ProcessEventsAsync(ct.IsCancellationRequested);
                    if (_captureFailed) break; // re-created by the watchdog
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("LoopAsync", ex);
                _captureFailed = true;
                ModeFailed(ex.Message);
                Status("Recording stopped: " + ex.Message, true);
            }
            finally
            {
                SegmentStart = null;
                lock (_lock) _segments.RemoveAll(s => s.InProgress);
                try { await ProcessEventsAsync(true); } catch { }
                _loopTask = null;
                Status("Not recording", false);
            }
        }

        private async Task<ulong> WriteSidecarAsync(string name, string content)
        {
            try
            {
                var f = await _loop.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(f, content);
                return (await f.GetBasicPropertiesAsync()).Size;
            }
            catch { return 0; }
        }

        // ---------------- Incidents ----------------

        /// <summary>Marks an incident now: footage from PreEventSeconds before until PostEventSeconds after is kept.</summary>
        public void TriggerEvent(string reason, double peakG)
        {
            if (_s == null) return;
            DateTime now = DateTime.Now;
            lock (_lock)
            {
                var open = _pending.FirstOrDefault(p => now <= p.Until);
                if (open != null)
                {
                    open.Until = now.AddSeconds(_s.PostEventSeconds);
                    open.PeakG = Math.Max(open.PeakG, peakG);
                    return;
                }
                _pending.Add(new PendingEvent
                {
                    Time = now, Until = now.AddSeconds(_s.PostEventSeconds), Reason = reason, PeakG = peakG,
                    Fix = CurrentFix != null ? CurrentFix() : null, SpeedKmh = CurrentSpeedKmh != null ? CurrentSpeedKmh() : null
                });
            }
            if (!IsRecording) { var _ = ProcessEventsAsync(true); }
        }

        public bool HasPendingEvent { get { lock (_lock) return _pending.Count > 0; } }

        private bool EventDue()
        {
            DateTime now = DateTime.Now;
            lock (_lock) return _pending.Any(p => now >= p.Until);
        }

        private async Task ProcessEventsAsync(bool force)
        {
            List<PendingEvent> due;
            DateTime now = DateTime.Now;
            lock (_lock)
            {
                due = _pending.Where(p => force || now >= p.Until).ToList();
                foreach (var d in due) _pending.Remove(d);
            }
            foreach (var ev in due)
            {
                List<SegmentInfo> segs;
                lock (_lock) segs = EventWindow.SegmentsFor(_segments.Where(s => !s.InProgress), ev.Time,
                                                           TimeSpan.FromSeconds(_s.PreEventSeconds), ev.Until - ev.Time);
                string safeReason = ev.Reason == "manual" ? "manual" : "impact";
                var folder = await _events.CreateFolderAsync(SegmentNaming.NameFor(ev.Time) + "_" + safeReason, CreationCollisionOption.GenerateUniqueName);
                int moved = 0;
                foreach (var seg in segs)
                {
                    foreach (var ext in SidecarExtensions)
                    {
                        try
                        {
                            var item = await _loop.TryGetItemAsync(seg.BaseName + ext) as StorageFile;
                            if (item != null) { await item.MoveAsync(folder, item.Name, NameCollisionOption.GenerateUniqueName); if (ext == ".mp4") moved++; }
                        }
                        catch { }
                    }
                    lock (_lock) _segments.Remove(seg);
                }
                await WriteEventInfoAsync(folder, ev, segs);
                var h = EventSaved; if (h != null) h(folder.Name, moved);
            }
        }

        private static async Task WriteEventInfoAsync(StorageFolder folder, PendingEvent ev, List<SegmentInfo> segs)
        {
            var inv = CultureInfo.InvariantCulture;
            var lines = new List<string>
            {
                "DriveDeck incident",
                "Type: " + (ev.Reason == "manual" ? "saved manually" : "impact detected"),
                "Time: " + ev.Time.ToString("yyyy-MM-dd HH:mm:ss", inv),
                "Peak acceleration: " + ev.PeakG.ToString("0.00", inv) + " g",
                "Speed: " + (ev.SpeedKmh.HasValue ? Math.Round(ev.SpeedKmh.Value).ToString(inv) + " km/h" : "unknown"),
                "Position: " + (ev.Fix != null ? ev.Fix.Latitude.ToString("0.000000", inv) + ", " + ev.Fix.Longitude.ToString("0.000000", inv) : "unknown"),
                "Video files: " + (segs.Count == 0 ? "none (recording was not running)" : string.Join(", ", segs.Select(s => s.BaseName + ".mp4"))),
            };
            try
            {
                var f = await folder.CreateFileAsync("incident.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteLinesAsync(f, lines);
            }
            catch { }
        }

        public async Task<ulong> EventsBytesAsync()
        {
            if (_events == null) return 0;
            ulong total = 0;
            try
            {
                foreach (var folder in await _events.GetFoldersAsync())
                    foreach (var f in await folder.GetFilesAsync())
                        total += (await f.GetBasicPropertiesAsync()).Size;
            }
            catch { }
            return total;
        }

        public StorageFolder LoopFolder { get { return _loop; } }
        public StorageFolder EventsFolder { get { return _events; } }
    }
}
