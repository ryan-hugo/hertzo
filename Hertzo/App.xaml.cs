using System.Text;
using System.Windows;
using System.Windows.Threading;
using Hertzo.Core;

namespace Hertzo;

public partial class App : Application
{
    /// <summary>Aberto com --segundo-plano: só o ícone na bandeja, sem janela.</summary>
    public bool StartInBackground { get; init; }

    /// <summary>Saindo de verdade (menu da bandeja, fim de sessão do Windows): a janela não vai para a bandeja.</summary>
    public static bool Exiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Resources["GrainBrush"] = Hertzo.Ui.Textures.Grain();

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (a.ExceptionObject is Exception ex) UiHost.Log(ex); };
        TaskScheduler.UnobservedTaskException += (_, a) => { UiHost.Log(a.Exception); a.SetObserved(); };
        SessionEnding += (_, _) => Exiting = true;

        // Protege %ProgramData%\Hertzo antes de qualquer leitura do diário ou execução do PresentMon.
        try { _ = AppPaths.MachineData; }
        catch (Exception ex)
        {
            UiHost.Log(ex);
            MessageBox.Show("O Hertzo não conseguiu proteger a pasta de backup (%ProgramData%\\Hertzo) e vai fechar por segurança.\n\n" + ex.Message,
                "Hertzo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        Task.Run(() => { try { GameSession.RecoverIfNeeded(); } catch (Exception ex) { UiHost.Log(ex); } });
        // se o início automático estiver ligado e este executável for mais novo, atualiza a cópia instalada
        Task.Run(() => { try { AutoStart.RefreshInstalledCopy(); } catch (Exception ex) { UiHost.Log(ex); } });

        base.OnStartup(e);

        var w = new MainWindow();
        MainWindow = w;
        Hertzo.Ui.Tray.Init(w);
        if (!StartInBackground) w.Show();
    }

    /// <summary>Traz a janela de volta (bandeja ou segunda abertura do Hertzo).</summary>
    public void ShowMainWindow()
    {
        if (MainWindow is not { } w) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true; w.Topmost = false; // garante que vem para frente
    }

    /// <summary>Fecha o Hertzo de verdade (para replay e gravação, remove o ícone da bandeja).</summary>
    public static void ExitApp()
    {
        Exiting = true;
        Current.Shutdown();
    }

    private static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        UiHost.Error(e.Exception);
        e.Handled = true; // nunca derruba o app por causa de um ajuste
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Exiting = true;
        try { Hertzo.Ui.Tray.Dispose(); } catch { }
        try { GameSession.Stop(); } catch { }
        try { Hertzo.Core.FrameCapture.Abort(); } catch { }
        try { Hertzo.Core.ReplayEngine.StopAll(); } catch { }
        base.OnExit(e);
    }
}
