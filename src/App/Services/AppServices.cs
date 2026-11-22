using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DriveDeck.Core;
using Windows.ApplicationModel.Core;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Geolocation;
using Windows.Devices.Sensors;
using Windows.Storage;
using Windows.UI.Core;

namespace DriveDeck.App.Services
{
    public static class Ui
    {
        public static void Run(Action a)
        {
            var d = CoreApplication.MainView.CoreWindow.Dispatcher;
            if (d.HasThreadAccess) a();
            else { var _ = d.RunAsync(CoreDispatcherPriority.Normal, () => a()); }
        }

        public static Task<T> RunAsync<T>(Func<Task<T>> f)
        {
            var d = CoreApplication.MainView.CoreWindow.Dispatcher;
            if (d.HasThreadAccess) return f();
            var tcs = new TaskCompletionSource<T>();
            var _ = d.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try { tcs.SetResult(await f()); } catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }
    }

    /// <summary>
    /// Diagnostic log in Videos\DriveDeck\DriveDeck-log.txt (readable over USB). Contains app events and errors only:
    /// no positions, no OBD values. Kept under ~256 KB by rotating to DriveDeck-log.old.txt.
    /// </summary>
    public static class AppLog
    {
        private const ulong MaxBytes = 256 * 1024;
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static StorageFolder _folder;

        public static void Info(string msg) { var _ = WriteAsync("INFO  " + msg); }

        public static void Error(string where, Exception ex)
        {
            var _ = WriteAsync("ERROR " + where + ": " + (ex == null ? "" : ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + " " + ex.Message + "\r\n" + ex.StackTrace));
        }

