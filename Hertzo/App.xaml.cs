using System.Text;
using System.Windows;
using System.Windows.Threading;
using Hertzo.Core;

namespace Hertzo;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Resources["GrainBrush"] = Hertzo.Ui.Textures.Grain();

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (a.ExceptionObject is Exception ex) UiHost.Log(ex); };
        TaskScheduler.UnobservedTaskException += (_, a) => { UiHost.Log(a.Exception); a.SetObserved(); };

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

        base.OnStartup(e);
    }

    private static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        UiHost.Error(e.Exception);
        e.Handled = true; // nunca derruba o app por causa de um ajuste
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { GameSession.Stop(); } catch { }
        try { Hertzo.Core.FrameCapture.Abort(); } catch { }
        try { Hertzo.Core.ReplayEngine.StopAll(); } catch { }
        base.OnExit(e);
    }
}
