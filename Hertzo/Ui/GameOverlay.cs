using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hertzo.Core;

namespace Hertzo.Ui;

/// <summary>
/// Aviso por cima do jogo: janela transparente, sempre no topo, que não rouba o foco nem recebe cliques.
/// Funciona em tela cheia em janela e no modo tela cheia moderno do Windows 11. Em tela cheia exclusiva
/// antiga o Windows pode não mostrar nada por cima do jogo (os bipes continuam valendo).
/// Fica visível só o necessário: enquanto aparece, o Windows compõe a tela, o que soma um pouco de atraso.
/// </summary>
public sealed class GameOverlay
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000, WS_EX_TOPMOST = 0x8;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    public static GameOverlay Instance { get; } = new();

    /// <summary>off | tl | tr | bl | br</summary>
    public string Corner { get; set; } = "tr";
    public bool ShowRecIndicator { get; set; } = true;

    private Window? _toastWin, _recWin;
    private TextBlock? _toastTitle, _toastSub, _recText;
    private Border? _toastBar;
    private DispatcherTimer? _hide;

    private Window Make(FrameworkElement content)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            Topmost = true, ShowActivated = false, ShowInTaskbar = false, Focusable = false,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Content = content,
            Title = "Hertzo",
        };
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOPMOST);
        };
        return w;
    }

    private void Place(Window w, int stackIndex)
    {
        w.UpdateLayout();
        var area = SystemParameters.WorkArea;
        var full = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        const double m = 28;
        double ww = w.ActualWidth, wh = w.ActualHeight;
        double y0 = stackIndex * (wh + 10);
        var (x, y) = Corner switch
        {
            "tl" => (full.Left + m, full.Top + m + y0),
            "bl" => (full.Left + m, full.Bottom - m - wh - y0),
            "br" => (full.Right - m - ww, full.Bottom - m - wh - y0),
            _ => (full.Right - m - ww, full.Top + m + y0),
        };
        w.Left = x; w.Top = y;
        var h = new WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);
    }

    private static Brush B(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>"CLIPE SALVO · 30 S" por 2,6 s no canto escolhido.</summary>
    public void Toast(string title, string sub, Tone tone = Tone.Accent)
    {
        if (Corner == "off") return;
        if (_toastWin == null)
        {
            _toastTitle = new TextBlock { FontFamily = (FontFamily)Application.Current.Resources["FontDisplay"], FontStretch = FontStretches.Condensed, FontWeight = FontWeights.Bold, FontSize = 22, Foreground = B("TextBrush") };
            _toastSub = new TextBlock { FontFamily = (FontFamily)Application.Current.Resources["FontMono"], FontSize = 10.5, Foreground = B("TextDimBrush"), Margin = new Thickness(0, 2, 0, 0) };
            _toastBar = new Border { Width = 6 };
            var mark = new TextBlock { Text = "HERTZO", FontFamily = (FontFamily)Application.Current.Resources["FontMono"], FontSize = 9, Foreground = B("TextMuteBrush"), Margin = new Thickness(0, 0, 0, 3) };
            var stack = new StackPanel { Margin = new Thickness(14, 10, 18, 11) };
            stack.Children.Add(mark); stack.Children.Add(_toastTitle); stack.Children.Add(_toastSub);
            var dock = new DockPanel();
            DockPanel.SetDock(_toastBar, Dock.Left);
            dock.Children.Add(_toastBar); dock.Children.Add(stack);
            var card = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0D, 0x0C, 0x0A)), BorderBrush = B("LineStrongBrush"), BorderThickness = new Thickness(1), Child = dock, RenderTransform = new TranslateTransform() };
            _toastWin = Make(card);
        }
        _toastTitle!.Text = title;
        _toastSub!.Text = sub;
        _toastBar!.Background = Palette.Fg(tone);
        if (!_toastWin.IsVisible) _toastWin.Show();
        Place(_toastWin, 0);

        // entra deslizando de lado, como as placas do app
        var card2 = (Border)_toastWin.Content;
        var tx = (TranslateTransform)card2.RenderTransform;
        var dir = Corner is "tl" or "bl" ? -1 : 1;
        tx.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dir * 40, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
        card2.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        _hide?.Stop();
        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        _hide.Tick += (_, _) =>
        {
            _hide!.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => _toastWin?.Hide();
            card2.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        _hide.Start();
    }

    /// <summary>Indicador "● REC 01:23" enquanto a gravação manual roda.</summary>
    public void Rec(bool on, string elapsed = "")
    {
        if (!on || Corner == "off" || !ShowRecIndicator)
        {
            _recWin?.Hide();
            return;
        }
        if (_recWin == null)
        {
            var dot = new Ellipse { Width = 9, Height = 9, Fill = B("BadBrush"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            _recText = new TextBlock { FontFamily = (FontFamily)Application.Current.Resources["FontMono"], FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = B("TextBrush"), VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 12, 6) };
            row.Children.Add(dot); row.Children.Add(_recText);
            _recWin = Make(new Border { Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x0D, 0x0C, 0x0A)), BorderBrush = B("BadBrush"), BorderThickness = new Thickness(1), Child = row });
        }
        _recText!.Text = "REC  " + elapsed;
        if (!_recWin.IsVisible) _recWin.Show();
        Place(_recWin, _toastWin is { IsVisible: true } ? 1 : 0);
    }

    public void HideAll()
    {
        _toastWin?.Hide();
        _recWin?.Hide();
    }
}
