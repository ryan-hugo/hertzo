using System.Diagnostics;
using System.Runtime.CompilerServices;
using Hertzo.Core;

namespace Hertzo;

/// <summary>
/// Ponto de entrada. Antes de tocar no WPF:
/// 1. Extração segura: o Hertzo é um executável único que extrai as DLLs nativas do WPF para uma pasta. Por padrão
///    essa pasta fica em %TEMP%, que qualquer programa sem admin pode alterar — e o Hertzo roda como admin. Por isso
///    ele se reabre com DOTNET_BUNDLE_EXTRACT_BASE_DIR apontando para %ProgramData%\Hertzo\runtime (só administradores).
/// 2. Instância única: abrir de novo só traz a janela que já está aberta (nada de dois Hertzo disputando atalhos e
///    configurações).
/// 3. --segundo-plano: abre só na bandeja (usado pelo início automático com o Windows).
/// </summary>
public static class Program
{
    public const string BackgroundArg = "--segundo-plano";
    private const string ExtractVar = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";
    private const string MutexName = @"Local\Hertzo.Instancia";
    private const string ShowEventName = @"Local\Hertzo.Mostrar";

    public static string SafeRuntimeDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hertzo", "runtime");

    private static Mutex? _instance;
    private static EventWaitHandle? _showSignal;

    [STAThread]
    public static int Main(string[] args)
    {
        bool background = args.Any(a => a.Equals(BackgroundArg, StringComparison.OrdinalIgnoreCase) || a.Equals("--background", StringComparison.OrdinalIgnoreCase));

        // linha de comando (sem janela): ligar/desligar o início automático com o Windows
        if (args.Any(a => a.Equals("--autostart-on", StringComparison.OrdinalIgnoreCase))) return Cli(AutoStart.Enable);
        if (args.Any(a => a.Equals("--autostart-off", StringComparison.OrdinalIgnoreCase))) return Cli(AutoStart.Disable);

        // 1) extração segura (só no executável único publicado; em "dotnet run" não há extração)
        if (IsSingleFileBundle && !UsingSafeRuntimeDir())
            return Relaunch(args);

        // 2) instância única
        _instance = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            if (!background)
                try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            return 0;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        try { return RunApp(background); }
        finally { _instance.ReleaseMutex(); }
    }

    private static int Cli(Action action)
    {
        try { _ = AppPaths.MachineData; action(); return 0; }
        catch (Exception ex) { UiHost.Log(ex); return 1; }
    }

    private static bool IsSingleFileBundle => string.IsNullOrEmpty(typeof(Program).Assembly.Location);

    private static bool UsingSafeRuntimeDir()
    {
        var v = Environment.GetEnvironmentVariable(ExtractVar);
        return !string.IsNullOrEmpty(v) && string.Equals(Path.GetFullPath(v).TrimEnd('\\'), SafeRuntimeDir, StringComparison.OrdinalIgnoreCase);
    }

    private static int Relaunch(string[] args)
    {
        // Protege %ProgramData%\Hertzo (e apaga o "runtime" se a pasta ainda não era protegida) antes de o .NET extrair nela.
        try { _ = AppPaths.MachineData; } catch { /* o processo novo mostra o erro e fecha */ }
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment[ExtractVar] = SafeRuntimeDir;
        using var p = Process.Start(psi);
        return 0;
    }

    /// <summary>Separado do Main para que nenhum tipo do WPF seja carregado antes da verificação acima.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp(bool background)
    {
        var app = new App { StartInBackground = background };
        app.InitializeComponent();
        // pedido de "mostrar" vindo de uma segunda abertura do Hertzo
        var signal = _showSignal!;
        new Thread(() =>
        {
            while (signal.WaitOne())
                app.Dispatcher.BeginInvoke(app.ShowMainWindow);
        }) { IsBackground = true, Name = "Hertzo instancia" }.Start();
        return app.Run();
    }
}
