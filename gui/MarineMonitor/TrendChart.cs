using System.Windows;
using System.Windows.Media;
using System.Globalization;

namespace MarineMonitor;

public sealed class TrendSeries(int channels)
{
    public sealed record Point(DateTime Time, double[]? Values);
    public int Channels { get; } = channels;
    public List<Point> Points { get; } = [];
    public event Action? Changed;
    public void Add(double[] values, DateTime? time = null)
    {
        Points.Add(new(time ?? DateTime.UtcNow, (double[])values.Clone()));
        Trim(); Changed?.Invoke();
    }
    public void Break() { if (Points.Count > 0 && Points[^1].Values != null) Points.Add(new(DateTime.UtcNow, null)); }
    public void Clear() { Points.Clear(); Changed?.Invoke(); }
    private void Trim() { Points.RemoveAll(p => p.Time < DateTime.UtcNow.AddSeconds(-60)); if (Points.Count > 650) Points.RemoveRange(0, Points.Count - 650); }
    public void Pulse() { Trim(); Changed?.Invoke(); }
}
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(TrendSeries), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
    public TrendSeries? Series { get => (TrendSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TrendChart)d;
        if (e.OldValue is TrendSeries old) old.Changed -= chart.InvalidateVisual;
        if (e.NewValue is TrendSeries next) next.Changed += chart.InvalidateVisual;
    }
    private static readonly Brush[] Colors = [new SolidColorBrush(Color.FromRgb(56, 216, 195)), new SolidColorBrush(Color.FromRgb(107, 164, 255)), new SolidColorBrush(Color.FromRgb(233, 177, 92))];
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 100 || h < 50) return;
        Rect area = new(46, 8, w - 56, h - 29);
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(42, 57, 77)), 1);
        void Label(string value, double x, double y) => dc.DrawText(new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, new SolidColorBrush(Color.FromRgb(132, 151, 174)), VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
        var points = Series?.Points ?? [];
        double[] values = points.Where(p => p.Values != null).SelectMany(p => p.Values!).ToArray();
        double min = values.Length == 0 ? -1 : values.Min(), max = values.Length == 0 ? 1 : values.Max();
        double pad = Math.Max((max - min) * .1, Math.Max(Math.Abs(max) * .05, 0.00001)); min -= pad; max += pad;
        for (int i = 0; i < 3; i++)
        {
            double y = area.Top + area.Height * i / 2;
            dc.DrawLine(grid, new(area.Left, y), new(area.Right, y));
            Label((max - (max - min) * i / 2).ToString("0.####"), 0, y - 6);
        }
        Label("−60s", area.Left, area.Bottom + 5); Label("−30s", area.Left + area.Width / 2 - 12, area.Bottom + 5); Label("지금", area.Right - 23, area.Bottom + 5);
        if (values.Length == 0) { Label("수신한 데이터가 여기에 표시됩니다", area.Left + 16, area.Top + area.Height / 2 - 6); return; }
        DateTime now = DateTime.UtcNow;
        dc.PushClip(new RectangleGeometry(area));
        for (int channel = 0; channel < (Series?.Channels ?? 0); channel++)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                bool open = false;
                foreach (var p in points)
                {
                    if (p.Values == null || channel >= p.Values.Length) { open = false; continue; }
                    var position = new System.Windows.Point(area.Right - (now - p.Time).TotalSeconds / 60 * area.Width,
                        area.Bottom - (p.Values[channel] - min) / (max - min) * area.Height);
                    if (!open) { context.BeginFigure(position, false, false); open = true; } else context.LineTo(position, true, false);
                }
            }
            geometry.Freeze(); dc.DrawGeometry(null, new Pen(Colors[channel % Colors.Length], 1.3), geometry);
        }
        dc.Pop();
    }
}
