using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Hertzo.Core;

namespace Hertzo.Ui;

public static class Palette
{
    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];

    public static Brush Fg(Tone t) => Get(t switch
    {
        Tone.Ok => "OkBrush",
        Tone.Warn => "WarnBrush",
        Tone.Bad => "BadBrush",
        Tone.Info => "InfoBrush",
        Tone.Accent => "AccentBrush",
        _ => "TextDimBrush",
    });

    public static Brush Soft(Tone t) => Get(t switch
    {
        Tone.Ok => "OkSoftBrush",
        Tone.Warn => "WarnSoftBrush",
        Tone.Bad => "BadSoftBrush",
        Tone.Info => "InfoSoftBrush",
        Tone.Accent => "AccentSoftBrush",
        _ => "NeutralSoftBrush",
    });
}

// ======================= CONVERSORES =======================

/// <summary>Converte Tone em pincel. Parâmetro "soft" = fundo translúcido.</summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var tone = value is Tone t ? t : Tone.Neutral;
        return (parameter as string) == "soft" ? Palette.Soft(tone) : Palette.Fg(tone);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if ((parameter as string) == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBool : IValueConverter
{
    public static readonly InverseBool Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
}

public sealed class NullVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value is string s ? s.Length > 0 : value != null;
        if ((parameter as string) == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class UpperConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (value?.ToString() ?? "").ToUpper(culture);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>AlternationIndex (0..n) → "01", "02"…</summary>
public sealed class IndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int i ? (i + 1).ToString("00") : "00";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// ======================= ETIQUETA =======================

/// <summary>Etiqueta técnica: barra de cor à esquerda + texto mono. Cantos retos, como uma etiqueta impressa.</summary>
public sealed class Pill : Border
{
    private readonly TextBlock _text = new() { FontSize = 9.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 7, 0) };
    private readonly Border _bar = new() { Width = 3 };

    public Pill()
    {
        Height = 19;
        Margin = new Thickness(0, 0, 6, 6);
        BorderThickness = new Thickness(0);
        var dp = new DockPanel();
        DockPanel.SetDock(_bar, Dock.Left);
        dp.Children.Add(_bar);
        dp.Children.Add(_text);
        Child = dp;
        Loaded += (_, _) => { _text.FontFamily = (FontFamily)Application.Current.Resources["FontMono"]; Update(); };
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Pill), new PropertyMetadata("", (d, _) => ((Pill)d).Update()));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Tone), typeof(Pill), new PropertyMetadata(Tone.Neutral, (d, _) => ((Pill)d).Update()));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Tone Tone { get => (Tone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    private void Update()
    {
        if (Application.Current == null) return;
        _text.Text = Text;
        var fg = Palette.Fg(Tone);
        _text.Foreground = fg;
        _bar.Background = fg;
        Background = Palette.Soft(Tone);
    }
}

// ======================= RÉGUA =======================

/// <summary>Régua de marcas — assinatura visual. O valor “varre” até o alvo quando muda.</summary>
public sealed class TickRuler : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(TickRuler), new PropertyMetadata(0.0, OnValue));
    public static readonly DependencyProperty TicksProperty = DependencyProperty.Register(
        nameof(Ticks), typeof(int), typeof(TickRuler), new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(Tone), typeof(TickRuler), new FrameworkPropertyMetadata(Tone.Accent, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MajorEveryProperty = DependencyProperty.Register(
        nameof(MajorEvery), typeof(int), typeof(TickRuler), new FrameworkPropertyMetadata(10, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(TickRuler), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Ticks { get => (int)GetValue(TicksProperty); set => SetValue(TicksProperty, value); }
    public Tone Tone { get => (Tone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public int MajorEvery { get => (int)GetValue(MajorEveryProperty); set => SetValue(MajorEveryProperty, value); }

    public TickRuler() { Loaded += (_, _) => Sweep(Value, from: 0); }

    private static void OnValue(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var r = (TickRuler)d;
        if (r.IsLoaded) r.Sweep((double)e.NewValue, null);
    }

    private void Sweep(double to, double? from)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(1100))
        {
            EasingFunction = new System.Windows.Media.Animation.QuarticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        if (from.HasValue) anim.From = from;
        BeginAnimation(ShownProperty, anim);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var n = Math.Max(2, Ticks);
        var w = ActualWidth; var h = ActualHeight;
        var step = w / n;
        var tw = Math.Max(1.5, Math.Round(step * 0.34));
        var shown = (double)GetValue(ShownProperty);
        var lit = shown / 100.0 * n;
        var on = Palette.Fg(Tone);
        var off = Palette.Get("Bg4Brush");
        for (int i = 0; i < n; i++)
        {
            bool major = MajorEvery > 0 && i % MajorEvery == 0;
            double th = major ? h : h * 0.58;
            var x = Math.Round(i * step + (step - tw) / 2);
            var rect = new Rect(x, h - th, tw, th);
            if (i < Math.Floor(lit)) dc.DrawRectangle(on, null, rect);
            else if (i < lit) // marca parcial: acende de baixo para cima
            {
                dc.DrawRectangle(off, null, rect);
                var f = lit - i;
                dc.DrawRectangle(on, null, new Rect(x, h - th * f, tw, th * f));
            }
            else dc.DrawRectangle(off, null, rect);
        }
    }
}

// ======================= OSCILOSCÓPIO =======================

/// <summary>Histórico de uso desenhado como um osciloscópio: grade pontilhada, traço e “fósforo” no último ponto.</summary>
public sealed class PulseGraph : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<double>), typeof(PulseGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(
        nameof(Version), typeof(int), typeof(PulseGraph), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(PulseGraph), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Samples { get => (IReadOnlyList<double>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        var dots = new Pen(Palette.Get("LineStrongBrush"), 1) { DashStyle = new DashStyle([1, 3], 0) };
        for (int i = 1; i < 4; i++) dc.DrawLine(dots, new Point(0, Math.Round(h * i / 4.0) + 0.5), new Point(w, Math.Round(h * i / 4.0) + 0.5));
        for (int i = 1; i < 6; i++) dc.DrawLine(dots, new Point(Math.Round(w * i / 6.0) + 0.5, 0), new Point(Math.Round(w * i / 6.0) + 0.5, h));
        dc.DrawLine(new Pen(Palette.Get("LineStrongBrush"), 1), new Point(0, h - 0.5), new Point(w, h - 0.5));

        var s = Samples;
        if (s == null || s.Count < 2) return;
        const int max = 60;
        var dx = w / (max - 1);
        var start = max - s.Count;
        Point P(int i) => new((start + i) * dx, h - Math.Clamp(s[i], 0, 100) / 100.0 * (h - 6) - 3);

        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(P(0), false, false);
            for (int i = 1; i < s.Count; i++)
            {
                // degraus: leitura de instrumento, não curva “bonita”
                var p = P(i);
                c.LineTo(new Point(p.X, P(i - 1).Y), true, false);
                c.LineTo(p, true, false);
            }
        }
        var color = Stroke is SolidColorBrush sc ? sc.Color : Colors.White;
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)), 6), geo);
        dc.DrawGeometry(null, new Pen(Stroke, 1.5), geo);

        var last = P(s.Count - 1);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B)), 1), new Point(last.X, 0), new Point(last.X, h));
        dc.DrawRectangle(Stroke, null, new Rect(last.X - 3, last.Y - 3, 6, 6));
    }
}

