using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Hertzo.Ui;

/// <summary>Como cada placa entra na tela. Cada tela mistura vários tipos — nada de animação única para tudo.</summary>
public enum Reveal
{
    /// <summary>Sobe com leve “quique”.</summary>
    Rise,
    /// <summary>Recorte da esquerda para a direita, com um feixe acompanhando a borda.</summary>
    Wipe,
    /// <summary>Abre de cima para baixo como uma persiana.</summary>
    Shutter,
    /// <summary>Linha de varredura desce revelando o conteúdo (CRT).</summary>
    Scan,
    /// <summary>Entra “tremendo” em quadros discretos, como sinal sintonizando.</summary>
    Glitch,
    /// <summary>Sai do desfoque e do zoom para o foco.</summary>
    Focus,
    None,
}

/// <summary>
/// Placa — o “card” do Hertzo. Cantos retos, marcas de corte de gráfica nos cantos (abrem no hover),
/// sombra dura hachurada deslocada no hover, entrada animada configurável, varredura ao mudar de estado
/// e efeito magnético opcional seguindo o cursor.
/// </summary>
public class Plate : ContentControl
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(nameof(Code), typeof(string), typeof(Plate), new PropertyMetadata(""));
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal), typeof(Reveal), typeof(Plate), new PropertyMetadata(Reveal.Rise));
    public static readonly DependencyProperty DelayProperty = DependencyProperty.Register(nameof(Delay), typeof(double), typeof(Plate), new PropertyMetadata(0.0));
    public static readonly DependencyProperty StaggerProperty = DependencyProperty.Register(nameof(Stagger), typeof(bool), typeof(Plate), new PropertyMetadata(false));
    public static readonly DependencyProperty MagneticProperty = DependencyProperty.Register(nameof(Magnetic), typeof(bool), typeof(Plate), new PropertyMetadata(false));
    public static readonly DependencyProperty HoverFxProperty = DependencyProperty.Register(nameof(HoverFx), typeof(bool), typeof(Plate), new PropertyMetadata(true));
    public static readonly DependencyProperty ShowCornersProperty = DependencyProperty.Register(nameof(ShowCorners), typeof(bool), typeof(Plate), new PropertyMetadata(true));
    public bool ShowCorners { get => (bool)GetValue(ShowCornersProperty); set => SetValue(ShowCornersProperty, value); }

    public static readonly DependencyProperty FlashProperty = DependencyProperty.Register(nameof(Flash), typeof(object), typeof(Plate), new PropertyMetadata(null, (d, e) => { if (!Equals(e.OldValue, e.NewValue) && e.OldValue != null) ((Plate)d).PlayFlash(); }));

    public string Code { get => (string)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }
    public Reveal Reveal { get => (Reveal)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }
    /// <summary>Atraso da entrada em milissegundos.</summary>
    public double Delay { get => (double)GetValue(DelayProperty); set => SetValue(DelayProperty, value); }
    /// <summary>Quando dentro de uma lista, soma um atraso proporcional à posição do item.</summary>
    public bool Stagger { get => (bool)GetValue(StaggerProperty); set => SetValue(StaggerProperty, value); }
    public bool Magnetic { get => (bool)GetValue(MagneticProperty); set => SetValue(MagneticProperty, value); }
    public bool HoverFx { get => (bool)GetValue(HoverFxProperty); set => SetValue(HoverFxProperty, value); }
    /// <summary>Ao mudar (ex.: ajuste ligado/desligado), passa um feixe de luz pela placa.</summary>
    public object? Flash { get => GetValue(FlashProperty); set => SetValue(FlashProperty, value); }

    private Border? _face, _shadow;
    private Rectangle? _sweep, _scan;
    private readonly List<(Path Path, double Dx, double Dy)> _corners = [];
    private bool _revealed;

    public Plate()
    {
        Loaded += (_, _) =>
        {
            if (_revealed) return;
            _revealed = true;
            PlayReveal();
        };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _face = GetTemplateChild("PART_Face") as Border;
        _shadow = GetTemplateChild("PART_Shadow") as Border;
        _sweep = GetTemplateChild("PART_Sweep") as Rectangle;
        _scan = GetTemplateChild("PART_Scan") as Rectangle;
        _corners.Clear();
        AddCorner("PART_TL", -4, -4); AddCorner("PART_TR", 4, -4);
        AddCorner("PART_BL", -4, 4); AddCorner("PART_BR", 4, 4);
    }

    private void AddCorner(string name, double dx, double dy)
    {
        if (GetTemplateChild(name) is Path p)
        {
            p.RenderTransform = new TranslateTransform();
            _corners.Add((p, dx, dy));
        }
    }

    // ---------------- entrada ----------------

    private static DoubleAnimation D(double from, double to, double ms, TimeSpan begin, IEasingFunction? ease = null) => new(from, to, TimeSpan.FromMilliseconds(ms))
    {
        BeginTime = begin,
        EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut },
    };

    private int IndexInList()
    {
        DependencyObject? cur = this;
        for (int depth = 0; cur != null && depth < 12; depth++)
        {
            var owner = ItemsControl.ItemsControlFromItemContainer(cur);
            if (owner != null) return Math.Max(0, owner.ItemContainerGenerator.IndexFromContainer(cur));
            cur = VisualTreeHelper.GetParent(cur);
        }
        return 0;
    }

    private void PlayReveal()
    {
        var delay = Delay + (Stagger ? Math.Min(IndexInList(), 14) * 60 : 0);
        var begin = TimeSpan.FromMilliseconds(delay);

        switch (Reveal)
        {
            case Reveal.Rise:
            {
                var tt = new TranslateTransform(0, 28);
                RenderTransform = tt;
                Opacity = 0;
                BeginAnimation(OpacityProperty, D(0, 1, 380, begin));
                tt.BeginAnimation(TranslateTransform.YProperty, D(28, 0, 640, begin, new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }));
                break;
            }
            case Reveal.Shutter:
            {
                var st = new ScaleTransform(1, 0);
                RenderTransformOrigin = new Point(0.5, 0);
                RenderTransform = st;
                st.BeginAnimation(ScaleTransform.ScaleYProperty, D(0, 1, 620, begin, new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut }));
                FadeContent(begin + TimeSpan.FromMilliseconds(260), 360);
                break;
            }
            case Reveal.Wipe:
            {
                var w = Math.Max(1, ActualWidth); var h = Math.Max(1, ActualHeight);
                var clip = new RectangleGeometry(new Rect(0, 0, 0, h + 20));
                Clip = clip;
                var ra = new RectAnimation(new Rect(0, 0, 0, h + 20), new Rect(0, 0, w + 20, h + 20), TimeSpan.FromMilliseconds(720))
                {
                    BeginTime = begin,
                    EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseInOut },
                };
                ra.Completed += (_, _) => Clip = null;
                clip.BeginAnimation(RectangleGeometry.RectProperty, ra);
                if (_sweep != null)
                {
                    var tx = new TranslateTransform(-140, 0);
                    _sweep.RenderTransform = tx;
                    tx.BeginAnimation(TranslateTransform.XProperty, D(-140, w, 720, begin, new QuinticEase { EasingMode = EasingMode.EaseInOut }));
                    var op = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
                    op.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
                    op.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))));
                    op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(820))));
                    _sweep.BeginAnimation(OpacityProperty, op);
                }
                break;
            }
            case Reveal.Scan:
            {
                var h = Math.Max(1, ActualHeight);
                var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                var s1 = new GradientStop(Colors.Black, 0);
                var s2 = new GradientStop(Colors.Transparent, 0.001);
                mask.GradientStops.Add(s1); mask.GradientStops.Add(s2);
                OpacityMask = mask;
                var a1 = D(0, 1.02, 760, begin, new SineEase { EasingMode = EasingMode.EaseInOut });
                a1.Completed += (_, _) => OpacityMask = null;
                s1.BeginAnimation(GradientStop.OffsetProperty, a1);
                s2.BeginAnimation(GradientStop.OffsetProperty, D(0.001, 1.03, 760, begin, new SineEase { EasingMode = EasingMode.EaseInOut }));
                if (_scan != null)
                {
                    var ty = new TranslateTransform();
                    _scan.RenderTransform = ty;
                    ty.BeginAnimation(TranslateTransform.YProperty, D(0, h, 760, begin, new SineEase { EasingMode = EasingMode.EaseInOut }));
                    var op = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
                    op.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                    op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700))));
                    op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(860))));
                    _scan.BeginAnimation(OpacityProperty, op);
                }
                break;
            }
            case Reveal.Glitch:
            {
                var tt = new TranslateTransform();
                RenderTransform = tt;
                Opacity = 0;
                var x = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
                var o = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
                (double t, double dx, double op)[] frames = [(0, -14, 0.0), (50, 10, 0.8), (100, -6, 0.2), (150, 4, 1), (210, -2, 0.5), (260, 0, 1)];
                foreach (var f in frames)
                {
                    x.KeyFrames.Add(new DiscreteDoubleKeyFrame(f.dx, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(f.t))));
                    o.KeyFrames.Add(new DiscreteDoubleKeyFrame(f.op, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(f.t))));
                }
                tt.BeginAnimation(TranslateTransform.XProperty, x);
                BeginAnimation(OpacityProperty, o);
                break;
            }
            case Reveal.Focus:
            {
                var blur = new BlurEffect { Radius = 16, RenderingBias = RenderingBias.Performance };
                Effect = blur;
                var st = new ScaleTransform(1.045, 1.045);
                RenderTransformOrigin = new Point(0.5, 0.5);
                RenderTransform = st;
                Opacity = 0;
                BeginAnimation(OpacityProperty, D(0, 1, 420, begin));
                st.BeginAnimation(ScaleTransform.ScaleXProperty, D(1.045, 1, 700, begin));
                st.BeginAnimation(ScaleTransform.ScaleYProperty, D(1.045, 1, 700, begin));
                var b = D(16, 0, 620, begin);
                b.Completed += (_, _) => Effect = null;
                blur.BeginAnimation(BlurEffect.RadiusProperty, b);
                break;
            }
        }
    }

    private void FadeContent(TimeSpan begin, double ms)
    {
        if (GetTemplateChild("PART_Content") is UIElement c)
        {
            c.Opacity = 0;
            c.BeginAnimation(OpacityProperty, D(0, 1, ms, begin));
        }
    }

    // ---------------- estado ----------------

    private void PlayFlash()
    {
        if (!_revealed || _sweep == null || !IsLoaded) return;
        var tx = new TranslateTransform();
        _sweep.RenderTransform = tx;
        tx.BeginAnimation(TranslateTransform.XProperty, D(-140, ActualWidth, 560, TimeSpan.Zero, new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        var op = new DoubleAnimationUsingKeyFrames();
        op.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420))));
        op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))));
        _sweep.BeginAnimation(OpacityProperty, op);
    }

    // ---------------- hover ----------------

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (!HoverFx) return;
        Hover(true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!HoverFx) return;
        Hover(false);
        if (Magnetic && _face?.RenderTransform is TranslateTransform ft)
        {
            ft.BeginAnimation(TranslateTransform.XProperty, D(ft.X, 0, 380, TimeSpan.Zero, new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }));
            ft.BeginAnimation(TranslateTransform.YProperty, D(ft.Y, 0, 380, TimeSpan.Zero, new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Magnetic || _face == null || ActualWidth < 1) return;
        if (_face.RenderTransform is not TranslateTransform ft || ft.IsFrozen) { ft = new TranslateTransform(); _face.RenderTransform = ft; }
        var p = e.GetPosition(this);
        var dx = (p.X / ActualWidth - 0.5) * 7;
        var dy = (p.Y / ActualHeight - 0.5) * 7;
        ft.BeginAnimation(TranslateTransform.XProperty, D(ft.X, dx, 120, TimeSpan.Zero));
        ft.BeginAnimation(TranslateTransform.YProperty, D(ft.Y, dy, 120, TimeSpan.Zero));
    }

    private void Hover(bool on)
    {
        var dur = on ? 180 : 260;
        if (_shadow != null)
        {
            if (_shadow.RenderTransform is not TranslateTransform st || st.IsFrozen) { st = new TranslateTransform(); _shadow.RenderTransform = st; }
            st.BeginAnimation(TranslateTransform.XProperty, D(st.X, on ? 7 : 0, dur, TimeSpan.Zero));
            st.BeginAnimation(TranslateTransform.YProperty, D(st.Y, on ? 7 : 0, dur, TimeSpan.Zero));
            _shadow.BeginAnimation(OpacityProperty, D(_shadow.Opacity, on ? 1 : 0, dur, TimeSpan.Zero));
        }
        var accent = Palette.Get("AccentBrush");
        var mute = Palette.Get("TextMuteBrush");
        foreach (var (path, dx, dy) in _corners)
        {
            var t = (TranslateTransform)path.RenderTransform;
            t.BeginAnimation(TranslateTransform.XProperty, D(t.X, on ? dx : 0, dur, TimeSpan.Zero, new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut }));
            t.BeginAnimation(TranslateTransform.YProperty, D(t.Y, on ? dy : 0, dur, TimeSpan.Zero, new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut }));
            path.Stroke = on ? accent : mute;
        }
        if (_face != null) _face.BorderBrush = on ? Palette.Get("LineStrongBrush") : BorderBrush;
    }
}

