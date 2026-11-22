using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DriveDeck.App.Services;
using DriveDeck.App.Widgets;
using DriveDeck.Core;
using Windows.Graphics.Display;
using Windows.System;
using Windows.System.Display;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace DriveDeck.App
{
    public sealed partial class MainPage : Page
    {
        public static bool DashcamDirty, ObdDirty, LayoutDirty, SensorsDirty;
        public static MainPage Current { get; private set; }

        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        private readonly DispatcherTimer _collapseTimer = new DispatcherTimer();
        private readonly DispatcherTimer _impactTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        private readonly List<List<WidgetView>> _pageWidgets = new List<List<WidgetView>>();
        private readonly HashSet<string> _alarmed = new HashSet<string>();
        private DisplayRequest _displayRequest;
        private bool _initialized, _landscape, _wantRecording, _restarting;
        private DateTime _lastWatchdog = DateTime.MinValue;

        public MainPage()
        {
            InitializeComponent();
            Current = this;
            _timer.Tick += (s, e) => Tick();
            _collapseTimer.Tick += (s, e) => { _collapseTimer.Stop(); CollapseStrip(); };
            _impactTimer.Tick += (s, e) => { _impactTimer.Stop(); ImpactOverlay.Visibility = Visibility.Collapsed; };
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (!_initialized)
            {
                _initialized = true;
                await InitAsync();
                return;
            }
            ApplyUiSettings();
            if (LayoutDirty) { LayoutDirty = false; BuildPages(); }
            if (SensorsDirty) { SensorsDirty = false; await StartSensorsAsync(); }
            if (ObdDirty) { ObdDirty = false; await AppHub.RestartObdAsync(); UpdateWanted(); }
            if (DashcamDirty) { DashcamDirty = false; await StartDashcamAsync(); }
        }

        private async Task InitAsync()
        {
            try { ApplicationView.GetForCurrentView().TryEnterFullScreenMode(); } catch { }
            NoticeList.ItemsSource = AppHub.Notices.Items;
            AppHub.Notices.Posted += OnNoticePosted;
            AppHub.Notices.Items.CollectionChanged += (s, e) => UpdateChip();
            ApplyUiSettings();

            _landscape = Window.Current.Bounds.Width >= Window.Current.Bounds.Height;
            Window.Current.SizeChanged += (s, e) =>
            {
                bool l = e.Size.Width >= e.Size.Height;
                if (l != _landscape) { _landscape = l; BuildPages(); }
            };
            var di = DisplayInformation.GetForCurrentView();
            di.OrientationChanged += (s, e) => AppHub.Dashcam.UpdateRotation(s.CurrentOrientation);

            AppHub.Location.Updated += fix =>
            {
                var sp = AppHub.Location.SpeedKmh;
                if (sp.HasValue) AppHub.Values.Set(PidCatalog.GpsSpeedKey, sp.Value);
                if (fix.Altitude.HasValue) AppHub.Values.Set(PidCatalog.GpsAltitudeKey, fix.Altitude.Value);
            };
            AppHub.Impact.Impact += g => Ui.Run(() => OnImpact(g));

            AppHub.Dashcam.CurrentFix = () => AppHub.Location.Last;
            AppHub.Dashcam.CurrentSpeedKmh = () => AppHub.Location.Last != null ? AppHub.Location.SpeedKmh : null;
            AppHub.Dashcam.StatusChanged += (msg, err) =>
            {
                if (err) AppHub.Notices.Post("dashcam", Severity.Warning, "Dashcam", msg, TimeSpan.FromMinutes(3));
            };
            AppHub.Dashcam.EventSaved += (name, count) =>
                AppHub.Notices.Post("event:" + name, Severity.Info, "Incident saved",
                    count + " video file(s) in Videos\\DriveDeck\\Events\\" + name);

            BuildPages();
            _timer.Start();
            await StartSensorsAsync();
            await AppHub.RestartObdAsync();
            UpdateWanted();
            await StartDashcamAsync();
        }

        private void ApplyUiSettings()
        {
            var ui = AppHub.Settings.Ui;
            Grid.SetRow(Strip, ui.NotificationPosition == "bottom" ? 2 : 0);
            Chip.VerticalAlignment = ui.NotificationPosition == "bottom" ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            Chip.Margin = ui.NotificationPosition == "bottom" ? new Thickness(0, 0, 60, 12) : new Thickness(0, 6, 6, 0);
            NightDim.Opacity = ui.NightMode ? 0.35 : 0;
            if (ui.KeepScreenOn && _displayRequest == null) { _displayRequest = new DisplayRequest(); _displayRequest.RequestActive(); }
            else if (!ui.KeepScreenOn && _displayRequest != null) { _displayRequest.RequestRelease(); _displayRequest = null; }
        }

        public async Task StartSensorsAsync()
        {
            var c = AppHub.Settings.Dashcam;
            AppHub.Location.Stop();
            if (c.GpsEnabled) await AppHub.Location.StartAsync();
            AppHub.Impact.Stop();
            if (c.ImpactDetection)
            {
                double g = c.ImpactSensitivity == "custom" ? c.ImpactThresholdG : ImpactDetector.PresetThreshold(c.ImpactSensitivity);
                if (!AppHub.Impact.Start(g)) AppHub.Notices.Post("acc", Severity.Warning, "No accelerometer", "Impact detection is unavailable.");
            }
        }

        private async Task StartDashcamAsync()
        {
            if (_restarting) return;
            _restarting = true;
            try
            {
                Preview.Source = null;
                await AppHub.Dashcam.ShutdownAsync();
                var c = AppHub.Settings.Dashcam;
                _wantRecording = c.Enabled && c.AutoStart;
                if (!c.Enabled) return;
                if (!await AppHub.Dashcam.InitializeAsync(c)) return;
                Preview.Source = AppHub.Dashcam.Capture;
                try { await AppHub.Dashcam.Capture.StartPreviewAsync(); }
                catch (Exception ex) { AppLog.Error("StartPreview", ex); }
                if (_wantRecording) AppHub.Dashcam.Start();
            }
            finally { _restarting = false; }
        }

        public async Task SuspendAsync()
        {
            _timer.Stop();
            Preview.Source = null;
            await AppHub.Dashcam.ShutdownAsync();
            if (AppHub.Obd != null) await AppHub.Obd.StopAsync();
            AppHub.Location.Stop();
            AppHub.Impact.Stop();
        }

        public async Task ResumeAsync()
        {
            _timer.Start();
            await StartSensorsAsync();
            await AppHub.RestartObdAsync();
            UpdateWanted();
            await StartDashcamAsync();
        }

        // ---------------- Pages ----------------

        private void BuildPages()
        {
            Pages.Items.Clear();
            _pageWidgets.Clear();
            var accent = WidgetView.Parse(AppHub.Settings.Ui.Accent, Color.FromArgb(255, 63, 169, 245));
            var all = AppHub.AllValues.ToList();
            string orient = _landscape ? "landscape" : "portrait";
            var pages = AppHub.Settings.Pages.Where(p => p.Orientation == "any" || p.Orientation == orient).ToList();
            if (pages.Count == 0) pages = AppHub.Settings.Pages.Take(1).ToList();

            foreach (var p in pages)
            {
                var grid = new Grid { Margin = new Thickness(3) };
                for (int r = 0; r < p.Rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                for (int c = 0; c < p.Columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var widgets = new List<WidgetView>();
                foreach (var cfg in p.Widgets)
                {
                    var def = all.FirstOrDefault(v => string.Equals(v.Key, cfg.ValueKey, StringComparison.OrdinalIgnoreCase));
                    var w = new WidgetView(cfg, def, accent);
                    Grid.SetRow(w, cfg.Row); Grid.SetColumn(w, cfg.Column);
                    Grid.SetRowSpan(w, cfg.RowSpan); Grid.SetColumnSpan(w, cfg.ColumnSpan);
                    grid.Children.Add(w);
                    widgets.Add(w);
                }
                _pageWidgets.Add(widgets);
                Pages.Items.Add(new FlipViewItem { Content = grid });
            }
            Pages.SelectedIndex = 0;
            PositionPreview();
            UpdateWanted();
        }

        private List<WidgetView> CurrentWidgets
        {
            get
            {
                int i = Pages.SelectedIndex;
                return i >= 0 && i < _pageWidgets.Count ? _pageWidgets[i] : new List<WidgetView>();
            }
        }

        private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            PositionPreview();
            UpdateWanted();
        }

        /// <summary>
        /// The single live preview never moves between parents (re-parenting a running CaptureElement fails on the phone).
        /// It sits in a layer above the pages and is placed over the camera tile of the visible page, or shrunk to
        /// an invisible 1 px when the page has no camera tile, so the camera keeps running.
        /// </summary>
        private void PositionPreview()
        {
            var host = CurrentWidgets.Where(w => w.CameraHost != null).Select(w => w.CameraHost).FirstOrDefault();
            try
            {
                if (host == null || host.ActualWidth < 2 || host.ActualHeight < 2)
                {
                    PreviewLayer.Width = 1; PreviewLayer.Height = 1; PreviewLayer.Opacity = 0.01;
                    return;
                }
                var pt = host.TransformToVisual(Pages).TransformPoint(new Windows.Foundation.Point(0, 0));
                var margin = new Thickness(pt.X, pt.Y, 0, 0);
                if (PreviewLayer.Margin != margin) PreviewLayer.Margin = margin;
                if (PreviewLayer.Width != host.ActualWidth || PreviewLayer.Height != host.ActualHeight)
                {
                    PreviewLayer.Width = host.ActualWidth; PreviewLayer.Height = host.ActualHeight;
                    PreviewLayer.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, host.ActualWidth, host.ActualHeight) };
                }
                PreviewLayer.Opacity = 1;
            }
            catch (Exception ex) { AppLog.Error("PositionPreview", ex); }
        }

        /// <summary>Polls what is visible, plus anything with an alarm threshold on any page.</summary>
        private void UpdateWanted()
        {
            var keys = new HashSet<string>(CurrentWidgets.Where(w => w.Definition != null).Select(w => w.Definition.Key));
            foreach (var p in AppHub.Settings.Pages)
                foreach (var w in p.Widgets)
                    if (!string.IsNullOrEmpty(w.ValueKey) && (!double.IsNaN(w.AlarmAbove) || !double.IsNaN(w.WarnAbove) || !double.IsNaN(w.WarnBelow)))
                        keys.Add(w.ValueKey);
            AppHub.UpdateWantedValues(keys);
        }

        // ---------------- Refresh / alarms / watchdog ----------------

        private void Tick()
        {
            var now = DateTime.Now;
            foreach (var w in CurrentWidgets)
            {
                w.Refresh(now);
                if (w.Definition == null) continue;
                string key = "alarm:" + w.Config.Id;
                if (w.Alert == AlertLevel.Alarm && !_alarmed.Contains(key))
                {
                    _alarmed.Add(key);
                    AppHub.Notices.Post(key, Severity.Alarm, w.Definition.Name + " too high",
                        w.LastValue.ToString("F" + w.Definition.Decimals) + " " + w.Definition.Unit, TimeSpan.FromMinutes(2));
                }
                else if (w.Alert != AlertLevel.Alarm) _alarmed.Remove(key);
            }
            RecToggle.Text = AppHub.Dashcam.IsRecording ? "Stop recording" : "Start recording";
            PositionPreview();
            var st = AppHub.Dashcam.SegmentStart;
            RecBadge.Text = st.HasValue ? (now.Second % 2 == 0 ? "● REC " : "○ REC ") + (now - st.Value).ToString(@"m\:ss")
                          : AppHub.Dashcam.IsOverheated ? "HOT – paused" : "not recording";

            // Watchdog: resume after overheating or a camera error, if the user wants recording.
            if ((now - _lastWatchdog).TotalSeconds >= 15)
            {
                _lastWatchdog = now;
                if (_wantRecording && !AppHub.Dashcam.IsRecording && !AppHub.Dashcam.IsOverheated && !_restarting)
                {
                    if (AppHub.Dashcam.IsInitialized) AppHub.Dashcam.Start();
                    else { var _ = StartDashcamAsync(); }
                }
            }
        }

        // ---------------- Notifications ----------------

        private void OnNoticePosted(Notice n)
        {
            string glyph; Color border, bg;
            NoticeStyle(n.Severity, out glyph, out border, out bg);
            StripGlyph.Text = glyph;
            StripGlyph.Foreground = new SolidColorBrush(border);
            StripTitle.Text = n.Title ?? "";
            StripDetail.Text = n.Detail ?? "";
            Strip.Background = new SolidColorBrush(bg);
            Strip.BorderBrush = new SolidColorBrush(border);
            Strip.BorderThickness = AppHub.Settings.Ui.NotificationPosition == "bottom" ? new Thickness(0, 2, 0, 0) : new Thickness(0, 0, 0, 2);
            Strip.Visibility = Visibility.Visible;
            UpdateChip();
            int secs = n.Severity == Severity.Info ? 4 : AppHub.Settings.Ui.NotificationCollapseSeconds;
            _collapseTimer.Stop();
            if (secs > 0) { _collapseTimer.Interval = TimeSpan.FromSeconds(secs); _collapseTimer.Start(); }
        }

        private static void NoticeStyle(Severity s, out string glyph, out Color border, out Color bg)
        {
            switch (s)
            {
                case Severity.Alarm: glyph = ""; border = WidgetView.AlarmColor; bg = Color.FromArgb(255, 43, 20, 22); break;
                case Severity.Warning: glyph = ""; border = WidgetView.WarnColor; bg = Color.FromArgb(255, 40, 34, 16); break;
                default: glyph = ""; border = Color.FromArgb(255, 63, 169, 245); bg = Color.FromArgb(255, 20, 30, 40); break;
            }
        }

        private void CollapseStrip()
        {
            Strip.Visibility = Visibility.Collapsed;
            UpdateChip();
        }

        private void UpdateChip()
        {
            var items = AppHub.Notices.Items;
            bool show = items.Count > 0 && Strip.Visibility == Visibility.Collapsed && NoticePanel.Visibility == Visibility.Collapsed;
            Chip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            var worst = items.Max(n => n.Severity);
            string glyph; Color border, bg;
            NoticeStyle(worst, out glyph, out border, out bg);
            ChipGlyph.Text = glyph;
            ChipGlyph.Foreground = new SolidColorBrush(border);
            ChipCount.Text = items.Count.ToString();
            Chip.BorderBrush = new SolidColorBrush(border);
            Chip.BorderThickness = new Thickness(1);
        }

        private void Strip_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e) { CollapseStrip(); OpenNotices(); }
        private void Chip_Click(object sender, RoutedEventArgs e) { OpenNotices(); }
        private void OpenNotices() { NoticePanel.Visibility = Visibility.Visible; UpdateChip(); }
        private void CloseNotices_Click(object sender, RoutedEventArgs e) { NoticePanel.Visibility = Visibility.Collapsed; UpdateChip(); }
        private void ClearNotices_Click(object sender, RoutedEventArgs e)
        {
            // Active fault codes stay listed until the car reports them gone.
            foreach (var n in AppHub.Notices.Items.Where(x => !x.Key.StartsWith("dtc:")).ToList()) AppHub.Notices.Items.Remove(n);
            if (AppHub.Notices.Items.Count == 0) NoticePanel.Visibility = Visibility.Collapsed;
            UpdateChip();
        }

        // ---------------- Incidents ----------------

        private void OnImpact(double g)
        {
            AppHub.Dashcam.TriggerEvent("impact", g);
            var c = AppHub.Settings.Dashcam;
            ImpactDetail.Text = string.Format("{0:0.0} g at {1:HH:mm:ss}. Video from {2} s before to {3} s after is being saved to Videos\\DriveDeck\\Events.",
                g, DateTime.Now, c.PreEventSeconds, c.PostEventSeconds);
            CallButton.Visibility = c.OfferEmergencyCall ? Visibility.Visible : Visibility.Collapsed;
            CallText.Text = "Call " + c.EmergencyNumber;
            ImpactOverlay.Visibility = Visibility.Visible;
            _impactTimer.Stop(); _impactTimer.Start();
        }

        private async void Call_Click(object sender, RoutedEventArgs e)
        {
            // The phone app asks for confirmation before dialling; the number comes from a fixed list of emergency numbers.
            await Launcher.LaunchUriAsync(new Uri("tel:" + AppHub.Settings.Dashcam.EmergencyNumber));
        }

        private void DismissImpact_Click(object sender, RoutedEventArgs e) { ImpactOverlay.Visibility = Visibility.Collapsed; }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            AppHub.Dashcam.TriggerEvent("manual", AppHub.Impact.CurrentG);
            var c = AppHub.Settings.Dashcam;
            AppHub.Notices.Post("manual-save", Severity.Info, "Saving incident",
                "Keeping video from " + c.PreEventSeconds + " s before until " + c.PostEventSeconds + " s after now");
        }

        // ---------------- Menu ----------------

        private async void RecToggle_Click(object sender, RoutedEventArgs e)
        {
            if (AppHub.Dashcam.IsRecording) { _wantRecording = false; await AppHub.Dashcam.StopAsync(); }
            else
            {
                _wantRecording = true;
                if (AppHub.Dashcam.IsInitialized) AppHub.Dashcam.Start(); else await StartDashcamAsync();
            }
        }

        private async void Faults_Click(object sender, RoutedEventArgs e)
        {
            var list = AppHub.CurrentDtcs ?? new List<Dtc>();
            var panel = new StackPanel();
            if (AppHub.Obd == null || AppHub.Obd.State != ObdState.Connected)
                panel.Children.Add(new TextBlock { Text = "OBD adapter is not connected (" + (AppHub.ObdStatus ?? "off") + ").", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            if (list.Count == 0)
                panel.Children.Add(new TextBlock { Text = "No emission-related fault codes reported by the engine or gearbox.", TextWrapping = TextWrapping.Wrap });
            foreach (var d in list)
            {
                panel.Children.Add(new TextBlock { Text = d.Code + "  •  " + d.Kind + (string.IsNullOrEmpty(d.Ecu) ? "" : "  •  " + d.Ecu), FontWeight = Windows.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
                panel.Children.Add(new TextBlock { Text = d.Description, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.LightGray) });
            }
            panel.Children.Add(new TextBlock
            {
                Text = "DriveDeck only reads codes. Use OBDeleven to see all control units and to clear codes.",
                TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = new SolidColorBrush(Colors.Gray), Margin = new Thickness(0, 14, 0, 0)
            });
            var dlg = new ContentDialog { Title = "Fault codes", Content = new ScrollViewer { Content = panel, MaxHeight = 400 }, PrimaryButtonText = "Close" };
            await dlg.ShowAsync();
        }

        private void Recordings_Click(object sender, RoutedEventArgs e) { Frame.Navigate(typeof(RecordingsPage)); }
        private void Layout_Click(object sender, RoutedEventArgs e) { Frame.Navigate(typeof(LayoutEditorPage)); }
        private void Settings_Click(object sender, RoutedEventArgs e) { Frame.Navigate(typeof(SettingsPage)); }
    }
}
