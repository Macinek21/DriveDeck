using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DriveDeck.App.Services;
using DriveDeck.App.Widgets;
using DriveDeck.Core;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace DriveDeck.App
{
    public sealed partial class LayoutEditorPage : Page
    {
        private AppSettings _work;
        private int _pageIndex;
        private WidgetConfig _selected;
        private List<ValueDefinition> _values;

        private static readonly string[] KindLabels = { "Number", "Dial gauge", "Bar", "Graph", "Camera preview", "Clock", "Status" };
        private static readonly WidgetKind[] Kinds = { WidgetKind.Number, WidgetKind.Gauge, WidgetKind.Bar, WidgetKind.Graph, WidgetKind.Camera, WidgetKind.Clock, WidgetKind.Status };
        private static readonly string[] ColorLabels = { "Accent", "Blue", "Green", "Orange", "Red", "Purple", "White", "Cyan" };
        private static readonly string[] ColorValues = { "", "#FF3FA9F5", "#FF4CD964", "#FFFF9500", "#FFE81123", "#FFB4009E", "#FFFFFFFF", "#FF00B7C3" };

        public LayoutEditorPage() { InitializeComponent(); }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            List<string> n; string err;
            _work = AppSettings.FromJson(AppHub.Settings.ToJson(), out n, out err) ?? AppSettings.Defaults();
            _values = PidCatalog.All(_work.Obd.CustomValues).ToList();
            _pageIndex = 0;
            Build();
        }

        private DashboardPage CurrentPage { get { return _work.Pages[_pageIndex]; } }

        private static ComboBox Combo(string header, IList<string> labels, int selected, Action<int> changed)
        {
            var c = new ComboBox { Header = header, Margin = new Thickness(0, 4, 8, 8), MinWidth = 120 };
            foreach (var l in labels) c.Items.Add(l);
            c.SelectedIndex = Math.Max(0, Math.Min(labels.Count - 1, selected));
            c.SelectionChanged += (s, e) => { if (c.SelectedIndex >= 0) changed(c.SelectedIndex); };
            return c;
        }

        private static IList<string> Range(int from, int to) { return Enumerable.Range(from, to - from + 1).Select(i => i.ToString()).ToList(); }

        private static StackPanel Row(params UIElement[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var i in items) p.Children.Add(i);
            return p;
        }

        private static TextBox NumberBox(string header, double value, Action<double> set)
        {
            var t = new TextBox { Header = header, Text = double.IsNaN(value) ? "" : value.ToString(CultureInfo.InvariantCulture), Width = 120, Margin = new Thickness(0, 4, 8, 8), PlaceholderText = "off" };
            t.TextChanged += (s, e) =>
            {
                double v;
                if (string.IsNullOrWhiteSpace(t.Text)) set(double.NaN);
                else if (double.TryParse(t.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsInfinity(v)) set(v);
            };
            return t;
        }

        private Button Btn(string text, Action click)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 4, 8, 4) };
            b.Click += (s, e) => click();
            return b;
        }

        private void Build()
        {
            Body.Children.Clear();
            if (_work.Pages.Count == 0) _work.Pages.Add(new DashboardPage());
            _pageIndex = Math.Max(0, Math.Min(_pageIndex, _work.Pages.Count - 1));
            var p = CurrentPage;

            Body.Children.Add(Row(
                Combo("Page", _work.Pages.Select(x => x.Name).ToList(), _pageIndex, i => { _pageIndex = i; _selected = null; Build(); }),
                Btn("Add page", () =>
                {
                    if (_work.Pages.Count >= AppSettings.MaxPages) return;
                    _work.Pages.Add(new DashboardPage { Name = "Page " + (_work.Pages.Count + 1) });
                    _pageIndex = _work.Pages.Count - 1; _selected = null; Build();
                }),
                Btn("Delete page", () =>
                {
                    if (_work.Pages.Count <= 1) return;
                    _work.Pages.RemoveAt(_pageIndex); _pageIndex = 0; _selected = null; Build();
                })));

            var name = new TextBox { Header = "Page name", Text = p.Name, MaxLength = 24, Margin = new Thickness(0, 4, 0, 8) };
            name.TextChanged += (s, e) => p.Name = name.Text;
            Body.Children.Add(name);

            Body.Children.Add(Row(
                Combo("Rows", Range(1, 6), p.Rows - 1, i => { p.Rows = i + 1; ClampWidgets(p); Build(); }),
                Combo("Columns", Range(1, 6), p.Columns - 1, i => { p.Columns = i + 1; ClampWidgets(p); Build(); }),
                Combo("Show when phone is", new[] { "Any orientation", "Landscape", "Portrait" }, Array.IndexOf(new[] { "any", "landscape", "portrait" }, p.Orientation),
                      i => { p.Orientation = new[] { "any", "landscape", "portrait" }[i]; Build(); })));

            Body.Children.Add(new TextBlock { Text = "Tap an empty cell to add a tile, or a tile to edit it. Swipe left/right on the dashboard to switch pages.", FontSize = 13, Foreground = new SolidColorBrush(Colors.Gray), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
            Body.Children.Add(BuildPreview(p));

            if (_selected != null && p.Widgets.Contains(_selected)) BuildWidgetEditor(p, _selected);
        }

        private static void ClampWidgets(DashboardPage p)
        {
            foreach (var w in p.Widgets)
            {
                w.Row = Math.Min(w.Row, p.Rows - 1);
                w.Column = Math.Min(w.Column, p.Columns - 1);
                w.RowSpan = Math.Max(1, Math.Min(w.RowSpan, p.Rows - w.Row));
                w.ColumnSpan = Math.Max(1, Math.Min(w.ColumnSpan, p.Columns - w.Column));
            }
        }

        private FrameworkElement BuildPreview(DashboardPage p)
        {
            bool portrait = p.Orientation == "portrait";
            var grid = new Grid { Background = new SolidColorBrush(Colors.Black), Height = portrait ? 420 : 260, Width = portrait ? 240 : 460, HorizontalAlignment = HorizontalAlignment.Left };
            for (int r = 0; r < p.Rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (int c = 0; c < p.Columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int r = 0; r < p.Rows; r++)
                for (int c = 0; c < p.Columns; c++)
                {
                    int rr = r, cc = c;
                    var cell = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(255, 50, 56, 64)), BorderThickness = new Thickness(1), Margin = new Thickness(1), Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
                    cell.Child = new TextBlock { Text = "+", Foreground = new SolidColorBrush(Color.FromArgb(255, 70, 76, 84)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 20 };
                    cell.Tapped += (s, e) =>
                    {
                        if (p.Widgets.Count >= AppSettings.MaxWidgetsPerPage) return;
                        var w = new WidgetConfig { Kind = WidgetKind.Number, ValueKey = PidCatalog.GpsSpeedKey, Row = rr, Column = cc };
                        p.Widgets.Add(w); _selected = w; Build();
                    };
                    Grid.SetRow(cell, r); Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }

            var accent = WidgetView.Parse(_work.Ui.Accent, Color.FromArgb(255, 63, 169, 245));
            foreach (var w in p.Widgets)
            {
                var def = _values.FirstOrDefault(v => v.Key == w.ValueKey);
                string title = KindLabels[Array.IndexOf(Kinds, w.Kind)];
                string sub = w.Kind == WidgetKind.Camera || w.Kind == WidgetKind.Clock || w.Kind == WidgetKind.Status ? "" : (def != null ? def.ShortName : "?");
                var color = string.IsNullOrEmpty(w.Color) ? accent : WidgetView.Parse(w.Color, accent);
                var tile = new Border
                {
                    Background = new SolidColorBrush(WidgetView.TileColor), Margin = new Thickness(2), CornerRadius = new CornerRadius(4),
                    BorderBrush = new SolidColorBrush(w == _selected ? Colors.White : color), BorderThickness = new Thickness(w == _selected ? 3 : 1),
                    Child = new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = sub, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = title, FontSize = 11, Foreground = new SolidColorBrush(Colors.Gray), HorizontalAlignment = HorizontalAlignment.Center }
                        }
                    }
                };
                var captured = w;
                tile.Tapped += (s, e) => { _selected = captured; e.Handled = true; Build(); };
                Grid.SetRow(tile, w.Row); Grid.SetColumn(tile, w.Column);
                Grid.SetRowSpan(tile, w.RowSpan); Grid.SetColumnSpan(tile, w.ColumnSpan);
                grid.Children.Add(tile);
            }
            return grid;
        }

        private void BuildWidgetEditor(DashboardPage p, WidgetConfig w)
        {
            var panel = new StackPanel();
            Body.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(255, 20, 24, 30)), Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0), Child = panel });
            panel.Children.Add(new TextBlock { Text = "Selected tile", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });

            panel.Children.Add(Combo("Type", KindLabels, Array.IndexOf(Kinds, w.Kind), i => { w.Kind = Kinds[i]; Build(); }));
            bool needsValue = w.Kind == WidgetKind.Number || w.Kind == WidgetKind.Gauge || w.Kind == WidgetKind.Bar || w.Kind == WidgetKind.Graph;
            if (needsValue)
            {
                var labels = _values.Select(v => v.Name + (string.IsNullOrEmpty(v.Unit) ? "" : " (" + v.Unit + ")")).ToList();
                panel.Children.Add(Combo("Shows", labels, _values.FindIndex(v => v.Key == w.ValueKey), i => { w.ValueKey = _values[i].Key; Build(); }));
            }
            panel.Children.Add(Row(
                Combo("Row", Range(1, p.Rows), w.Row, i => { w.Row = i; ClampWidgets(p); Build(); }),
                Combo("Column", Range(1, p.Columns), w.Column, i => { w.Column = i; ClampWidgets(p); Build(); })));
            panel.Children.Add(Row(
                Combo("Height (rows)", Range(1, p.Rows - w.Row), w.RowSpan - 1, i => { w.RowSpan = i + 1; Build(); }),
                Combo("Width (columns)", Range(1, p.Columns - w.Column), w.ColumnSpan - 1, i => { w.ColumnSpan = i + 1; Build(); })));

            if (needsValue)
            {
                var label = new TextBox { Header = "Label (empty = default)", Text = w.Label ?? "", MaxLength = 24, Margin = new Thickness(0, 4, 0, 8) };
                label.TextChanged += (s, e) => w.Label = label.Text;
                panel.Children.Add(label);
                panel.Children.Add(Combo("Colour", ColorLabels, Math.Max(0, Array.IndexOf(ColorValues, w.Color ?? "")), i => { w.Color = ColorValues[i]; Build(); }));
                panel.Children.Add(new TextBlock { Text = "Thresholds (amber = warning, red = alarm + notification). Leave empty to turn off.", FontSize = 13, Foreground = new SolidColorBrush(Colors.Gray), TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(Row(NumberBox("Warn above", w.WarnAbove, v => w.WarnAbove = v), NumberBox("Alarm above", w.AlarmAbove, v => w.AlarmAbove = v), NumberBox("Warn below", w.WarnBelow, v => w.WarnBelow = v)));
                if (w.Kind != WidgetKind.Number)
                    panel.Children.Add(Row(NumberBox("Scale min", w.Min, v => w.Min = v), NumberBox("Scale max", w.Max, v => w.Max = v)));
            }

            panel.Children.Add(Row(
                Btn("Delete tile", () => { p.Widgets.Remove(w); _selected = null; Build(); }),
                Btn("Done", () => { _selected = null; Build(); })));
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            var notes = _work.Normalize();
            AppHub.Settings.Pages = _work.Pages;
            try { await AppHub.SaveSettingsAsync(); }
            catch (Exception ex)
            {
                await new ContentDialog { Title = "Could not save", Content = ex.Message, PrimaryButtonText = "OK" }.ShowAsync();
                return;
            }
            MainPage.LayoutDirty = true;
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { if (Frame.CanGoBack) Frame.GoBack(); }
    }
}
