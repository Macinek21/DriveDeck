using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DriveDeck.Core;
using Windows.Devices.Enumeration;
using Windows.Foundation.Collections;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display;
using Windows.Media.Capture;
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
            try
            {
                var videos = KnownFolders.VideosLibrary;
                var root = await videos.CreateFolderAsync("DriveDeck", CreationCollisionOption.OpenIfExists);
                _loop = await root.CreateFolderAsync("Loop", CreationCollisionOption.OpenIfExists);
                _events = await root.CreateFolderAsync("Events", CreationCollisionOption.OpenIfExists);
                await ScanLoopFolderAsync();

                var cams = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
                var cam = cams.FirstOrDefault(c => c.EnclosureLocation != null && c.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Back)
                          ?? cams.FirstOrDefault();
                if (cam == null) { Status("No camera found", true); return false; }

                AppLog.Info("Camera: " + cam.Name + ", " + cams.Count + " camera(s)");
                _captureFailed = false;
                _mc = new MediaCapture();
                await _mc.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = cam.Id,
                    StreamingCaptureMode = settings.RecordAudio ? StreamingCaptureMode.AudioAndVideo : StreamingCaptureMode.Video,
                });
                _mc.Failed += OnCaptureFailed;
                _mc.RecordLimitationExceeded += s => { AppLog.Info("RecordLimitationExceeded"); RequestRollover(); };
                if (ApiInformation.IsEventPresent("Windows.Media.Capture.MediaCapture", "ThermalStatusChanged"))
                    _mc.ThermalStatusChanged += OnThermal;

                await ConfigureStreamAsync();
                _profile = BuildProfile();
                AppLog.Info("Profile: " + _profile.Video.Width + "x" + _profile.Video.Height + " @" + _profile.Video.FrameRate.Numerator + "/" +
                            _profile.Video.FrameRate.Denominator + " " + _profile.Video.Bitrate / 1000 + " kbit/s, audio " + (_profile.Audio != null));
                _effectAdded = false;
                if (settings.BurnInStamp && !EffectDisabled)
                {
                    try
                    {
                        _fx["text"] = "";
                        await _mc.AddVideoEffectAsync(new VideoEffectDefinition("DriveDeck.Effects.OverlayVideoEffect", _fx), MediaStreamType.VideoRecord);
                        _effectAdded = true;
                        AppLog.Info("Burn-in stamp effect added");
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
            // A failure with the GPU stamp effect active is retried once with it; a second one turns the effect off.
            if (_effectAdded && ++_failuresWithEffect >= 2) DisableEffect();
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

        private async Task ConfigureStreamAsync()
        {
            uint wantH = _s.Resolution == "1080p" ? 1080u : 720u;
            var props = _mc.VideoDeviceController.GetAvailableMediaStreamProperties(MediaStreamType.VideoRecord)
                .OfType<VideoEncodingProperties>()
                .Where(p => p.Height == wantH && p.FrameRate.Denominator != 0)
                .OrderBy(p => Math.Abs((double)p.FrameRate.Numerator / p.FrameRate.Denominator - _s.FrameRate))
                .FirstOrDefault();
            if (props != null) await _mc.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoRecord, props);
        }

        private MediaEncodingProfile BuildProfile()
        {
            var p = MediaEncodingProfile.CreateMp4(_s.Resolution == "1080p" ? VideoEncodingQuality.HD1080p : VideoEncodingQuality.HD720p);
            p.Video.Bitrate = (uint)_s.BitrateKbps * 1000;
            p.Video.FrameRate.Numerator = (uint)_s.FrameRate;
            p.Video.FrameRate.Denominator = 1;
            if (!_s.RecordAudio) p.Audio = null;
            return p;
        }

        /// <summary>Back camera on a portrait-native phone: the sensor is aligned with landscape.</summary>
        public void UpdateRotation(DisplayOrientations o)
        {
            switch (o)
            {
                case DisplayOrientations.Portrait: _rotation = VideoRotation.Clockwise90Degrees; break;
                case DisplayOrientations.LandscapeFlipped: _rotation = VideoRotation.Clockwise180Degrees; break;
                case DisplayOrientations.PortraitFlipped: _rotation = VideoRotation.Clockwise270Degrees; break;
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
            lock (_lock) doomed = LoopStoragePolicy.SelectForDeletion(_segments, cap, free, Gb(_s.ReserveFreeGb), incoming);
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
                        try { await rec.FinishAsync(); }
                        catch (Exception ex) { AppLog.Error("FinishAsync", ex); Status("Could not finish segment: " + ex.Message, true); }
                        SegmentStart = null;
                    }

                    if (cueText != null && _s.SubtitleTrack) srt.AddCue(cueStart, DateTime.Now, cueText);
                    var videoFile = await _loop.TryGetItemAsync(file.Name) as StorageFile;
                    ulong videoSize = videoFile != null ? (await videoFile.GetBasicPropertiesAsync()).Size : 0;
                    AppLog.Info("Segment " + baseName + " finished: " + (DateTime.Now - start).TotalSeconds.ToString("0.0") + " s, " + videoSize / 1024 + " KB");
                    if (videoSize == 0)
                    {
                        // Nothing usable was recorded: drop the empty file, keep no sidecars, and let the camera restart.
                        lock (_lock) _segments.Remove(seg);
                        try { if (videoFile != null) await videoFile.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                        _captureFailed = true;
                        Status("The camera produced no video. Restarting the camera.", true);
                        break;
                    }
                    ulong size = videoSize;
                    if (_s.SubtitleTrack && srt.ToString().Length > 0)
                        size += await WriteSidecarAsync(baseName + ".srt", srt.ToString());
                    if (_s.GpxTrack && track.Count > 0)
                        size += await WriteSidecarAsync(baseName + ".gpx", GpxBuilder.Build(baseName, track));
                    seg.End = DateTime.Now;
                    seg.SizeBytes = size;
                    seg.InProgress = false;

                    await ProcessEventsAsync(ct.IsCancellationRequested);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("LoopAsync", ex);
                _captureFailed = true;
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
