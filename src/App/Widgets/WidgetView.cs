using System;
using System.Collections.Generic;
using System.Globalization;
using DriveDeck.App.Services;
using DriveDeck.Core;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace DriveDeck.App.Widgets
{
    public enum AlertLevel { Normal, Warn, Alarm }

    /// <summary>One dashboard tile, built in code so every layout is just data.</summary>
    public sealed class WidgetView : Grid
    {
        public static readonly Color WarnColor = Color.FromArgb(255, 255, 185, 0);
        public static readonly Color AlarmColor = Color.FromArgb(255, 232, 17, 35);
        public static readonly Color TileColor = Color.FromArgb(255, 22, 28, 36);
        public static readonly Color DimText = Color.FromArgb(255, 140, 153, 168);
        private const int GraphPoints = 120;

        public WidgetConfig Config { get; private set; }
        public ValueDefinition Definition { get; private set; }
        public AlertLevel Alert { get; private set; }
        public double LastValue { get; private set; }
        public Grid CameraHost { get; private set; }

        private readonly Color _accent;
        private TextBlock _label, _value, _unit, _rec;
        private static readonly Color LineColor = Color.FromArgb(255, 38, 48, 60);
        private static readonly Color TrackColor = Color.FromArgb(255, 36, 45, 56);
        private readonly Border _frame;
        private readonly SolidColorBrush _normalFill;
        private readonly SolidColorBrush _warnFill = new SolidColorBrush(WarnColor), _alarmFill = new SolidColorBrush(AlarmColor), _white = new SolidColorBrush(Colors.White);
        private Polygon _area;
        private StackPanel _status;
        private AlertLevel _shownAlert = AlertLevel.Normal;
        private Path _arcBg, _arcVal;
        private Rectangle _barFill;
        private Grid _barTrack;
        private Polyline _line;
        private readonly List<double> _history = new List<double>();
        private double _min, _max;

        public WidgetView(WidgetConfig cfg, ValueDefinition def, Color accent)
        {
            Config = cfg;
            Definition = def;
            _accent = string.IsNullOrEmpty(cfg.Color) ? accent : Parse(cfg.Color, accent);
            LastValue = double.NaN;
            _min = !double.IsNaN(cfg.Min) ? cfg.Min : def != null ? def.Min : 0;
            _max = !double.IsNaN(cfg.Max) ? cfg.Max : def != null ? def.Max : 100;
            if (_max <= _min) _max = _min + 1;

            _normalFill = new SolidColorBrush(_accent);
            Margin = new Thickness(3);
            _frame = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
            Children.Add(_frame);
            Paint(AlertLevel.Normal);

            switch (cfg.Kind)
            {
                case WidgetKind.Camera: BuildCamera(); break;
                case WidgetKind.Gauge: BuildGauge(); break;
                case WidgetKind.Bar: BuildBar(); break;
                case WidgetKind.Graph: BuildGraph(); break;
                case WidgetKind.Clock: BuildText("Time", ""); break;
                case WidgetKind.Status: BuildStatus(); break;
                default: BuildNumber(); break;
            }
        }

        /// <summary>The whole card takes the alert colour, so a warning is seen without reading the number.</summary>
        private void Paint(AlertLevel a)
        {
            _shownAlert = a;
            if (a == AlertLevel.Normal)
            {
                _frame.Background = new SolidColorBrush(TileColor);
                _frame.BorderBrush = new SolidColorBrush(LineColor);
                return;
            }
            var c = a == AlertLevel.Alarm ? AlarmColor : WarnColor;
            _frame.BorderBrush = new SolidColorBrush(c);
            _frame.Background = new SolidColorBrush(Color.FromArgb(255, (byte)(TileColor.R + (c.R - TileColor.R) / 5), (byte)(TileColor.G + (c.G - TileColor.G) / 5), (byte)(TileColor.B + (c.B - TileColor.B) / 5)));
        }

        public static Color Parse(string hex, Color fallback)
        {
            try
            {
                if (!AppSettings.IsColor(hex)) return fallback;
                string h = hex.Substring(1);
                if (h.Length == 6) h = "FF" + h;
                uint v = uint.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
            }
            catch { return fallback; }
        }

        private string LabelText
        {
            get
            {
                if (!string.IsNullOrEmpty(Config.Label)) return Config.Label;
                return Definition != null ? Definition.ShortName : Config.Kind.ToString();
            }
        }

        private TextBlock MakeLabel()
        {
            return new TextBlock
            {
                Text = LabelText, FontSize = 13, Foreground = new SolidColorBrush(DimText), Margin = new Thickness(12, 7, 10, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        private void BuildNumber()
        {
            _label = MakeLabel();
            _value = new TextBlock { Text = "--", FontSize = 40, FontWeight = FontWeights.SemiLight, Foreground = _white };
            _unit = new TextBlock
            {
                Text = Definition != null ? Definition.Unit : "", FontSize = 13, Foreground = new SolidColorBrush(DimText),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 12, 6)
            };
            Children.Add(new Viewbox { Child = _value, Margin = new Thickness(12, 24, 12, 20), StretchDirection = StretchDirection.Both });
            Children.Add(_label); Children.Add(_unit);
        }

        private void BuildText(string label, string value)
        {
            Config.Label = string.IsNullOrEmpty(Config.Label) ? label : Config.Label;
            BuildNumber();
            _unit.Text = value;
        }

        private void BuildGauge()
        {
            _arcBg = new Path { Stroke = new SolidColorBrush(TrackColor), StrokeThickness = 10, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _arcVal = new Path { Stroke = _normalFill, StrokeThickness = 10, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            Children.Add(_arcBg); Children.Add(_arcVal);
            _label = MakeLabel();
            _value = new TextBlock { Text = "--", FontWeight = FontWeights.SemiLight, Foreground = _white };
            var vb = new Viewbox { Child = _value, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Children.Add(vb); Children.Add(_label);
            _unit = new TextBlock { Text = Definition != null ? Definition.Unit : "", FontSize = 13, Foreground = new SolidColorBrush(DimText), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) };
            Children.Add(_unit);
            SizeChanged += (s, e) =>
            {
                double r = Math.Min(e.NewSize.Width, e.NewSize.Height) / 2 - 14;
                if (r < 10) return;
                vb.Width = r * 1.1; vb.Height = r * 0.55;
                _arcBg.Data = Arc(e.NewSize, r, 1.0);
                UpdateGaugeArc();
            };
        }

        private Geometry Arc(Size size, double r, double ratio)
        {
            ratio = Math.Max(0.001, Math.Min(1, ratio));
            double cx = size.Width / 2, cy = size.Height / 2 + r * 0.1;
            double a0 = 150 * Math.PI / 180, sweep = 240 * ratio * Math.PI / 180;
            var start = new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0));
            var end = new Point(cx + r * Math.Cos(a0 + sweep), cy + r * Math.Sin(a0 + sweep));
            var fig = new PathFigure { StartPoint = start, IsClosed = false };
            fig.Segments.Add(new ArcSegment { Point = end, Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise, IsLargeArc = 240 * ratio > 180 });
            var g = new PathGeometry();
            g.Figures.Add(fig);
            return g;
        }

        private void UpdateGaugeArc()
        {
            if (_arcVal == null || ActualWidth <= 0) return;
            double r = Math.Min(ActualWidth, ActualHeight) / 2 - 14;
            if (r < 10) return;
            double ratio = double.IsNaN(LastValue) ? 0 : (LastValue - _min) / (_max - _min);
            _arcVal.Data = Arc(new Size(ActualWidth, ActualHeight), r, ratio);
            _arcVal.Opacity = double.IsNaN(LastValue) ? 0 : 1;
        }

        private void BuildBar()
        {
            BuildNumber();
            _barTrack = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 10), Background = new SolidColorBrush(TrackColor) };
            _barFill = new Rectangle { Fill = _normalFill, RadiusX = 3, RadiusY = 3, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _unit.Margin = new Thickness(0, 0, 12, 18);
            _barTrack.Children.Add(_barFill);
            Children.Add(_barTrack);
        }

        private void BuildGraph()
        {
            _label = MakeLabel();
            var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(110, _accent.R, _accent.G, _accent.B), Offset = 0 });
            fade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, _accent.R, _accent.G, _accent.B), Offset = 1 });
            _area = new Polygon { Fill = fade, Margin = new Thickness(8, 32, 8, 8) };
            _line = new Polyline { Stroke = _normalFill, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round, Margin = new Thickness(8, 32, 8, 8) };
            Children.Add(_area);
            _value = new TextBlock { Text = "--", FontSize = 22, FontWeight = FontWeights.SemiLight, Foreground = _white, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 12, 0) };
            Children.Add(_line); Children.Add(_label); Children.Add(_value);
        }

        private void BuildCamera()
        {
            CameraHost = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
            CameraHost.Tapped += async (s, e) => await AppHub.Dashcam.FocusAsync();
            Children.Add(CameraHost);
            _rec = new TextBlock
            {
                Text = "", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(AlarmColor),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
            };
            Children.Add(_rec);
        }

        private void BuildStatus()
        {
            _label = MakeLabel();
            _label.Text = "Status";
            _status = new StackPanel { Margin = new Thickness(12, 26, 8, 6) };
            for (int i = 0; i < 5; i++)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
                row.Children.Add(new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { FontSize = 13, Foreground = _white, TextTrimming = TextTrimming.CharacterEllipsis });
                _status.Children.Add(row);
            }
            Children.Add(_status); Children.Add(_label);
        }

        private void StatusRow(int i, string text, Color dot)
        {
            var row = (StackPanel)_status.Children[i];
            row.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            var e = (Ellipse)row.Children[0];
            var b = e.Fill as SolidColorBrush;
            if (b == null) e.Fill = new SolidColorBrush(dot); else b.Color = dot;
            ((TextBlock)row.Children[1]).Text = text ?? "";
        }

        // ---------------- Refresh ----------------

        public void Refresh(DateTime now)
        {
            switch (Config.Kind)
            {
                case WidgetKind.Camera:
                    {
                        _rec.Text = AppHub.Dashcam.Capture == null ? (AppHub.Dashcam.IsOverheated ? "HOT – paused" : "Camera off") : "";
                        return;
                    }
                case WidgetKind.Clock:
                    _value.Text = now.ToString("HH:mm");
                    _unit.Text = now.ToString("ddd d.M.");
                    return;
                case WidgetKind.Status:
                    ShowStatus();
                    return;
            }

            if (Definition == null) return;
            var sample = AppHub.Values.Get(Definition.Key);
            bool unsupported = AppHub.Values.IsUnsupported(Definition.Key);
            bool stale = sample == null || (now - sample.Time).TotalSeconds > ValueHub.StaleSeconds;
            double v = sample != null && !stale ? sample.Value : double.NaN;
            LastValue = v;

            string text = unsupported ? "n/a" : double.IsNaN(v) ? "--" : v.ToString("F" + Definition.Decimals, CultureInfo.CurrentCulture);
            Alert = Evaluate(v);
            if (_shownAlert != Alert) Paint(Alert);
            if (_value != null) { _value.Text = text; _value.Foreground = Alert == AlertLevel.Alarm ? _alarmFill : Alert == AlertLevel.Warn ? _warnFill : _white; }
            Opacity = stale && !unsupported ? 0.55 : 1;

            var fill = Alert == AlertLevel.Alarm ? _alarmFill : Alert == AlertLevel.Warn ? _warnFill : _normalFill;
            if (_arcVal != null) { _arcVal.Stroke = fill; UpdateGaugeArc(); }
            if (_barFill != null)
            {
                double ratio = double.IsNaN(v) ? 0 : Math.Max(0, Math.Min(1, (v - _min) / (_max - _min)));
                _barFill.Width = Math.Max(0, _barTrack.ActualWidth * ratio);
                _barFill.Fill = fill;
            }
            if (_line != null)
            {
                _history.Add(v);
                if (_history.Count > GraphPoints) _history.RemoveAt(0);
                double w = Math.Max(1, ActualWidth - 16), h = Math.Max(1, ActualHeight - 42);
                var pts = new PointCollection();
                for (int i = 0; i < _history.Count; i++)
                {
                    double hv = _history[i];
                    if (double.IsNaN(hv)) continue;
                    double ratio = Math.Max(0, Math.Min(1, (hv - _min) / (_max - _min)));
                    pts.Add(new Point(w * i / (GraphPoints - 1), h * (1 - ratio)));
                }
                _line.Points = pts;
                if (pts.Count > 1)
                {
                    var area = new PointCollection { new Point(pts[0].X, h) };
                    foreach (var pt in pts) area.Add(pt);
                    area.Add(new Point(pts[pts.Count - 1].X, h));
                    _area.Points = area;
                }
                _line.Stroke = fill;
            }
        }

        private AlertLevel Evaluate(double v)
        {
            if (double.IsNaN(v)) return AlertLevel.Normal;
            if (!double.IsNaN(Config.AlarmAbove) && v >= Config.AlarmAbove) return AlertLevel.Alarm;
            if (!double.IsNaN(Config.WarnAbove) && v >= Config.WarnAbove) return AlertLevel.Warn;
            if (!double.IsNaN(Config.WarnBelow) && v <= Config.WarnBelow) return AlertLevel.Warn;
            return AlertLevel.Normal;
        }

        private void ShowStatus()
        {
            var grey = Color.FromArgb(255, 95, 107, 121);
            var green = Color.FromArgb(255, 76, 217, 100);
            var d = AppHub.Dashcam;
            StatusRow(0, d.SegmentStart.HasValue ? "Recording" : d.IsOverheated ? "Paused (hot)" : "Not recording",
                d.SegmentStart.HasValue ? AlarmColor : d.IsOverheated ? WarnColor : grey);
            bool obd = AppHub.Obd != null && AppHub.Obd.State == ObdState.Connected;
            StatusRow(1, AppHub.ObdStatus == null || AppHub.ObdStatus.StartsWith("OBD") ? (AppHub.ObdStatus ?? "OBD off") : "OBD: " + AppHub.ObdStatus, obd ? green : grey);
            var fix = AppHub.Location.Last;
            StatusRow(2, "GPS: " + (fix != null ? (fix.Accuracy.HasValue ? "±" + Math.Round(fix.Accuracy.Value) + " m" : "fix") : (AppHub.Location.State ?? "off")), fix != null ? green : grey);
            StatusRow(3, "Loop: " + (d.LoopBytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB", _accent);
            int faults = AppHub.CurrentDtcs != null ? AppHub.CurrentDtcs.Count : 0;
            StatusRow(4, faults > 0 ? "Faults: " + faults : null, AlarmColor);
        }
    }
}
