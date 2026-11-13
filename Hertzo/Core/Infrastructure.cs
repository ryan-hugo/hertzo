using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using Microsoft.Win32;

namespace Hertzo.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Comando simples (sync ou async). Enquanto um comando async roda, ele fica desabilitado.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?>? _sync;
    private readonly Func<object?, Task>? _async;
    private readonly Func<object?, bool>? _can;
    private bool _running;

    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null) { _sync = run; _can = can; }
    public RelayCommand(Func<object?, Task> run, Func<object?, bool>? can = null) { _async = run; _can = can; }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => !_running && (_can?.Invoke(p) ?? true);

    public async void Execute(object? p)
    {
        if (_sync != null)
        {
            try { _sync(p); } catch (Exception ex) { UiHost.Error(ex); }
            return;
        }
        _running = true; Raise();
        try { await _async!(p); }
        catch (Exception ex) { UiHost.Error(ex); }
        finally { _running = false; Raise(); }
    }

    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public enum Hive { HKLM, HKCU }

/// <summary>Acesso ao registro sempre na visão 64 bits.</summary>
public static class Reg
{
    public static RegistryKey Base(Hive h) => RegistryKey.OpenBaseKey(
        h == Hive.HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    public static object? Get(Hive h, string path, string name)
    {
        using var b = Base(h);
        using var k = b.OpenSubKey(path);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public static RegistryValueKind? KindOf(Hive h, string path, string name)
    {
        using var b = Base(h);
        using var k = b.OpenSubKey(path);
        if (k == null || k.GetValue(name) == null) return null;
        return k.GetValueKind(name);
    }

    public static int? GetDword(Hive h, string path, string name) => Get(h, path, name) is int i ? i : null;

    public static string? GetString(Hive h, string path, string name) => Get(h, path, name) as string;

    public static void Set(Hive h, string path, string name, object value, RegistryValueKind kind)
    {
        using var b = Base(h);
        using var k = b.CreateSubKey(path, true) ?? throw new InvalidOperationException($"Não foi possível abrir {h}\\{path}");
        k.SetValue(name, value, kind);
    }

    public static void Delete(Hive h, string path, string name)
    {
        using var b = Base(h);
        using var k = b.OpenSubKey(path, true);
        k?.DeleteValue(name, false);
    }

    /// <summary>Apaga a chave se ela ficou vazia (sem valores nem subchaves) — limpeza depois de reverter.</summary>
    public static void DeleteKeyIfEmpty(Hive h, string path)
    {
        using var b = Base(h);
        using (var k = b.OpenSubKey(path))
            if (k == null || k.ValueCount > 0 || k.SubKeyCount > 0) return;
        b.DeleteSubKey(path, false);
    }

    public static string[] ValueNames(Hive h, string path)
    {
        using var b = Base(h);
        using var k = b.OpenSubKey(path);
        return k?.GetValueNames() ?? [];
    }
}

/// <summary>Executa ferramentas do próprio Windows (System32) sem abrir janelas.</summary>
public static class Shell
{
    public static string Sys(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    public static (int Code, string Output) Run(string file, string args, int timeoutMs = 60000)
    {
        Encoding enc;
        try { enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { enc = Encoding.UTF8; }

        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Falha ao iniciar {file}");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch { }
            return (-1, "tempo esgotado");
        }
        return (p.ExitCode, outTask.Result + errTask.Result);
    }

    private static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>
    /// Abre um site, pasta ou arquivo pelo Explorer que já está rodando, ou seja, SEM os privilégios de
    /// administrador do Hertzo. Sempre pelo caminho completo do explorer.exe (nunca pela pasta do Hertzo).
    /// </summary>
    public static void Open(string target)
    {
        try { Process.Start(Explorer, $"\"{target}\""); } catch (Exception ex) { UiHost.Log(ex); }
    }

    /// <summary>Abre a pasta com o arquivo selecionado.</summary>
    public static void Reveal(string file)
    {
        try { Process.Start(Explorer, $"/select,\"{file}\""); } catch (Exception ex) { UiHost.Log(ex); }
    }

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
        Open(u.AbsoluteUri);
    }
}

public static class AppPaths
{
    private static readonly object HardenGate = new();
    private static bool _hardened;

    /// <summary>%ProgramData%\Hertzo — protegida para que só administradores escrevam (ver <see cref="Guard.HardenMachineData"/>).</summary>
    public static string MachineData
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hertzo");
            if (!_hardened)
                lock (HardenGate)
                    if (!_hardened)
                    {
                        Guard.HardenMachineData(d);
                        MigrateMachineData(d);
                        _hardened = true;
                    }
            return d;
        }
    }

    private static readonly object UserGate = new();
    private static bool _userReady;

    public static string UserData
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hertzo");
            if (!_userReady)
                lock (UserGate)
                    if (!_userReady)
                    {
                        try { MigrateUserData(d); } catch { }
                        Directory.CreateDirectory(d);
                        _userReady = true;
                    }
            return d;
        }
    }

    // ---------------------------------------------------------------------
    // Migração do nome antigo (Pulso): copia, nunca apaga. As pastas antigas ficam como estavam.
    // ---------------------------------------------------------------------
    private const string LegacyName = "Pulso";

    private static void MigrateMachineData(string dir)
    {
        var journal = Path.Combine(dir, "journal.json");
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), LegacyName, "journal.json");
        // Só confia no diário antigo se ele for dos Administradores; o conteúdo ainda passa pela validação do Journal.
        if (File.Exists(journal) || !File.Exists(old) || !Guard.OwnedByAdmins(old)) return;
        File.Copy(old, journal);
    }

    private static void MigrateUserData(string dir)
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LegacyName);
        if (Directory.Exists(dir) || !Directory.Exists(old)) return;
        foreach (var src in Directory.EnumerateFiles(old, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(old, src);
            var dst = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, false);
        }
        // As medições guardam o caminho do CSV bruto: aponta para a cópia nova.
        var store = Path.Combine(dir, "medicoes.json");
        if (File.Exists(store))
        {
            static string Esc(string p) => System.Text.Json.JsonEncodedText.Encode(p + "\\").ToString();
            var text = File.ReadAllText(store);
            File.WriteAllText(store, text.Replace(Esc(old), Esc(dir), StringComparison.OrdinalIgnoreCase));
        }
    }
}

/// <summary>Avisa as telas quando algo no sistema mudou (ajuste aplicado, revertido etc.).</summary>
public static class AppEvents
{
    public static event Action? StateChanged;
    public static void RaiseStateChanged() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => StateChanged?.Invoke());
}
