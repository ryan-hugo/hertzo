using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Hertzo.Core;
using Hertzo.ViewModels;

namespace Hertzo;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.InitAsync();
        SourceInitialized += (_, _) => StyleFrame();
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        PreviewKeyDown += OnKey;
    }

    private void StyleFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2, border = 0x002D2622; // COLORREF (BGR) de #22262D
        Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // modo escuro
        Native.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));  // cantos arredondados (Win11)
        Native.DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int)); // cor da borda (Win11)
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_vm.HasDialog) return;
        if (e.Key == Key.Escape) { _vm.DialogCancel.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { _vm.DialogConfirm.Execute(null); e.Handled = true; }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