        private static async Task WriteAsync(string line)
        {
            try
            {
                await Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_folder == null)
                        _folder = await KnownFolders.VideosLibrary.CreateFolderAsync("DriveDeck", CreationCollisionOption.OpenIfExists);
                    var file = await _folder.CreateFileAsync("DriveDeck-log.txt", CreationCollisionOption.OpenIfExists);
                    if ((await file.GetBasicPropertiesAsync()).Size > MaxBytes)
                    {
                        await file.RenameAsync("DriveDeck-log.old.txt", NameCollisionOption.ReplaceExisting);
                        file = await _folder.CreateFileAsync("DriveDeck-log.txt", CreationCollisionOption.OpenIfExists);
                    }
                    await FileIO.AppendTextAsync(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + "\r\n");
                }
                finally { Gate.Release(); }
            }
            catch { /* logging must never break the app */ }
        }
    }

    public static class SettingsStore
    {
        private const string FileName = "settings.json";

        public static async Task<AppSettings> LoadAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName) as StorageFile;
                if (file != null)
                {
                    List<string> notes; string err;
                    var s = AppSettings.FromJson(await FileIO.ReadTextAsync(file), out notes, out err);
                    if (s != null) return s;
                }
            }
            catch { }
            return AppSettings.Defaults();
        }

        /// <summary>Writes to a temporary file first so a crash or power loss cannot leave a half-written settings file.</summary>
        public static async Task SaveAsync(AppSettings s)
        {
            s.Normalize();
            var folder = ApplicationData.Current.LocalFolder;
            var tmp = await folder.CreateFileAsync(FileName + ".tmp", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(tmp, s.ToJson());
            await tmp.RenameAsync(FileName, NameCollisionOption.ReplaceExisting);
        }
    }

    public sealed class Sample
    {
        public double Value;
        public DateTime Time;
    }

    /// <summary>Latest value per key, written from any thread, read by the UI at its own refresh rate.</summary>
    public sealed class ValueHub
    {
        private readonly ConcurrentDictionary<string, Sample> _values = new ConcurrentDictionary<string, Sample>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _unsupported = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public void Set(string key, double v) { _values[key] = new Sample { Value = v, Time = DateTime.Now }; }
        public void MarkUnsupported(string key) { _unsupported[key] = true; }
        public bool IsUnsupported(string key) { return _unsupported.ContainsKey(key); }
        public void ClearUnsupported() { _unsupported.Clear(); }
        public Sample Get(string key) { Sample s; return _values.TryGetValue(key, out s) ? s : null; }
    }

    public enum Severity { Info, Warning, Alarm }

    public sealed class Notice
    {
        public string Key { get; set; }
        public Severity Severity { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
        public DateTime Time { get; set; }
        public string TimeText { get { return Time.ToString("HH:mm"); } }
        public string Glyph { get { return Severity == Severity.Alarm ? "" : Severity == Severity.Warning ? "" : ""; } }
    }

    /// <summary>Notification centre (UI thread only). Notices with the same key replace each other.</summary>
    public sealed class NotificationCenter
    {
        public ObservableCollection<Notice> Items { get; private set; }
        public event Action<Notice> Posted;
        private readonly Dictionary<string, DateTime> _lastPosted = new Dictionary<string, DateTime>();

        public NotificationCenter() { Items = new ObservableCollection<Notice>(); }

        public void Post(string key, Severity sev, string title, string detail, TimeSpan? throttle = null)
        {
            Ui.Run(() =>
            {
                DateTime last;
                if (throttle.HasValue && _lastPosted.TryGetValue(key, out last) && DateTime.Now - last < throttle.Value) return;
                _lastPosted[key] = DateTime.Now;
                var existing = Items.FirstOrDefault(n => n.Key == key);
                if (existing != null) Items.Remove(existing);
                if (!key.StartsWith("dtc:")) AppLog.Info("Notice [" + sev + "] " + title + ": " + detail);
                var n2 = new Notice { Key = key, Severity = sev, Title = title, Detail = detail, Time = DateTime.Now };
                Items.Insert(0, n2);
                while (Items.Count > 30) Items.RemoveAt(Items.Count - 1);
                var h = Posted; if (h != null) h(n2);
            });
        }

        public void Remove(string key)
        {
            Ui.Run(() =>
            {
                var existing = Items.FirstOrDefault(n => n.Key == key);
                if (existing != null) Items.Remove(existing);
            });
        }
    }

    public sealed class LocationService
    {
        private Geolocator _geo;
        private GpsFix _last, _prev;
        public double? SpeedKmh { get; private set; }
        public GpsFix Last { get { var l = _last; return l != null && (DateTime.Now - l.Time).TotalSeconds < 10 ? l : null; } }
        public string State { get; private set; }
        public event Action<GpsFix> Updated;

        /// <summary>Call on the UI thread.</summary>
        public async Task<bool> StartAsync()
        {
            State = "Starting";
            var access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed) { State = "Location access denied"; return false; }
            _geo = new Geolocator { DesiredAccuracy = PositionAccuracy.High, ReportInterval = 1000, MovementThreshold = 0 };
            _geo.PositionChanged += OnPosition;
            _geo.StatusChanged += (s, e) => State = e.Status == PositionStatus.Ready ? "GPS fix" : e.Status.ToString();
            return true;
        }

        public void Stop()
        {
            if (_geo != null) _geo.PositionChanged -= OnPosition;
            _geo = null;
            State = "Off";
        }

        private void OnPosition(Geolocator sender, PositionChangedEventArgs args)
        {
            var c = args.Position.Coordinate;
            var p = c.Point.Position;
            double? speed = c.Speed.HasValue && !double.IsNaN(c.Speed.Value) ? c.Speed.Value * 3.6 : (double?)null;
            var fix = new GpsFix
            {
                Time = DateTime.Now, Latitude = p.Latitude, Longitude = p.Longitude,
                Altitude = double.IsNaN(p.Altitude) ? (double?)null : p.Altitude,
                SpeedKmh = speed, Accuracy = c.Accuracy, Heading = c.Heading
            };
            _prev = _last; _last = fix;
            SpeedKmh = GeoMath.EffectiveSpeedKmh(_prev, fix);
            if (SpeedKmh.HasValue && SpeedKmh.Value < 2) SpeedKmh = 0; // suppress GPS jitter when parked
            var h = Updated; if (h != null) h(fix);
        }
    }

    public sealed class ImpactService
    {
        private Accelerometer _acc;
        private ImpactDetector _det;
        public event Action<double> Impact;
        public double CurrentG { get { return _det != null ? _det.LastDynamicG : 0; } }
        public bool Available { get { return Accelerometer.GetDefault() != null; } }

        public bool Start(double thresholdG)
        {
            Stop();
            _acc = Accelerometer.GetDefault();
            if (_acc == null) return false;
            _det = new ImpactDetector(thresholdG);
            _acc.ReportInterval = Math.Max(_acc.MinimumReportInterval, 20u);
            _acc.ReadingChanged += OnReading;
            return true;
        }

        public void SetThreshold(double g) { if (_det != null) _det.ThresholdG = g; }

        public void Stop()
        {
            if (_acc != null) { _acc.ReadingChanged -= OnReading; _acc.ReportInterval = 0; }
            _acc = null;
        }

        private void OnReading(Accelerometer sender, AccelerometerReadingChangedEventArgs args)
        {
            var r = args.Reading;
            var det = _det;
            if (det == null) return;
            if (det.Feed(r.AccelerationX, r.AccelerationY, r.AccelerationZ, DateTime.Now))
            {
                var h = Impact; if (h != null) h(det.LastDynamicG);
            }
        }
    }

    /// <summary>Owns all long-lived services for the app session.</summary>
    public static class AppHub
    {
        public static AppSettings Settings { get; set; }
        public static readonly ValueHub Values = new ValueHub();
        public static readonly NotificationCenter Notices = new NotificationCenter();
        public static readonly DashcamService Dashcam = new DashcamService();
        public static readonly LocationService Location = new LocationService();
        public static readonly ImpactService Impact = new ImpactService();
        public static ObdPoller Obd { get; private set; }
        public static IReadOnlyList<Dtc> CurrentDtcs { get; set; }
        public static string ObdStatus { get; set; }

        public static IEnumerable<ValueDefinition> AllValues { get { return PidCatalog.All(Settings.Obd.CustomValues); } }

        public static async Task SaveSettingsAsync() { await SettingsStore.SaveAsync(Settings); }

        public static async Task RestartObdAsync()
        {
            if (Obd != null) { await Obd.StopAsync(); Obd = null; }
            Values.ClearUnsupported();
            CurrentDtcs = new List<Dtc>();
            var o = Settings.Obd;
            if (!o.Enabled || string.IsNullOrEmpty(o.DeviceId)) { ObdStatus = o.Enabled ? "No adapter selected" : "OBD off"; return; }
            string id = o.DeviceId;
            var poller = new ObdPoller(async ct =>
            {
                // Resolving the RFCOMM service may show a consent prompt, so it must run on the UI thread.
                var svc = await Ui.RunAsync(async () => await RfcommDeviceService.FromIdAsync(id));
                return await BluetoothElmTransport.ConnectAsync(svc, ct);
            }, new ObdPollerOptions
            {
                Protocol = o.Protocol, DtcIntervalSeconds = o.DtcIntervalSeconds, ReadPermanentDtcs = o.ReadPermanentDtcs, FuelType = o.FuelType
            });
            poller.ValueUpdated += (k, v) => Values.Set(k, v);
            poller.ValueUnsupported += k => Values.MarkUnsupported(k);
            poller.StateChanged += (s, m) =>
            {
                ObdStatus = m;
                if (s == ObdState.Connected) Notices.Post("obd", Severity.Info, "OBD connected", m);
                else if (s == ObdState.Failed) Notices.Post("obd", Severity.Warning, "OBD adapter not reachable", m, TimeSpan.FromMinutes(2));
            };
            poller.DtcsChanged += (all, fresh) =>
            {
                CurrentDtcs = all;
                foreach (var d in fresh)
                    Notices.Post("dtc:" + d.Code + d.Kind, d.Kind == DtcKind.Pending ? Severity.Warning : Severity.Alarm,
                        "Fault " + d.Code + (d.Kind == DtcKind.Pending ? " (pending)" : "") + (string.IsNullOrEmpty(d.Ecu) ? "" : " – " + d.Ecu),
                        d.Description);
                if (all.Count == 0) Ui.Run(() => { foreach (var n in Notices.Items.Where(x => x.Key.StartsWith("dtc:")).ToList()) Notices.Items.Remove(n); });
            };
            Obd = poller;
            poller.Start();
        }

        public static void UpdateWantedValues(IEnumerable<string> keys)
        {
            if (Obd == null) return;
            var all = AllValues.ToList();
            Obd.SetWanted(keys.Select(k => all.FirstOrDefault(v => string.Equals(v.Key, k, StringComparison.OrdinalIgnoreCase))).Where(v => v != null));
        }
    }
}