// ======================= TEMPO DE QUADRO =======================

/// <summary>
/// Gráfico de tempo de quadro (ms) da medição: pior quadro de cada fatia, em degraus.
/// Linha tracejada = a outra medição na comparação. Guias = taxa do monitor e 60 FPS.
/// Picos acima da escala aparecem como marcas vermelhas no topo.
/// </summary>
public sealed class FrameTimeGraph : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(float[]), typeof(FrameTimeGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((FrameTimeGraph)d).Reveal()));
    public static readonly DependencyProperty CompareProperty = DependencyProperty.Register(
        nameof(Compare), typeof(float[]), typeof(FrameTimeGraph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RefreshMsProperty = DependencyProperty.Register(
        nameof(RefreshMs), typeof(double), typeof(FrameTimeGraph), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(FrameTimeGraph), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public float[]? Series { get => (float[]?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public float[]? Compare { get => (float[]?)GetValue(CompareProperty); set => SetValue(CompareProperty, value); }
    public double RefreshMs { get => (double)GetValue(RefreshMsProperty); set => SetValue(RefreshMsProperty, value); }

    public FrameTimeGraph() { Loaded += (_, _) => Reveal(); }

    private void Reveal()
    {
        if (!IsLoaded) return;
        BeginAnimation(ShownProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1000))
        {
            EasingFunction = new System.Windows.Media.Animation.QuarticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        });
    }

    private static double Pct(float[] s, double q)
    {
        var c = s.ToArray();
        Array.Sort(c);
        return c[(int)Math.Clamp(Math.Ceiling(q * c.Length) - 1, 0, c.Length - 1)];
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        const double left = 44, top = 8, bottom = 18;
        double gw = w - left, gh = h - top - bottom;
        if (gw < 20 || gh < 20) return;

        var mono = (FontFamily)Application.Current.Resources["FontMono"];
        var face = new Typeface(mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var mute = Palette.Get("TextMuteBrush");
        FormattedText T(string text, Brush b) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 9.5, b, dpi);

        var s = Series;
        var cmp = Compare;
        double top99 = 0;
        if (s is { Length: > 1 }) top99 = Math.Max(top99, Pct(s, 0.985));
        if (cmp is { Length: > 1 }) top99 = Math.Max(top99, Pct(cmp, 0.985));
        double ymax = Math.Max(top99 * 1.6, Math.Max(RefreshMs * 1.8, 8));
        // escala "redonda": 4 divisões de 1, 2, 2,5, 5, 10, 20, 25, 50… ms
        double raw = ymax / 4, mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * mag).First(v => v >= raw - 1e-9);
        ymax = step * 4;
        double Y(double ms) => top + gh - Math.Min(ms, ymax) / ymax * gh;

        // grade pontilhada + eixo
        var dots = new Pen(Palette.Get("LineStrongBrush"), 1) { DashStyle = new DashStyle([1, 3], 0) };
        for (int i = 0; i <= 4; i++)
        {
            var v = ymax * i / 4;
            var y = Math.Round(Y(v)) + 0.5;
            dc.DrawLine(i == 0 ? new Pen(Palette.Get("LineStrongBrush"), 1) : dots, new Point(left, y), new Point(w, y));
            var t = T($"{v:0.#}", mute);
            dc.DrawText(t, new Point(left - 8 - t.Width, y - t.Height / 2));
        }
        dc.DrawText(T("MS", mute), new Point(0, top + gh + 4));

        void Guide(double ms, string label, Brush b)
        {
            if (ms <= 0 || ms >= ymax) return;
            var y = Math.Round(Y(ms)) + 0.5;
            dc.DrawLine(new Pen(b, 1) { DashStyle = new DashStyle([6, 4], 0) }, new Point(left, y), new Point(w, y));
            var t = T(label, b);
            dc.DrawRectangle(Palette.Get("Bg1Brush"), null, new Rect(w - t.Width - 10, y - t.Height - 2, t.Width + 8, t.Height + 1));
            dc.DrawText(t, new Point(w - t.Width - 6, y - t.Height - 2));
        }
        Guide(RefreshMs, $"{1000 / Math.Max(RefreshMs, 0.01):0} FPS · MONITOR", Palette.Get("InfoBrush"));
        if (Math.Abs(RefreshMs - 1000 / 60.0) > 1) Guide(1000 / 60.0, "60 FPS", mute);

        var shown = (double)GetValue(ShownProperty);
        Geometry Trace(float[] d, double portion)
        {
            var g = new StreamGeometry();
            int n = Math.Max(2, (int)Math.Ceiling(d.Length * portion));
            double dx = gw / (d.Length - 1);
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(left, Y(d[0])), false, false);
                for (int i = 1; i < Math.Min(n, d.Length); i++)
                {
                    var x = left + i * dx;
                    c.LineTo(new Point(x, Y(d[i - 1])), true, false);
                    c.LineTo(new Point(x, Y(d[i])), true, false);
                }
            }
            g.Freeze();
            return g;
        }

        if (cmp is { Length: > 1 })
            dc.DrawGeometry(null, new Pen(Palette.Get("TextDimBrush"), 1) { DashStyle = new DashStyle([3, 2], 0) }, Trace(cmp, 1));

        if (s is { Length: > 1 })
        {
            var accent = Palette.Get("AccentBrush");
            var col = ((SolidColorBrush)accent).Color;
            var geo = Trace(s, shown);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(46, col.R, col.G, col.B)), 6), geo);
            dc.DrawGeometry(null, new Pen(accent, 1.5), geo);

            // picos fora da escala
            var bad = Palette.Get("BadBrush");
            double dx = gw / (s.Length - 1);
            int lim = (int)Math.Ceiling(s.Length * shown);
            for (int i = 0; i < Math.Min(lim, s.Length); i++)
                if (s[i] > ymax) dc.DrawRectangle(bad, null, new Rect(left + i * dx - 1.5, top - 2, 3, 7));

            if (shown < 1)
            {
                var x = left + gw * shown;
                dc.DrawLine(new Pen(accent, 1), new Point(x, top), new Point(x, top + gh));
            }
        }

        var startT = T("INÍCIO", mute);
        dc.DrawText(startT, new Point(left, top + gh + 4));
        var endT = T("FIM", mute);
        dc.DrawText(endT, new Point(w - endT.Width, top + gh + 4));
    }
}

/// <summary>Largura → altura 16:9 (miniaturas dos clipes).</summary>
public sealed class AspectConverter : IValueConverter
{
    public static readonly AspectConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double w && w > 0 ? Math.Round(w * 9 / 16) : 120.0;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// ======================= MEDIDOR DE NÍVEL =======================

/// <summary>Medidor de nível de áudio em segmentos (escala em dB: -60 a 0). Verde, amarelo perto do máximo, vermelho no pico.</summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(LevelMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        const int n = 42;
        double w = ActualWidth, h = ActualHeight;
        double step = w / n, seg = Math.Max(1.5, step - 2);
        var db = Level <= 0.0001 ? -60 : 20 * Math.Log10(Level);
        var lit = Math.Clamp((db + 60) / 60, 0, 1) * n;
        for (int i = 0; i < n; i++)
        {
            var frac = (i + 1) / (double)n;
            var tone = frac > 0.93 ? Tone.Bad : frac > 0.78 ? Tone.Warn : Tone.Ok;
            var brush = i < lit ? Palette.Fg(tone) : Palette.Get("Bg4Brush");
            dc.DrawRectangle(brush, null, new Rect(Math.Round(i * step), 0, seg, h));
        }
    }
}
