using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace KnobControl;

/// <summary>
/// Click-through, non-activating window that draws the radial menu and the level gauge.
/// Everything is laid out on a fixed 560×560 stage and scaled to the configured size.
/// </summary>
internal sealed class OverlayWindow : Window
{
    const double Stage = 560, C = Stage / 2;
    const double OuterR = 258, OuterSelectedR = 272, InnerR = 132, HubR = 118;
    const double IconR = 195, IconSelectedR = 202;
    const double GaugeR = 236, GaugeStart = 135, GaugeSweep = 270;
    const double ShadowMargin = 56;

    static readonly Brush Plate = Solid(0x16, 0x1A, 0x21, 0xEB);
    static readonly Brush PlateEdge = Solid(0xFF, 0xFF, 0xFF, 0x10);
    static readonly Brush Hub = Solid(0x11, 0x15, 0x1B);
    static readonly Brush HubEdge = Solid(0xFF, 0xFF, 0xFF, 0x12);
    static readonly Brush SegmentFill = Solid(0xFF, 0xFF, 0xFF, 0x0B);
    static readonly Brush SegmentEdge = Solid(0xFF, 0xFF, 0xFF, 0x14);
    static readonly Brush Accent = Solid(0x3D, 0x7B, 0xF0);
    static readonly Brush AccentEdge = Solid(0x5B, 0x93, 0xFF);
    static readonly Brush GaugeTrack = Solid(0x23, 0x29, 0x33);
    static readonly Brush GaugeFill = Solid(0x4C, 0x8D, 0xFF);
    static readonly Brush TextMain = Solid(0xE8, 0xEC, 0xF2);
    static readonly Brush TextMuted = Solid(0x9A, 0xA4, 0xB2);
    static readonly Brush IconIdle = Solid(0xB7, 0xC0, 0xCC);
    static readonly Brush Toast = Solid(0xE8, 0xEC, 0xF2);
    static readonly Brush ToastText = Solid(0x0B, 0x0D, 0x11);
    static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");

    static readonly Geometry SunGlyph = Glyph(new GeometryGroup
    {
        Children =
        {
            new EllipseGeometry(new Point(12, 12), 4, 4),
            Geometry.Parse("M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"),
        },
    });
    static readonly Geometry SpeakerGlyph = Glyph(Geometry.Parse("M4 9h4l5-4v14l-5-4H4z M16.5 8.5a5 5 0 0 1 0 7 M19 6a8.5 8.5 0 0 1 0 12"));
    static readonly Geometry AppGlyph = Glyph(Geometry.Parse("M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h6v6h-6z"));

    readonly IReadOnlyList<MenuItemConfig> _items;
    readonly List<Path> _segments = new();
    readonly List<(Canvas Host, TextBlock Label, Path? Glyph, Image? Image)> _icons = new();
    readonly Grid _menuLayer = new(), _levelLayer = new();
    readonly TextBlock _counter, _name, _hint, _levelName, _levelValue, _toastText;
    readonly Border _toast;
    readonly Path _gaugeValue;
    readonly Ellipse _knobDotOuter, _knobDotInner;
    bool _hiding;

    public bool IsShown => IsVisible && !_hiding;
    public FrameworkElement Scene { get; }

