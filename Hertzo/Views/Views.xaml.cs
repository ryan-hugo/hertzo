using System.Windows.Controls;

namespace Hertzo.Views;

public partial class DashboardView : UserControl { public DashboardView() => InitializeComponent(); }
public partial class CheckupView : UserControl { public CheckupView() => InitializeComponent(); }
public partial class TweaksView : UserControl { public TweaksView() => InitializeComponent(); }
public partial class GamesView : UserControl { public GamesView() => InitializeComponent(); }
public partial class BenchView : UserControl { public BenchView() => InitializeComponent(); }
public partial class ClipsView : UserControl
{
    public ClipsView() => InitializeComponent();

    // escolha de atalho: captura a combinação apertada enquanto o usuário está mudando um atalho
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not Hertzo.ViewModels.ClipsViewModel { IsCapturingKey: true } vm) return;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin) return;
        var mods = (uint)System.Windows.Input.Keyboard.Modifiers; // Alt=1, Ctrl=2, Shift=4, Win=8 (iguais aos do Windows)
        vm.CaptureKey(mods, (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key), key == System.Windows.Input.Key.Escape && mods == 0);
    }
}
public partial class CleanupView : UserControl { public CleanupView() => InitializeComponent(); }
public partial class StartupView : UserControl { public StartupView() => InitializeComponent(); }
public partial class BackupView : UserControl { public BackupView() => InitializeComponent(); }
public partial class TransparencyView : UserControl { public TransparencyView() => InitializeComponent(); }
