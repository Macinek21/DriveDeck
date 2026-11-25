using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using DriveDeck.App.Services;
using DriveDeck.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace DriveDeck.App
{
    public sealed partial class SettingsPage : Page
    {
        private AppSettings _s;
        private string _origDashcam, _origObd, _origSensors;
        private double _capacityGb = 64;
        private TextBlock _gMeter;
        private double _gPeak;
        private readonly DispatcherTimer _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private ValueDefinition _editing;
        private Panel _into;
        private int _section;
        private static readonly string[] Sections = { "Video", "Storage", "GPS & stamp", "Impact", "Car (OBD)", "Own values", "Display", "Backup" };
        private static readonly string[] Glyphs = { "", "", "", "", "", "", "", "" };

        public SettingsPage()
        {
            InitializeComponent();
            SizeChanged += (s, e) => PlaceNav(e.NewSize.Width >= e.NewSize.Height);
            _meterTimer.Tick += (s, e) =>
            {
                double g = AppHub.Impact.CurrentG;
                if (g > _gPeak) _gPeak = g;
                if (_gMeter != null) _gMeter.Text = string.Format(CultureInfo.InvariantCulture, "Live: {0:0.00} g   Peak: {1:0.00} g", g, _gPeak);
            };
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            List<string> n; string err;
            if (e.NavigationMode == NavigationMode.Back && _s != null)
            {
                // Returning from the layout editor: keep unsaved edits here, take the layout that was saved there.
                var fresh = AppSettings.FromJson(AppHub.Settings.ToJson(), out n, out err);
                if (fresh != null) _s.Pages = fresh.Pages;
                _meterTimer.Start();
                return;
            }
            _editing = null;
            _s = AppSettings.FromJson(AppHub.Settings.ToJson(), out n, out err) ?? AppSettings.Defaults();
            _origDashcam = Json(_s, x => x.Dashcam);
            _origObd = Json(_s, x => x.Obd);
            _origSensors = SensorsKey(_s.Dashcam);
            try
            {
                var props = await ApplicationData.Current.LocalFolder.Properties.RetrievePropertiesAsync(new[] { "System.Capacity" });
                object v;
                if (props.TryGetValue("System.Capacity", out v) && v is ulong) _capacityGb = Math.Max(2, Math.Floor((ulong)v / 1073741824.0));
            }
            catch { }
            Build();
            _meterTimer.Start();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _meterTimer.Stop();
            base.OnNavigatedFrom(e);
        }

        private static string Json<T>(AppSettings s, Func<AppSettings, T> part)
        {
            var copy = new AppSettings();
            if (typeof(T) == typeof(DashcamSettings)) copy.Dashcam = s.Dashcam;
            if (typeof(T) == typeof(ObdSettings)) copy.Obd = s.Obd;
            return copy.ToJson();
        }

        private static string SensorsKey(DashcamSettings d)
        {
            return d.GpsEnabled + "|" + d.ImpactDetection + "|" + d.ImpactSensitivity + "|" + d.ImpactThresholdG.ToString(CultureInfo.InvariantCulture);
        }

        // ---------------- Builder helpers ----------------

        private void PlaceNav(bool landscape)
        {
            Nav.Orientation = landscape ? Orientation.Vertical : Orientation.Horizontal;
            Nav.Margin = landscape ? new Thickness(6) : new Thickness(6, 0, 6, 6);
            Grid.SetRow(NavScroll, landscape ? 2 : 1);
            Grid.SetColumnSpan(NavScroll, landscape ? 1 : 2);
            Grid.SetColumn(BodyScroll, landscape ? 1 : 0);
            Grid.SetColumnSpan(BodyScroll, landscape ? 1 : 2);
            NavScroll.HorizontalScrollMode = landscape ? ScrollMode.Disabled : ScrollMode.Auto;
            NavScroll.VerticalScrollMode = landscape ? ScrollMode.Auto : ScrollMode.Disabled;
        }

        private void BuildNav()
        {
            Nav.Children.Clear();
            for (int i = 0; i < Sections.Length; i++)
            {
                int index = i;
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock { Text = Glyphs[i], FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Margin = new Thickness(0, 2, 9, 0), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = Sections[i], FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
                var b = new Button
                {
                    Content = row, Style = (Style)Application.Current.Resources[i == _section ? "AccentButton" : "SoftButton"], Margin = new Thickness(0, 0, 4, 4),
                    Padding = new Thickness(12, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left
                };
                if (i != _section) b.Background = new SolidColorBrush(Colors.Transparent);
                b.Click += (s, e) => { _section = index; _editing = null; Build(); BodyScroll.ChangeView(null, 0, null, true); };
                Nav.Children.Add(b);
            }
        }

        private static Style Soft { get { return (Style)Application.Current.Resources["SoftButton"]; } }

        /// <summary>Starts a new card; what follows is put into it.</summary>
        private void Header(string text)
        {
            var panel = new StackPanel();
            Body.Children.Add(new Border { Background = (Brush)Application.Current.Resources["CardBrush"], CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 10), Child = panel });
            panel.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
            _into = panel;
        }

        private void Note(string text)
        {
            _into.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["DimBrush"], Margin = new Thickness(0, 2, 0, 8) });
        }

        /// <summary>Text on the left, switch on the right.</summary>
        private ToggleSwitch Toggle(string title, bool value, Action<bool> set, bool rebuild = false)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2), MinHeight = 44 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = title.Trim(), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(title.StartsWith(" ") ? 14 : 0, 0, 10, 0) });
            var t = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            t.Toggled += (s, e) => { set(t.IsOn); if (rebuild) Build(); };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            _into.Children.Add(g);
            return t;
        }

        private ComboBox Choice(string title, string[] labels, string[] values, string current, Action<string> set, bool rebuild = false)
        {
            var c = new ComboBox { Header = title, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 8) };
            foreach (var l in labels) c.Items.Add(l);
            c.SelectedIndex = Math.Max(0, Array.IndexOf(values, current));
            c.SelectionChanged += (s, e) => { if (c.SelectedIndex >= 0 && c.SelectedIndex < values.Length) { set(values[c.SelectedIndex]); if (rebuild) Build(); } };
            _into.Children.Add(c);
            return c;
        }

        private Slider SliderRow(string title, double min, double max, double step, double value, Func<double, string> fmt, Action<double> set, bool enabled = true)
        {
            var label = new TextBlock { Text = title + ": " + fmt(value), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            var sl = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = Math.Max(min, Math.Min(max, value)), IsEnabled = enabled, Margin = new Thickness(0, 0, 0, 4) };
            sl.ValueChanged += (s, e) => { set(e.NewValue); label.Text = title + ": " + fmt(e.NewValue); };
            _into.Children.Add(label);
            _into.Children.Add(sl);
            return sl;
        }

        private TextBox TextRow(string title, string value, int maxLength, Action<string> set, string placeholder = "")
        {
            var t = new TextBox { Header = title, Text = value ?? "", MaxLength = maxLength, PlaceholderText = placeholder, Margin = new Thickness(0, 4, 0, 8) };
            t.TextChanged += (s, e) => set(t.Text);
            _into.Children.Add(t);
            return t;
        }

        private Button ButtonRow(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, Style = Soft, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 4) };
            b.Click += click;
            _into.Children.Add(b);
            return b;
        }

        // ---------------- Content ----------------

        private void Build()
        {
            Body.Children.Clear();
            _gMeter = null;
            BuildNav();
            switch (_section)
            {
                case 0: VideoSection(_s.Dashcam); break;
                case 1: StorageSection(_s.Dashcam); break;
                case 2: GpsSection(_s.Dashcam); break;
                case 3: ImpactSection(_s.Dashcam); break;
                case 4: ObdSection(_s.Obd); break;
                case 5: OwnValuesSection(_s.Obd); break;
                case 6: DisplaySection(_s.Ui); break;
                default: BackupSection(); break;
            }
        }

        private void VideoSection(DashcamSettings d)
        {

            Header("Dashcam");
            Toggle("Dashcam enabled", d.Enabled, v => d.Enabled = v);
            Toggle("Start recording when the app opens", d.AutoStart, v => d.AutoStart = v);
            Choice("Camera", new[] { "Back camera", "Front camera" }, new[] { "back", "front" }, d.UseFrontCamera ? "front" : "back", v => d.UseFrontCamera = v == "front", true);
            VideoOptions(d);
            Choice("Focus", new[] { "Automatic", "Far away (road)" }, new[] { "auto", "far" }, d.Focus, v => d.Focus = v);
            Note("Automatic keeps focusing. Choose Far away if it catches dirt or reflections on the windscreen. A tap on the camera picture focuses again.");
            Toggle("Slow down in the dark for a brighter picture", d.SlowInDark, v => d.SlowInDark = v);
            Toggle("Record sound (microphone)", d.RecordAudio, v => d.RecordAudio = v);
            Choice("Clip length", new[] { "30 s", "1 min", "2 min", "3 min", "5 min" }, new[] { "30", "60", "120", "180", "300" }, d.SegmentSeconds.ToString(), v => d.SegmentSeconds = int.Parse(v));
        }

        private void StorageSection(DashcamSettings d)
        {
            Header("Storage");
            Toggle("Unlimited (use all space except the reserve below)", d.UnlimitedStorage, v => d.UnlimitedStorage = v, true);
            SliderRow("Maximum space for loop recording", 1, _capacityGb, 1, d.StorageCapGb,
                v => v.ToString("0") + " GB  (≈ " + Hours(v, d) + " of driving)", v => d.StorageCapGb = v, !d.UnlimitedStorage);
            SliderRow("Always keep free on the phone", 0.5, 8, 0.5, d.ReserveFreeGb, v => v.ToString("0.0") + " GB", v => d.ReserveFreeGb = v);
            Note("When the limit is reached the oldest loop videos are overwritten. Saved incidents are never deleted automatically. Files are in Videos\\DriveDeck on the phone (also visible over USB).");
        }

        private void GpsSection(DashcamSettings d)
        {
            Header("GPS & speed in video");
            Toggle("Use GPS", d.GpsEnabled, v => d.GpsEnabled = v, true);
            Toggle("Burn stamp into the video", d.BurnInStamp, v => d.BurnInStamp = v, true);
            if (d.BurnInStamp)
            {
                Toggle("   Date and time", d.StampDateTime, v => d.StampDateTime = v);
                Toggle("   GPS speed", d.StampSpeed, v => d.StampSpeed = v);
                Toggle("   GPS coordinates", d.StampCoordinates, v => d.StampCoordinates = v);
                TextRow("   Extra label (e.g. licence plate)", d.StampLabel, StampFormatter.MaxLabelLength, v => d.StampLabel = v, "optional");
            }
            Toggle("Subtitle track with speed and position (.srt next to each video)", d.SubtitleTrack, v => d.SubtitleTrack = v);
            Toggle("GPS track file (.gpx next to each video)", d.GpxTrack, v => d.GpxTrack = v);
        }

        private void ImpactSection(DashcamSettings d)
        {
            Header("Impact detection");
            Toggle("Save footage automatically on impact", d.ImpactDetection, v => d.ImpactDetection = v, true);
            if (d.ImpactDetection)
            {
                Choice("Sensitivity", new[] { "High (1.3 g)", "Medium (2.0 g)", "Low (3.0 g)", "Custom" }, new[] { "high", "medium", "low", "custom" },
                       d.ImpactSensitivity, v => d.ImpactSensitivity = v, true);
                if (d.ImpactSensitivity == "custom")
                    SliderRow("Threshold", 0.8, 8, 0.1, d.ImpactThresholdG, v => v.ToString("0.0", CultureInfo.InvariantCulture) + " g", v => d.ImpactThresholdG = v);
                _gMeter = new TextBlock { FontSize = 13, Foreground = new SolidColorBrush(Colors.Gray), Margin = new Thickness(0, 0, 0, 6) };
                _into.Children.Add(_gMeter);
                Note("Potholes and speed bumps are usually below 1.2 g. Drive over a few with the phone mounted and watch the peak to pick a threshold.");
                SliderRow("Keep video before impact", 10, 180, 10, d.PreEventSeconds, v => v.ToString("0") + " s", v => d.PreEventSeconds = (int)v);
                SliderRow("Keep video after impact", 10, 120, 10, d.PostEventSeconds, v => v.ToString("0") + " s", v => d.PostEventSeconds = (int)v);
                Toggle("Offer an emergency call button after an impact", d.OfferEmergencyCall, v => d.OfferEmergencyCall = v);
                Choice("Emergency number", new[] { "112 (EU)", "155 (CZ ambulance)", "158 (CZ police)", "150 (CZ fire)", "911", "999" }, new[] { "112", "155", "158", "150", "911", "999" },
                       d.EmergencyNumber, v => d.EmergencyNumber = v);
            }
        }

        private void ObdSection(ObdSettings o)
        {
            Header("OBD-II (ELM327 Bluetooth)");
            Toggle("Read data from the car", o.Enabled, v => o.Enabled = v);
            var adapter = Choice("Adapter", new[] { string.IsNullOrEmpty(o.DeviceName) ? "(searching paired devices…)" : o.DeviceName }, new[] { o.DeviceId }, o.DeviceId, v => { });
            var _ = FillAdaptersAsync(adapter);
            Note("Pair the ELM327 first in the phone's Settings > Devices > Bluetooth (PIN 1234 or 0000). OBDeleven only works with its own app.");
            Choice("Protocol", new[] { "CAN 11-bit 500 kbit (SEAT Leon Mk4, fastest)", "Automatic", "CAN 29-bit 500 kbit", "CAN 11-bit 250 kbit", "CAN 29-bit 250 kbit" },
                   new[] { "6", "0", "7", "8", "9" }, o.Protocol, v => o.Protocol = v);
            Choice("Fuel (for consumption estimate)", new[] { "Petrol (TSI)", "Diesel (TDI)" }, new[] { "petrol", "diesel" }, o.FuelType, v => o.FuelType = v);
            SliderRow("Check fault codes every", 10, 300, 10, o.DtcIntervalSeconds, v => v.ToString("0") + " s", v => o.DtcIntervalSeconds = (int)v);
            Toggle("Also read permanent fault codes", o.ReadPermanentDtcs, v => o.ReadPermanentDtcs = v);
            Note("DriveDeck is read-only: it cannot clear codes, change coding or run tests. Requests that write to the car are blocked.");
        }

        private void OwnValuesSection(ObdSettings o)
        {
            Header("Custom values (advanced)");
            Note("Add manufacturer values (e.g. UDS service 22 DIDs). Only read services 01, 02, 03, 07, 09, 0A, 19, 21, 22 are accepted. Formula uses A–H for the reply bytes, e.g. (A*256+B)/10.");
            foreach (var cv in o.CustomValues.ToList())
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = cv.Name + "  (" + (string.IsNullOrEmpty(cv.Header) ? "" : cv.Header + ": ") + cv.Request + ")", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var edit = new Button { Content = "Edit", Style = Soft, Margin = new Thickness(6, 0, 0, 0) };
                var del = new Button { Content = "Delete", Style = Soft, Margin = new Thickness(6, 0, 0, 0) };
                var captured = cv;
                edit.Click += (s, e) => { _editing = captured; Build(); };
                del.Click += (s, e) => { o.CustomValues.Remove(captured); Build(); };
                Grid.SetColumn(edit, 1); Grid.SetColumn(del, 2);
                row.Children.Add(edit); row.Children.Add(del);
                _into.Children.Add(row);
            }
            if (_editing != null) BuildCustomEditor(o);
            else if (o.CustomValues.Count < AppSettings.MaxCustomValues)
                ButtonRow("Add custom value", (s, e) => { _editing = new ValueDefinition { Name = "", Request = "22 ", Header = "7E0", Formula = "A", Min = 0, Max = 100, Priority = 1 }; Build(); });
        }

        private void DisplaySection(UiSettings u)
        {
            Header("Display");
            Choice("Notification strip position", new[] { "Top", "Bottom" }, new[] { "top", "bottom" }, u.NotificationPosition, v => u.NotificationPosition = v);
            SliderRow("Collapse warnings to a small badge after", 0, 60, 2, u.NotificationCollapseSeconds,
                      v => v < 1 ? "never" : v.ToString("0") + " s", v => u.NotificationCollapseSeconds = (int)v);
            Toggle("Keep the screen on", u.KeepScreenOn, v => u.KeepScreenOn = v);
            Toggle("Dim the display (night)", u.NightMode, v => u.NightMode = v);
            Choice("Accent colour", new[] { "Blue", "Green", "Orange", "Red", "Purple", "White" },
                   new[] { "#FF3FA9F5", "#FF4CD964", "#FFFF9500", "#FFE81123", "#FFB4009E", "#FFFFFFFF" }, u.Accent, v => u.Accent = v);
            ButtonRow("Edit dashboard layout…", (s, e) => Frame.Navigate(typeof(LayoutEditorPage)));
        }

        private void BackupSection()
        {
            Header("Backup");
            ButtonRow("Export settings to a file…", async (s, e) => await ExportAsync());
            ButtonRow("Import settings from a file…", async (s, e) => await ImportAsync());
            ButtonRow("Reset everything to defaults", async (s, e) => await ResetAsync());

            Header("About");
            var ver = Windows.ApplicationModel.Package.Current.Id.Version;
            Note("DriveDeck " + ver.Major + "." + ver.Minor + "." + ver.Build + " for Windows 10 Mobile. Everything stays on this phone: the app has no internet permission and sends nothing anywhere. Location is only written into your own video sidecar files.");
        }

        private void VideoOptions(DashcamSettings d)
        {
            var known = CameraModes.For(d.UseFrontCamera);
            var modes = known.Usable();
            if (modes.Count == 0)
            {
                modes = VideoModes.Parse(d.UseFrontCamera ? "1280,720,30,0" : "1280,720,24,0;1280,720,30,0;1920,1080,24,0;1920,1080,30,0");
                Note("All modes of this camera appear here after it has been opened once.");
            }
            var now = VideoModes.Pick(modes, d.Resolution, d.FrameRate);
            Func<int> recommended = () => { var m = VideoModes.Pick(modes, d.Resolution, d.FrameRate); return VideoModes.RecommendedMbit(m.Width, m.Height, m.Fps); };
            var res = modes.GroupBy(m => m.Resolution).Select(g => g.First()).ToList();
            Choice("Resolution", res.Select(m => VideoModes.Label(m) + (m.Resolution == "720p" && res.Count > 1 ? " (small files, less detail)" : "")).ToArray(),
                res.Select(m => m.Resolution).ToArray(), now.Resolution,
                v => { d.Resolution = v; d.FrameRate = VideoModes.Pick(modes, v, d.FrameRate).Fps; d.BitrateKbps = recommended() * 1000; }, true);
            var rates = modes.Where(m => m.Resolution == now.Resolution).ToList();
            Choice("Frame rate", rates.Select(m => RateLabel(known, m)).ToArray(), rates.Select(m => m.Fps.ToString()).ToArray(), now.Fps.ToString(),
                v => { d.Resolution = now.Resolution; d.FrameRate = int.Parse(v); d.BitrateKbps = recommended() * 1000; }, true);
            if (now.Demanding) Note("Demanding mode: the phone gets warmer, and recording pauses when it overheats. DriveDeck measures the first clip; if the burned-in stamp makes it too slow, the stamp is left out and speed stays in the subtitle file.");
            int rec = recommended();
            SliderRow("Video bitrate", 2, VideoModes.MaxMbit, 1, d.BitrateKbps / 1000.0, v => v.ToString("0") + " Mbit/s (recommended " + rec + ")", v => d.BitrateKbps = (int)(v * 1000));
            if (known.Stamp(now) == StampState.Off)
            {
                Note("In this mode the burned-in stamp slowed recording down, so it is left out. Speed stays in the subtitle file.");
                ButtonRow("Use the stamp anyway", (s, e) => { known.SetStamp(now, StampState.Keep); MainPage.DashcamRestart = true; Build(); });
            }
            if (!d.UseFrontCamera) ButtonRow("Search for hidden camera modes…", async (s, e) => await SearchAsync());
            var refused = known.Refused();
            if (refused.Count > 0)
            {
                Note("Did not work and removed from the list: " + string.Join(", ", refused));
                ButtonRow("Offer these modes again", (s, e) => { known.ClearRefused(); Build(); });
            }
        }

        private async Task SearchAsync()
        {
            var ask = new ContentDialog
            {
                Title = "Search for hidden modes?", PrimaryButtonText = "Search", SecondaryButtonText = "Cancel",
                Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "DriveDeck asks the cameras for 4K, 2K, 60 fps and both cameras at once, and measures the stamp. It takes two to four minutes and nothing is recorded meanwhile, so do it while parked, in good light, with the app on screen.\n\nIf the camera misbehaves afterwards, restart the phone." }
            };
            if (await ask.ShowAsync() != ContentDialogResult.Primary || MainPage.Current == null) return;
            var text = new TextBlock { Text = "Starting…", TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { MinWidth = 240 };
            panel.Children.Add(text);
            panel.Children.Add(new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 12, 0, 0) });
            var dlg = new ContentDialog { Title = "Searching", Content = panel };
            bool done = false;
            dlg.Closing += (s, e) => { if (!done) e.Cancel = true; };
            var shown = dlg.ShowAsync();
            List<string> result;
            try { result = await MainPage.Current.RunLabAsync(m => Ui.Run(() => text.Text = m)); }
            finally { done = true; dlg.Hide(); }
            await Message("Search finished", "• " + string.Join("\n• ", result) + "\n\nDetails are in the log.");
            Build();
        }

        private static string RateLabel(CameraModes known, VideoMode m)
        {
            double real = known.RealFps(m);
            string text = m.Fps + " fps";
            if (real > 0 && real < m.Fps * 0.95) text += " (recorded at " + Math.Round(real) + " fps)";
            else if (real > 0 && m.Demanding) text += " (works)";
            else if (m.Demanding) text += " (not tried yet)";
            if (known.Stamp(m) == StampState.Off) text += ", no burned-in stamp";
            return text;
        }

        private static string Hours(double gb, DashcamSettings d)
        {
            double bytesPerHour = LoopStoragePolicy.EstimateSegmentBytes(d.BitrateKbps * 1000, d.RecordAudio, 3600);
            double h = gb * 1073741824.0 / bytesPerHour;
            return h < 1 ? (h * 60).ToString("0") + " min" : h.ToString("0.0") + " h";
        }

        private async Task FillAdaptersAsync(ComboBox box)
        {
            var o = _s.Obd;
            try
            {
                var devices = await BluetoothElmTransport.ListPairedAdaptersAsync();
                var ids = devices.Select(x => x.Id).ToList();
                var names = devices.Select(x => x.Name).ToList();
                if (!string.IsNullOrEmpty(o.DeviceId) && !ids.Contains(o.DeviceId)) { ids.Add(o.DeviceId); names.Add(o.DeviceName + " (not paired)"); }
                ids.Insert(0, ""); names.Insert(0, devices.Count == 0 ? "(no paired Bluetooth serial devices)" : "(none)");
                box.Items.Clear();
                foreach (var n in names) box.Items.Add(n);
                box.SelectedIndex = Math.Max(0, ids.IndexOf(o.DeviceId));
                box.SelectionChanged += (s, e) =>
                {
                    if (box.SelectedIndex < 0 || box.SelectedIndex >= ids.Count) return;
                    o.DeviceId = ids[box.SelectedIndex];
                    o.DeviceName = o.DeviceId.Length == 0 ? "" : names[box.SelectedIndex].Replace(" (not paired)", "");
                };
            }
            catch (Exception ex)
            {
                box.Items.Clear(); box.Items.Add("Bluetooth unavailable: " + ex.Message); box.SelectedIndex = 0;
            }
        }

        private void BuildCustomEditor(ObdSettings o)
        {
            var e = _editing;
            var draft = e.Clone();
            var panel = new StackPanel();
            Body.Children.Add(new Border { Background = (Brush)Application.Current.Resources["CardBrush"], CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 10), Child = panel });
            var hold = _into;
            _into = panel;
            TextRow("Name", draft.Name, 24, v => draft.Name = v, "e.g. Oil temperature");
            TextRow("Unit", draft.Unit, 8, v => draft.Unit = v, "°C");
            TextRow("ECU header (3 hex digits, empty = broadcast)", draft.Header, 3, v => draft.Header = v, "7E0 = engine");
            TextRow("Request (hex)", draft.Request, 20, v => draft.Request = v, "22 11 BD");
            TextRow("Formula", draft.Formula, Formula.MaxLength, v => draft.Formula = v, "(A*256+B)/10");
            TextRow("Gauge minimum", draft.Min.ToString(CultureInfo.InvariantCulture), 10, v => { double x; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out x)) draft.Min = x; });
            TextRow("Gauge maximum", draft.Max.ToString(CultureInfo.InvariantCulture), 10, v => { double x; if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out x)) draft.Max = x; });
            Choice("Decimals", new[] { "0", "1", "2", "3" }, new[] { "0", "1", "2", "3" }, draft.Decimals.ToString(), v => draft.Decimals = int.Parse(v));
            Choice("Update rate", new[] { "Fast (every cycle)", "Medium (1 s)", "Slow (5 s)" }, new[] { "0", "1", "2" }, draft.Priority.ToString(), v => draft.Priority = int.Parse(v));
            var error = new TextBlock { Foreground = new SolidColorBrush(Colors.OrangeRed), TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            var ok = new Button { Content = "OK", Style = (Style)Application.Current.Resources["AccentButton"], Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Style = Soft };
            ok.Click += (s, a) =>
            {
                string err;
                if (!AppSettings.ValidateCustom(draft, out err)) { error.Text = err; return; }
                int idx = o.CustomValues.IndexOf(e);
                if (idx >= 0) o.CustomValues[idx] = draft; else o.CustomValues.Add(draft);
                _editing = null;
                Build();
            };
            cancel.Click += (s, a) => { _editing = null; Build(); };
            buttons.Children.Add(ok); buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            _into = hold;
        }

        private async Task ExportAsync()
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "DriveDeck-settings" };
            picker.FileTypeChoices.Add("DriveDeck settings", new List<string> { ".json" });
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            _s.Normalize();
            await FileIO.WriteTextAsync(file, _s.ToJson());
            await Message("Exported", "Settings saved to " + file.Name + ".");
        }

        private async Task ImportAsync()
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            var props = await file.GetBasicPropertiesAsync();
            if (props.Size > AppSettings.MaxSettingsBytes) { await Message("Not imported", "The file is too large to be a DriveDeck settings file."); return; }
            List<string> notes; string err;
            var s = AppSettings.FromJson(await FileIO.ReadTextAsync(file), out notes, out err);
            if (s == null) { await Message("Not imported", err); return; }
            _s = s;
            Build();
            await Message("Imported", notes.Count == 0 ? "Press Save to apply." : "Some values were corrected:\n• " + string.Join("\n• ", notes.Take(8)) + "\n\nPress Save to apply.");
        }

        private async Task ResetAsync()
        {
            var dlg = new ContentDialog { Title = "Reset settings?", Content = "All settings and dashboard layouts return to defaults. Recordings are not touched.", PrimaryButtonText = "Reset", SecondaryButtonText = "Cancel" };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            _s = AppSettings.Defaults();
            Build();
        }

        private static async Task Message(string title, string text)
        {
            await new ContentDialog { Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = "OK" }.ShowAsync();
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            _s.Normalize();
            MainPage.DashcamDirty |= Json(_s, x => x.Dashcam) != _origDashcam;
            MainPage.ObdDirty = Json(_s, x => x.Obd) != _origObd;
            MainPage.SensorsDirty = SensorsKey(_s.Dashcam) != _origSensors;
            MainPage.LayoutDirty = true; // pages/accent may have changed; rebuilding is cheap
            AppHub.Settings = _s;
            try { await AppHub.SaveSettingsAsync(); }
            catch (Exception ex) { await Message("Could not save", ex.Message); return; }
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }
    }
}