/// <summary>Letreiro rolante contínuo (estilo painel de box/pit-wall) com dados ao vivo.</summary>
public sealed class Ticker : Canvas
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Ticker), new PropertyMetadata("", (d, _) => ((Ticker)d).Update()));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private readonly TextBlock _a = new(), _b = new();
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    private readonly TranslateTransform _tx = new();
    private double _w;

    public Ticker()
    {
        ClipToBounds = true;
        _row.Children.Add(_a);
        _row.Children.Add(_b);
        _row.RenderTransform = _tx;
        Children.Add(_row);
        Loaded += (_, _) =>
        {
            foreach (var t in new[] { _a, _b })
            {
                t.FontFamily = (FontFamily)Application.Current.Resources["FontMono"];
                t.FontSize = 10.5;
                t.Foreground = Palette.Get("TextMuteBrush");
            }
            HookWindow();
            Update();
        };
    }

    private void Update()
    {
        _a.Text = _b.Text = Text + "        ";
        _row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = _a.DesiredSize.Width;
        if (w < 1 || Math.Abs(w - _w) < 2) return;
        _w = w;
        _clock = new DoubleAnimation(0, -w, TimeSpan.FromSeconds(w / 38)) { RepeatBehavior = RepeatBehavior.Forever }.CreateClock();
        _tx.ApplyAnimationClock(TranslateTransform.XProperty, _clock);
        if (_win is { IsActive: false }) _clock.Controller?.Pause();
    }

    // Com a janela em segundo plano (ex.: durante o jogo) o letreiro para: assim o Hertzo não fica redesenhando a tela.
    private AnimationClock? _clock;
    private Window? _win;

    private void HookWindow()
    {
        if (_win != null) return;
        _win = Window.GetWindow(this);
        if (_win == null) return;
        _win.Activated += (_, _) => _clock?.Controller?.Resume();
        _win.Deactivated += (_, _) => _clock?.Controller?.Pause();
    }
}

