using System.Management;
using Microsoft.Win32;

namespace Hertzo.Core;

public static class RestorePoint
{
    private const string SrKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

    /// <summary>Cria um ponto de restauração do Windows. Contorna temporariamente o limite de 1 por 24h e restaura a configuração original.</summary>
    public static (bool Ok, string Message) Create(string description)
    {
        var prev = Reg.Get(Hive.HKLM, SrKey, "SystemRestorePointCreationFrequency");
        try
        {
            Reg.Set(Hive.HKLM, SrKey, "SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var inP = cls.GetMethodParameters("CreateRestorePoint");
            inP["Description"] = description;
            inP["RestorePointType"] = 12;  // MODIFY_SETTINGS
            inP["EventType"] = 100;        // BEGIN_SYSTEM_CHANGE
            using var outP = cls.InvokeMethod("CreateRestorePoint", inP, null);
            var rv = Convert.ToUInt32(outP["ReturnValue"]);
            if (rv == 0)
            {
                Journal.Log("Ponto de restauração criado", "info");
                return (true, "Ponto de restauração criado.");
            }
            return (false, rv == 1058 || rv == 0x80070422
                ? "A Proteção do Sistema está desativada neste PC."
                : $"O Windows não criou o ponto de restauração (código {rv}). A Proteção do Sistema pode estar desligada no disco C:.");
        }
        catch (Exception ex)
        {
            return (false, "Não foi possível criar o ponto de restauração: " + ex.Message);
        }
        finally
        {
            try
            {
                if (prev is int p) Reg.Set(Hive.HKLM, SrKey, "SystemRestorePointCreationFrequency", p, RegistryValueKind.DWord);
                else Reg.Delete(Hive.HKLM, SrKey, "SystemRestorePointCreationFrequency");
            }
            catch { }
        }
    }

    public static (bool Ok, string Message) EnableProtection()
    {
        try
        {
            var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var inP = cls.GetMethodParameters("Enable");
            inP["Drive"] = drive;
            using var outP = cls.InvokeMethod("Enable", inP, null);
            var rv = Convert.ToUInt32(outP["ReturnValue"]);
            if (rv == 0) Journal.Log($"Proteção do Sistema ativada em {drive}", "info");
            return (rv == 0, rv == 0 ? "Proteção do Sistema ativada." : $"Falha ao ativar (código {rv}).");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}

public static class SystemActions
{
    /// <summary>Esvazia a lista de memória “em espera” (o mesmo que o ISLC/RAMMap fazem). Não mexe na memória dos programas.</summary>
    public static bool PurgeStandbyList()
    {
        Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
        int cmd = 4; // MemoryPurgeStandbyList
        return Native.NtSetSystemInformation(80, ref cmd, sizeof(int)) == 0;
    }

    public static bool FlushDns()
    {
        try { return Native.DnsFlushResolverCache() != 0; } catch { return false; }
    }
}

/// <summary>Sessão de jogo: liga o Alto Desempenho e libera a memória em espera; ao terminar, devolve o plano anterior.</summary>
public static class GameSession
{
    private static string? _previous;
    public static bool Active { get; private set; }

    public static string Start()
    {
        var notes = new List<string>();
        _previous = Power.Active();
        if (!Power.IsPerformancePlan(_previous))
        {
            var plan = Power.EnsurePlan(Power.High, "power.high");
            if (plan != null && Power.SetActive(plan))
            {
                notes.Add("Alto Desempenho ativo");
                // Guardado no diário: se o Hertzo fechar sem encerrar a sessão (queda, desligamento), o plano volta na próxima abertura.
                if (_previous != null) Journal.SetExtra("session.previous", _previous);
            }
        }
        else notes.Add("plano de energia já otimizado");
        if (SystemActions.PurgeStandbyList()) notes.Add("memória em espera liberada");
        Active = true;
        Journal.Log("Sessão de jogo iniciada", "apply");
        return string.Join(" · ", notes);
    }

    public static void Stop()
    {
        if (!Active) return;
        if (_previous != null && Power.Active() != _previous) Power.SetActive(_previous);
        Journal.SetExtra("session.previous", null);
        Active = false;
        Journal.Log("Sessão de jogo encerrada", "revert");
    }

    /// <summary>Na abertura: se a última sessão de jogo não foi encerrada, devolve o plano de energia de antes.</summary>
    public static void RecoverIfNeeded()
    {
        var prev = Journal.GetExtra("session.previous");
        if (prev == null) return;
        if (Power.Exists(prev) && Power.Active() != prev && Power.SetActive(prev))
            Journal.Log("Sessão de jogo anterior não foi encerrada: plano de energia devolvido", "revert");
        Journal.SetExtra("session.previous", null);
    }
}

// ======================= LIMPEZA =======================

public sealed class CleanTarget : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string[] Paths { get; init; } = [];
    /// <summary>Arquivos soltos (ex.: MEMORY.DMP).</summary>
    public string[] Files { get; init; } = [];
    public string? Warning { get; init; }
    public bool IsRecycleBin { get; init; }

    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private long _size = -1;
    public long Size { get => _size; set { Set(ref _size, value); OnPropertyChanged(nameof(SizeLabel)); } }
    public string SizeLabel => Size < 0 ? "—" : SystemInfo.Bytes(Size);
    public bool HasWarning => Warning != null;
}

public static class Cleaner
{
    public static List<CleanTarget> Targets()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        return
        [
            new CleanTarget { Name = "Arquivos temporários do usuário", Description = "Sobras de instaladores e programas (%TEMP%). Arquivos em uso são ignorados.", Paths = [Path.GetTempPath()], Selected = true },
            new CleanTarget { Name = "Temporários do Windows", Description = "Pasta Windows\\Temp.", Paths = [Path.Combine(win, "Temp")], Selected = true },
            new CleanTarget { Name = "Relatórios de erro", Description = "Relatórios do Windows Error Reporting e despejos de falha de apps.", Paths = [Path.Combine(pd, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(pd, @"Microsoft\Windows\WER\ReportQueue"), Path.Combine(local, "CrashDumps")], Selected = true },
            new CleanTarget { Name = "Cache de shaders do DirectX", Description = "Shaders compilados pelo Windows. Limpar ajuda após atualizar o driver de vídeo.", Paths = [Path.Combine(local, "D3DSCache")], Warning = "Na próxima abertura, os jogos recompilam shaders e podem travar um pouco nos primeiros minutos." },
            new CleanTarget { Name = "Cache de shaders NVIDIA", Description = "DXCache/GLCache do driver NVIDIA. Recomendado depois de trocar de driver.", Paths = [Path.Combine(local, @"NVIDIA\DXCache"), Path.Combine(local, @"NVIDIA\GLCache"), Path.Combine(localLow, @"NVIDIA\PerDriverVersion\DXCache")], Warning = "Os jogos recompilam shaders na próxima abertura." },
            new CleanTarget { Name = "Cache de shaders AMD", Description = "DxCache/VkCache do driver AMD. Recomendado depois de trocar de driver.", Paths = [Path.Combine(local, @"AMD\DxCache"), Path.Combine(local, @"AMD\DxcCache"), Path.Combine(local, @"AMD\VkCache")], Warning = "Os jogos recompilam shaders na próxima abertura." },
            new CleanTarget { Name = "Despejos de memória de telas azuis", Description = "MEMORY.DMP e Minidump: cópias da memória gravadas quando o Windows trava com tela azul. Podem ocupar vários GB.", Paths = [Path.Combine(win, "Minidump")], Files = [Path.Combine(win, "MEMORY.DMP")], Warning = "Se você está investigando telas azuis com alguém, guarde-os antes: são a principal pista do que causou o travamento." },
            new CleanTarget { Name = "Lixeira", Description = "Esvazia a Lixeira de todos os discos.", IsRecycleBin = true, Warning = "Exclusão permanente: não dá para recuperar depois." },
        ];
    }

    public static long Measure(CleanTarget t)
    {
        if (t.IsRecycleBin)
        {
            var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
            return Native.SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
        }
        long total = 0;
        foreach (var p in t.Paths) total += Walk(p, delete: false).Bytes;
        foreach (var f in t.Files)
            try { var fi = new FileInfo(f); if (fi.Exists && !fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) total += fi.Length; } catch { }
        return total;
    }

    public static long Clean(CleanTarget t)
    {
        if (t.IsRecycleBin)
        {
            var before = Measure(t);
            Native.SHEmptyRecycleBin(IntPtr.Zero, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
            return before;
        }
        long freed = 0;
        foreach (var p in t.Paths) freed += Walk(p, delete: true).Bytes;
        foreach (var f in t.Files)
            try
            {
                var fi = new FileInfo(f);
                if (!fi.Exists || fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var len = fi.Length;
                var dir = Guard.FinalDirectory(fi.DirectoryName!);
                if (dir != null && Guard.SafeDelete(f, dir)) freed += len;
            }
            catch { }
        return freed;
    }

    /// <summary>
    /// Percorre a pasta sem seguir junções/links simbólicos. Na exclusão, cada arquivo é conferido pelo caminho
    /// real no momento de apagar (<see cref="Guard.SafeDelete"/>), então nada fora da pasta alvo é apagado.
    /// </summary>
    private static (long Bytes, int Files) Walk(string root, bool delete)
    {
        long bytes = 0; int files = 0;
        if (!Directory.Exists(root)) return (0, 0);
        if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint)) return (0, 0);
        var rootFinal = Guard.FinalDirectory(root);
        if (rootFinal == null) return (0, 0);
        var stack = new Stack<string>();
        stack.Push(root);
        var dirs = new List<string>();
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        var len = fi.Length;
                        if (delete && !Guard.SafeDelete(f, rootFinal)) continue;
                        bytes += len; files++;
                    }
                    catch { /* em uso ou sem permissão: ignora */ }
                }
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        stack.Push(d);
                        dirs.Add(d);
                    }
                    catch { }
                }
            }
            catch { }
        }
        if (delete)
            foreach (var d in dirs.OrderByDescending(x => x.Length))
                try { Directory.Delete(d, false); } catch { }
        return (bytes, files);
    }
}