    public OverlayWindow(IReadOnlyList<MenuItemConfig> items, IReadOnlyList<ImageSource?> icons, double size)
    {
        _items = items;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Width = Height = size + ShadowMargin * 2;
        Title = "Knob menu";

        var stage = new Grid { Width = Stage, Height = Stage };
        stage.Children.Add(new Ellipse
        {
            Fill = Plate, Stroke = PlateEdge, StrokeThickness = 1,
            Effect = new DropShadowEffect { BlurRadius = 70, ShadowDepth = 16, Direction = 270, Opacity = 0.55, Color = Colors.Black },
        });

        // Menu: segments, icons, hub.
        var ring = new Canvas { Width = Stage, Height = Stage };
        for (int i = 0; i < items.Count; i++)
        {
            var seg = new Path { StrokeThickness = 1 };
            _segments.Add(seg);
            ring.Children.Add(seg);
        }
        for (int i = 0; i < items.Count; i++)
        {
            var icon = BuildIcon(items[i], icons[i]);
            _icons.Add(icon);
            ring.Children.Add(icon.Host);
        }
        _menuLayer.Children.Add(ring);
        _menuLayer.Children.Add(new Ellipse { Width = HubR * 2, Height = HubR * 2, Fill = Hub, Stroke = HubEdge, StrokeThickness = 1 });
        _counter = Text(12, FontWeights.Medium, TextMuted, caps: true);
        _name = Text(30, FontWeights.Bold, TextMain);
        _hint = Text(13, FontWeights.Normal, TextMuted);
        _menuLayer.Children.Add(Stack(6, _counter, _name, _hint));
        stage.Children.Add(_menuLayer);

        // Level gauge.
        var gauge = new Canvas { Width = Stage, Height = Stage };
        gauge.Children.Add(GaugeStroke(GaugeTrack, Arc(GaugeR, GaugeStart, GaugeStart + GaugeSweep)));
        _gaugeValue = GaugeStroke(GaugeFill, Geometry.Empty);
        gauge.Children.Add(_gaugeValue);
        _knobDotOuter = new Ellipse { Width = 38, Height = 38, Fill = Brushes.White };
        _knobDotInner = new Ellipse { Width = 14, Height = 14, Fill = GaugeFill };
        gauge.Children.Add(_knobDotOuter);
        gauge.Children.Add(_knobDotInner);
        _levelLayer.Children.Add(gauge);
        _levelName = Text(13, FontWeights.Medium, TextMuted, caps: true);
        _levelValue = Text(88, FontWeights.Bold, TextMain);
        _levelValue.LineHeight = 92;
        _levelValue.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var levelHint = Text(13, FontWeights.Normal, TextMuted);
        levelHint.Text = "Turn to adjust · Press to go back";
        levelHint.Margin = new Thickness(0, 8, 0, 0);
        _levelLayer.Children.Add(Stack(4, _levelName, _levelValue, levelHint));
        _levelLayer.Visibility = Visibility.Collapsed;
        stage.Children.Add(_levelLayer);

        _toastText = Text(14, FontWeights.SemiBold, ToastText);
        _toast = new Border
        {
            Background = Toast, CornerRadius = new CornerRadius(999), Padding = new Thickness(18, 10, 18, 10),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -20), Child = _toastText, Visibility = Visibility.Collapsed,
        };
        stage.Children.Add(_toast);