/// <summary>Numeral gigante só com contorno (tipografia de cartaz). Entra deslizando devagar.</summary>
public sealed class OutlineText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlineText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(OutlineText),
        new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(OutlineText),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(OutlineText),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private Geometry? _geo;

    public OutlineText()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            var tt = new TranslateTransform(-24, 0);
            RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-24, 0, TimeSpan.FromMilliseconds(1100)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)));
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var ft = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed),
            Size, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _geo = ft.BuildGeometry(new Point(0, 0));
        var b = _geo.Bounds;
        return b.IsEmpty ? new Size(0, 0) : new Size(b.Width + 2, b.Height + 2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_geo == null || _geo.Bounds.IsEmpty) return;
        var b = _geo.Bounds;
        dc.PushTransform(new TranslateTransform(-b.X + 1, -b.Y + 1));
        dc.DrawGeometry(Fill, new Pen(Stroke, 1.2), _geo);
        dc.Pop();
    }
}

/// <summary>Cabeçalho de página. O conteúdo (Content) vai à direita — normalmente os botões de ação.</summary>
public sealed class PageHeader : ContentControl
{
    public static readonly DependencyProperty NumberProperty = DependencyProperty.Register(nameof(Number), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty KickerProperty = DependencyProperty.Register(nameof(Kicker), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata(""));

    public string Number { get => (string)GetValue(NumberProperty); set => SetValue(NumberProperty, value); }
    public string Kicker { get => (string)GetValue(KickerProperty); set => SetValue(KickerProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
}

public static class Textures
{
    /// <summary>Grão de filme gerado na hora (nada de imagem externa).</summary>
    public static ImageBrush Grain()
    {
        const int s = 180;
        var px = new byte[s * s * 4];
        var r = new Random(1984);
        for (int i = 0; i < s * s; i++)
        {
            byte v = (byte)r.Next(256);
            px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v;
            px[i * 4 + 3] = (byte)r.Next(0, 22);
        }
        var bmp = BitmapSource.Create(s, s, 96, 96, PixelFormats.Bgra32, null, px, s * 4);
        bmp.Freeze();
        var brush = new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, s, s), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        brush.Freeze();
        return brush;
    }
}