// ======================= INICIALIZAÇÃO =======================

public sealed class StartupItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required string Source { get; init; }
    public required Hive ApprovedHive { get; init; }
    public required string ApprovedPath { get; init; }
    public required string ApprovedName { get; init; }
    public string Publisher { get; init; } = "";
    public string Initials => new string(Name.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();

    private static readonly string[] Keep =
        ["Vanguard", "SecurityHealth", "Realtek", "RtkAud", "FACEIT", "EasyAntiCheat", "BattlEye", "BEService", "Windows Defender", "Waves"];

    /// <summary>Itens que não devem ser desligados (anti-cheat, segurança, áudio).</summary>
    public bool KeepOn => Keep.Any(k => Name.Contains(k, StringComparison.OrdinalIgnoreCase) || Command.Contains(k, StringComparison.OrdinalIgnoreCase));

    private bool _enabled;
    public bool Enabled { get => _enabled; set { _enabled = value; OnPropertyChanged(); } }
}

/// <summary>Usa o mesmo mecanismo do Gerenciador de Tarefas (StartupApproved), então é 100% reversível.</summary>
public static class StartupManager
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static List<StartupItem> Read()
    {
        var list = new List<StartupItem>();
        AddRun(list, Hive.HKCU, Run, Approved + @"\Run", "Usuário");
        AddRun(list, Hive.HKLM, Run, Approved + @"\Run", "Todos os usuários");
        AddRun(list, Hive.HKLM, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", Approved + @"\Run32", "Todos os usuários (32 bits)");
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Hive.HKCU, "Pasta Inicializar");
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Hive.HKLM, "Pasta Inicializar (todos)");
        return list.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void SetEnabled(StartupItem item, bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        Reg.Set(item.ApprovedHive, item.ApprovedPath, item.ApprovedName, data, RegistryValueKind.Binary);
        item.Enabled = enabled;
        Journal.Log($"{(enabled ? "Ativado" : "Desativado")} na inicialização: {item.Name}", enabled ? "revert" : "apply");
    }

    private static bool IsEnabled(Hive h, string path, string name)
        => Reg.Get(h, path, name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;

    private static void AddRun(List<StartupItem> list, Hive h, string runPath, string approvedPath, string source)
    {
        try
        {
            using var b = Reg.Base(h);
            using var k = b.OpenSubKey(runPath);
            if (k == null) return;
            foreach (var name in k.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                var cmd = k.GetValue(name)?.ToString() ?? "";
                list.Add(new StartupItem
                {
                    Name = name, Command = cmd, Source = source,
                    ApprovedHive = h, ApprovedPath = approvedPath, ApprovedName = name,
                    Publisher = PublisherOf(cmd),
                    Enabled = IsEnabled(h, approvedPath, name),
                });
            }
        }
        catch { }
    }

    private static void AddFolder(List<StartupItem> list, string folder, Hive h, string source)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.EnumerateFiles(folder))
            {
                var file = Path.GetFileName(f);
                if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(f), Command = f, Source = source,
                    ApprovedHive = h, ApprovedPath = Approved + @"\StartupFolder", ApprovedName = file,
                    Enabled = IsEnabled(h, Approved + @"\StartupFolder", file),
                });
            }
        }
        catch { }
    }

    private static string PublisherOf(string cmd)
    {
        try
        {
            var exe = ExtractExe(cmd);
            if (exe == null || !File.Exists(exe)) return "";
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            return vi.CompanyName?.Trim() ?? "";
        }
        catch { return ""; }
    }

    private static string? ExtractExe(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
        var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? cmd[..(i + 4)] : cmd.Split(' ')[0];
    }
}