        Scene = new Viewbox { Width = size, Height = size, Child = stage, Margin = new Thickness(ShadowMargin) };
        Content = Scene;
        Opacity = 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Never take focus and let clicks fall through to whatever is underneath.
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
            (IntPtr)(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT));
    }

    public void ShowMenu(int selected)
    {
        int n = _items.Count;
        for (int i = 0; i < n; i++)
        {
            bool on = i == selected;
            _segments[i].Data = Segment(i, n, on);
            _segments[i].Fill = on ? Accent : SegmentFill;
            _segments[i].Stroke = on ? AccentEdge : SegmentEdge;

            var (host, label, glyph, image) = _icons[i];
            var c = Polar(on ? IconSelectedR : IconR, CenterAngle(i, n));
            Canvas.SetLeft(host, c.X - host.Width / 2);
            Canvas.SetTop(host, c.Y - host.Height / 2);
            label.Foreground = on ? Brushes.White : IconIdle;
            if (glyph != null) glyph.Stroke = on ? Brushes.White : IconIdle;
            if (image != null) image.Opacity = on ? 1 : 0.8;
        }

        var item = _items[selected];
        _counter.Text = $"{selected + 1} / {n}";
        _name.Text = item.Name;
        _hint.Text = item.IsLevel ? "Press to adjust" : "Press to open";
        _menuLayer.Visibility = Visibility.Visible;
        _levelLayer.Visibility = Visibility.Collapsed;
        _toast.Visibility = Visibility.Collapsed;
    }

    public void ShowLevel(string name, int percent)
    {
        double end = GaugeStart + GaugeSweep * Math.Max(percent, 0.4) / 100;
        _gaugeValue.Data = Arc(GaugeR, GaugeStart, end);
        var dot = Polar(GaugeR, end);
        Place(_knobDotOuter, dot);
        Place(_knobDotInner, dot);
        _levelName.Text = name;
        _levelValue.Text = percent + "%";
        _menuLayer.Visibility = Visibility.Collapsed;
        _levelLayer.Visibility = Visibility.Visible;
        _toast.Visibility = Visibility.Collapsed;
    }

    public void ShowToast(string text)
    {
        _toastText.Text = text;
        _toast.Visibility = Visibility.Visible;
    }

    public void Appear()
    {
        _hiding = false;
        if (!IsVisible)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
            Show();
        }
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    }

    public void Vanish()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { if (_hiding) { Hide(); _hiding = false; } };
        BeginAnimation(OpacityProperty, fade);
    }

    (Canvas Host, TextBlock Label, Path? Glyph, Image? Image) BuildIcon(MenuItemConfig item, ImageSource? image)
    {
        var host = new Canvas { Width = 96, Height = 76 };
        FrameworkElement visual;
        Path? glyph = null;
        Image? img = null;
        if (image != null)
        {
            visual = img = new Image { Source = image, Width = 38, Height = 38 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        }
        else
        {
            glyph = new Path
            {
                Data = item.IsLevel ? (item.IsBrightness ? SunGlyph : SpeakerGlyph) : AppGlyph,
                StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            var box = new Grid { Width = 24, Height = 24 };
            box.Children.Add(glyph);
            visual = new Viewbox { Width = 36, Height = 36, Child = box };
        }
        var label = Text(12, FontWeights.Medium, IconIdle);
        label.Text = item.Name;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.MaxWidth = host.Width;
        var stack = Stack(6, visual, label);
        stack.Width = host.Width;
        stack.Height = host.Height;
        host.Children.Add(stack);
        return (host, label, glyph, img);
    }

    static Geometry Segment(int i, int n, bool selected)
    {
        double span = 360.0 / n, gap = 1.6, r = selected ? OuterSelectedR : OuterR;
        double a0 = CenterAngle(i, n) - span / 2 + gap, a1 = a0 + span - gap * 2;
        bool large = a1 - a0 > 180;
        var figure = new PathFigure { StartPoint = Polar(r, a0), IsClosed = true };
        figure.Segments.Add(new ArcSegment(Polar(r, a1), new Size(r, r), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(Polar(InnerR, a1), true));
        figure.Segments.Add(new ArcSegment(Polar(InnerR, a0), new Size(InnerR, InnerR), 0, large, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry(new[] { figure });
        g.Freeze();
        return g;
    }

    static Geometry Arc(double r, double from, double to)
    {
        var figure = new PathFigure { StartPoint = Polar(r, from) };
        figure.Segments.Add(new ArcSegment(Polar(r, to), new Size(r, r), 0, to - from > 180, SweepDirection.Clockwise, true));
        var g = new PathGeometry(new[] { figure });
        g.Freeze();
        return g;
    }

    static double CenterAngle(int i, int n) => -90 + i * 360.0 / n;

    static Point Polar(double r, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(C + r * Math.Cos(a), C + r * Math.Sin(a));
    }

    static void Place(FrameworkElement e, Point center)
    {
        Canvas.SetLeft(e, center.X - e.Width / 2);
        Canvas.SetTop(e, center.Y - e.Height / 2);
    }

    static Path GaugeStroke(Brush brush, Geometry data) => new()
    {
        Data = data, Stroke = brush, StrokeThickness = 30,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    };

    static TextBlock Text(double size, FontWeight weight, Brush brush, bool caps = false) => new()
    {
        FontFamily = Font, FontSize = size, FontWeight = weight, Foreground = brush,
        TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        Typography = { Capitals = caps ? FontCapitals.AllSmallCaps : FontCapitals.Normal },
    };

    static StackPanel Stack(double gap, params FrameworkElement[] children)
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < children.Length; i++)
        {
            if (i > 0) children[i].Margin = new Thickness(0, gap + children[i].Margin.Top, 0, 0);
            s.Children.Add(children[i]);
        }
        return s;
    }

    static Geometry Glyph(Geometry g)
    {
        g.Freeze();
        return g;
    }

    static SolidColorBrush Solid(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
