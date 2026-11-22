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
        public static readonly Color TileColor = Color.FromArgb(255, 20, 24, 30);
        public static readonly Color DimText = Color.FromArgb(255, 150, 160, 170);
        private const int GraphPoints = 120;

        public WidgetConfig Config { get; private set; }
        public ValueDefinition Definition { get; private set; }
        public AlertLevel Alert { get; private set; }
        public double LastValue { get; private set; }
        public Grid CameraHost { get; private set; }

        private readonly Color _accent;
        private TextBlock _label, _value, _unit, _rec;
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

            Margin = new Thickness(3);
            var frame = new Border { Background = new SolidColorBrush(TileColor), CornerRadius = new CornerRadius(6) };
            Children.Add(frame);

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
                Text = LabelText, FontSize = 14, Foreground = new SolidColorBrush(DimText), Margin = new Thickness(8, 4, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        private void BuildNumber()
        {
            _label = MakeLabel();
            _value = new TextBlock { Text = "--", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) };
            var vb = new Viewbox { Child = _value, Margin = new Thickness(10, 20, 10, 14), StretchDirection = StretchDirection.Both };
            _unit = new TextBlock
            {
                Text = Definition != null ? Definition.Unit : "", FontSize = 13, Foreground = new SolidColorBrush(DimText),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 8, 3)
            };
            Children.Add(vb); Children.Add(_label); Children.Add(_unit);
        }

        private void BuildText(string label, string value)
        {
            Config.Label = string.IsNullOrEmpty(Config.Label) ? label : Config.Label;
            BuildNumber();
            _unit.Text = value;
        }

        private void BuildGauge()
        {
            _arcBg = new Path { Stroke = new SolidColorBrush(Color.FromArgb(255, 45, 52, 60)), StrokeThickness = 10, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _arcVal = new Path { Stroke = new SolidColorBrush(_accent), StrokeThickness = 10, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            Children.Add(_arcBg); Children.Add(_arcVal);
            _label = MakeLabel();
            _value = new TextBlock { Text = "--", FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) };
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
            _barTrack = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 8, 22), Background = new SolidColorBrush(Color.FromArgb(255, 45, 52, 60)) };
            _barFill = new Rectangle { Fill = new SolidColorBrush(_accent), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _barTrack.Children.Add(_barFill);
            Children.Add(_barTrack);
        }

        private void BuildGraph()
        {
            _label = MakeLabel();
            _line = new Polyline { Stroke = new SolidColorBrush(_accent), StrokeThickness = 2, Margin = new Thickness(8, 26, 8, 8) };
            _value = new TextBlock { Text = "--", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 8, 0) };
            Children.Add(_line); Children.Add(_label); Children.Add(_value);
        }

        private void BuildCamera()
        {
            CameraHost = new Grid { Margin = new Thickness(2) };
            Children.Add(CameraHost);
            _rec = new TextBlock
            {
                Text = "", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(AlarmColor),
                Margin = new Thickness(8, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
            };
            Children.Add(_rec);
        }

        private void BuildStatus()
        {
            _label = MakeLabel();
            _label.Text = "Status";
            _value = new TextBlock { FontSize = 13, Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 22, 6, 4), LineHeight = 17 };
            Children.Add(_value); Children.Add(_label);
        }

        // ---------------- Refresh ----------------

        public void Refresh(DateTime now)
        {
            switch (Config.Kind)
            {
                case WidgetKind.Camera:
                    {
                        var st = AppHub.Dashcam.SegmentStart;
                        _rec.Text = st.HasValue ? (now.Second % 2 == 0 ? "● REC " : "○ REC ") + (now - st.Value).ToString(@"m\:ss") : (AppHub.Dashcam.IsOverheated ? "HOT – paused" : "not recording");
                        return;
                    }
                case WidgetKind.Clock:
                    _value.Text = now.ToString("HH:mm");
                    _unit.Text = now.ToString("ddd d.M.");
                    return;
                case WidgetKind.Status:
                    _value.Text = StatusText(now);
                    return;
            }

            if (Definition == null) return;
            var sample = AppHub.Values.Get(Definition.Key);
            bool unsupported = AppHub.Values.IsUnsupported(Definition.Key);
            bool stale = sample == null || (now - sample.Time).TotalSeconds > 5;
            double v = sample != null && !stale ? sample.Value : double.NaN;
            LastValue = v;

            string text = unsupported ? "n/a" : double.IsNaN(v) ? "--" : v.ToString("F" + Definition.Decimals, CultureInfo.CurrentCulture);
            Alert = Evaluate(v);
            var color = Alert == AlertLevel.Alarm ? AlarmColor : Alert == AlertLevel.Warn ? WarnColor : Colors.White;
            if (_value != null) { _value.Text = text; _value.Foreground = new SolidColorBrush(color); }
            Opacity = stale && !unsupported ? 0.55 : 1;

            var fill = new SolidColorBrush(Alert == AlertLevel.Normal ? _accent : color);
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
                double w = Math.Max(1, ActualWidth - 16), h = Math.Max(1, ActualHeight - 34);
                var pts = new PointCollection();
                for (int i = 0; i < _history.Count; i++)
                {
                    double hv = _history[i];
                    if (double.IsNaN(hv)) continue;
                    double ratio = Math.Max(0, Math.Min(1, (hv - _min) / (_max - _min)));
                    pts.Add(new Point(w * i / (GraphPoints - 1), h * (1 - ratio)));
                }
                _line.Points = pts;
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

        private static string StatusText(DateTime now)
        {
            var d = AppHub.Dashcam;
            string rec = d.SegmentStart.HasValue ? "● Recording" : d.IsOverheated ? "Paused (hot)" : "Not recording";
            string obd = "OBD: " + (AppHub.ObdStatus ?? "off");
            var fix = AppHub.Location.Last;
            string gps = "GPS: " + (fix != null ? (fix.Accuracy.HasValue ? "±" + Math.Round(fix.Accuracy.Value) + " m" : "fix") : (AppHub.Location.State ?? "off"));
            string loop = "Loop: " + (d.LoopBytes / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB";
            int faults = AppHub.CurrentDtcs != null ? AppHub.CurrentDtcs.Count : 0;
            return rec + "\n" + obd + "\n" + gps + "\n" + loop + (faults > 0 ? "\nFaults: " + faults : "");
        }
    }
}
