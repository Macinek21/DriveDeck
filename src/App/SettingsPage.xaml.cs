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

        public SettingsPage()
        {
            InitializeComponent();
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

        private void Header(string text)
        {
            Body.Children.Add(new TextBlock { Text = text, FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(255, 63, 169, 245)), Margin = new Thickness(0, 22, 0, 6) });
        }

        private void Note(string text)
        {
            Body.Children.Add(new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Gray), Margin = new Thickness(0, 0, 0, 8) });
        }

        private ToggleSwitch Toggle(string title, bool value, Action<bool> set, bool rebuild = false)
        {
            var t = new ToggleSwitch { Header = title, IsOn = value, Margin = new Thickness(0, 2, 0, 2) };
            t.Toggled += (s, e) => { set(t.IsOn); if (rebuild) Build(); };
            Body.Children.Add(t);
            return t;
        }

        private ComboBox Choice(string title, string[] labels, string[] values, string current, Action<string> set, bool rebuild = false)
        {
            var c = new ComboBox { Header = title, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 8) };
            foreach (var l in labels) c.Items.Add(l);
            c.SelectedIndex = Math.Max(0, Array.IndexOf(values, current));
            c.SelectionChanged += (s, e) => { if (c.SelectedIndex >= 0 && c.SelectedIndex < values.Length) { set(values[c.SelectedIndex]); if (rebuild) Build(); } };
            Body.Children.Add(c);
            return c;
        }

        private Slider SliderRow(string title, double min, double max, double step, double value, Func<double, string> fmt, Action<double> set, bool enabled = true)
        {
            var label = new TextBlock { Text = title + ": " + fmt(value), Margin = new Thickness(0, 6, 0, 0) };
            var sl = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = Math.Max(min, Math.Min(max, value)), IsEnabled = enabled, Margin = new Thickness(0, 0, 0, 4) };
            sl.ValueChanged += (s, e) => { set(e.NewValue); label.Text = title + ": " + fmt(e.NewValue); };
            Body.Children.Add(label);
            Body.Children.Add(sl);
            return sl;
        }

        private TextBox TextRow(string title, string value, int maxLength, Action<string> set, string placeholder = "")
        {
            var t = new TextBox { Header = title, Text = value ?? "", MaxLength = maxLength, PlaceholderText = placeholder, Margin = new Thickness(0, 4, 0, 8) };
            t.TextChanged += (s, e) => set(t.Text);
            Body.Children.Add(t);
            return t;
        }

        private Button ButtonRow(string text, RoutedEventHandler click)
        {
            var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4, 0, 4) };
            b.Click += click;
            Body.Children.Add(b);
            return b;
        }

        // ---------------- Content ----------------

        private void Build()
        {
            Body.Children.Clear();
            var d = _s.Dashcam;
            var o = _s.Obd;
            var u = _s.Ui;

            Header("Dashcam");
            Toggle("Dashcam enabled", d.Enabled, v => d.Enabled = v);
            Toggle("Start recording when the app opens", d.AutoStart, v => d.AutoStart = v);
            Choice("Resolution", new[] { "720p (recommended – cooler phone, smaller files)", "1080p" }, new[] { "720p", "1080p" }, d.Resolution, v => d.Resolution = v);
            Choice("Frame rate", new[] { "30 fps", "24 fps" }, new[] { "30", "24" }, d.FrameRate.ToString(), v => d.FrameRate = int.Parse(v));
            SliderRow("Video bitrate", 2, 20, 1, d.BitrateKbps / 1000.0, v => v.ToString("0") + " Mbit/s", v => d.BitrateKbps = (int)(v * 1000));
            Toggle("Record sound (microphone)", d.RecordAudio, v => d.RecordAudio = v);
            Choice("Segment length", new[] { "30 s", "1 min", "2 min", "3 min", "5 min" }, new[] { "30", "60", "120", "180", "300" }, d.SegmentSeconds.ToString(), v => d.SegmentSeconds = int.Parse(v));

            Header("Storage");
            Toggle("Unlimited (use all space except the reserve below)", d.UnlimitedStorage, v => d.UnlimitedStorage = v, true);
            SliderRow("Maximum space for loop recording", 1, _capacityGb, 1, d.StorageCapGb,
                v => v.ToString("0") + " GB  (≈ " + Hours(v, d) + " of driving)", v => d.StorageCapGb = v, !d.UnlimitedStorage);
            SliderRow("Always keep free on the phone", 0.5, 8, 0.5, d.ReserveFreeGb, v => v.ToString("0.0") + " GB", v => d.ReserveFreeGb = v);
            Note("When the limit is reached the oldest loop videos are overwritten. Saved incidents are never deleted automatically. Files are in Videos\\DriveDeck on the phone (also visible over USB).");

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

            Header("Impact detection");
            Toggle("Save footage automatically on impact", d.ImpactDetection, v => d.ImpactDetection = v, true);
            if (d.ImpactDetection)
            {
                Choice("Sensitivity", new[] { "High (1.3 g)", "Medium (2.0 g)", "Low (3.0 g)", "Custom" }, new[] { "high", "medium", "low", "custom" },
                       d.ImpactSensitivity, v => d.ImpactSensitivity = v, true);
                if (d.ImpactSensitivity == "custom")
                    SliderRow("Threshold", 0.8, 8, 0.1, d.ImpactThresholdG, v => v.ToString("0.0", CultureInfo.InvariantCulture) + " g", v => d.ImpactThresholdG = v);
                _gMeter = new TextBlock { FontSize = 13, Foreground = new SolidColorBrush(Colors.Gray), Margin = new Thickness(0, 0, 0, 6) };
                Body.Children.Add(_gMeter);
                Note("Potholes and speed bumps are usually below 1.2 g. Drive over a few with the phone mounted and watch the peak to pick a threshold.");
                SliderRow("Keep video before impact", 10, 180, 10, d.PreEventSeconds, v => v.ToString("0") + " s", v => d.PreEventSeconds = (int)v);
                SliderRow("Keep video after impact", 10, 120, 10, d.PostEventSeconds, v => v.ToString("0") + " s", v => d.PostEventSeconds = (int)v);
                Toggle("Offer an emergency call button after an impact", d.OfferEmergencyCall, v => d.OfferEmergencyCall = v);
                Choice("Emergency number", new[] { "112 (EU)", "155 (CZ ambulance)", "158 (CZ police)", "150 (CZ fire)" }, new[] { "112", "155", "158", "150" },
                       d.EmergencyNumber, v => d.EmergencyNumber = v);
            }

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

            Header("Custom values (advanced)");
            Note("Add manufacturer values (e.g. UDS service 22 DIDs). Only read services 01, 02, 03, 07, 09, 0A, 19, 21, 22 are accepted. Formula uses A–H for the reply bytes, e.g. (A*256+B)/10.");
            foreach (var cv in o.CustomValues.ToList())
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = cv.Name + "  (" + (string.IsNullOrEmpty(cv.Header) ? "" : cv.Header + ": ") + cv.Request + ")", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var edit = new Button { Content = "Edit", Margin = new Thickness(6, 0, 0, 0) };
                var del = new Button { Content = "Delete", Margin = new Thickness(6, 0, 0, 0) };
                var captured = cv;
                edit.Click += (s, e) => { _editing = captured; Build(); };
                del.Click += (s, e) => { o.CustomValues.Remove(captured); Build(); };
                Grid.SetColumn(edit, 1); Grid.SetColumn(del, 2);
                row.Children.Add(edit); row.Children.Add(del);
                Body.Children.Add(row);
            }
            if (_editing != null) BuildCustomEditor(o);
            else if (o.CustomValues.Count < AppSettings.MaxCustomValues)
                ButtonRow("Add custom value", (s, e) => { _editing = new ValueDefinition { Name = "", Request = "22 ", Header = "7E0", Formula = "A", Min = 0, Max = 100, Priority = 1 }; Build(); });

            Header("Display");
            Choice("Notification strip position", new[] { "Top", "Bottom" }, new[] { "top", "bottom" }, u.NotificationPosition, v => u.NotificationPosition = v);
            SliderRow("Collapse warnings to a small badge after", 0, 60, 2, u.NotificationCollapseSeconds,
                      v => v < 1 ? "never" : v.ToString("0") + " s", v => u.NotificationCollapseSeconds = (int)v);
            Toggle("Keep the screen on", u.KeepScreenOn, v => u.KeepScreenOn = v);
            Toggle("Dim the display (night)", u.NightMode, v => u.NightMode = v);
            Choice("Accent colour", new[] { "Blue", "Green", "Orange", "Red", "Purple", "White" },
                   new[] { "#FF3FA9F5", "#FF4CD964", "#FFFF9500", "#FFE81123", "#FFB4009E", "#FFFFFFFF" }, u.Accent, v => u.Accent = v);
            ButtonRow("Edit dashboard layout…", (s, e) => Frame.Navigate(typeof(LayoutEditorPage)));

            Header("Backup");
            ButtonRow("Export settings to a file…", async (s, e) => await ExportAsync());
            ButtonRow("Import settings from a file…", async (s, e) => await ImportAsync());
            ButtonRow("Reset everything to defaults", async (s, e) => await ResetAsync());

            Header("About");
            Note("DriveDeck 1.0 for Windows 10 Mobile. Everything stays on this phone: the app has no internet permission and sends nothing anywhere. Location is only written into your own video sidecar files.");
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
                box.Items.Clear();
                if (ids.Count == 0) { box.Items.Add("No paired Bluetooth serial devices found"); box.SelectedIndex = 0; return; }
                foreach (var n in names) box.Items.Add(n);
                box.SelectedIndex = Math.Max(0, ids.IndexOf(o.DeviceId));
                if (box.SelectedIndex >= 0 && ids.IndexOf(o.DeviceId) < 0) { o.DeviceId = ids[0]; o.DeviceName = names[0]; }
                box.SelectionChanged += (s, e) =>
                {
                    if (box.SelectedIndex >= 0 && box.SelectedIndex < ids.Count) { o.DeviceId = ids[box.SelectedIndex]; o.DeviceName = names[box.SelectedIndex]; }
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
            Body.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(255, 20, 24, 30)), Padding = new Thickness(12), Margin = new Thickness(0, 6, 0, 6), Child = panel });
            var hold = Body;
            Body = panel;
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
            var ok = new Button { Content = "OK", Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel" };
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
            Body = hold;
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
            MainPage.DashcamDirty = Json(_s, x => x.Dashcam) != _origDashcam;
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
