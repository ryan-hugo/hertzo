using Microsoft.Win32;

namespace Hertzo.Core;

public enum Tone { Ok, Warn, Bad, Info, Neutral, Accent }
public enum Risk { Seguro, Moderado, Avancado }
public enum Impact { Alto, Medio, Baixo, Situacional }
public enum Evidence { Forte, Moderada, Fraca }

/// <summary>
/// Um ajuste do sistema. Todo ajuste sabe: detectar o estado atual, aplicar com backup e reverter.
/// Também carrega rótulos honestos de impacto, risco e nível de evidência.
/// </summary>
public abstract class Tweak : ObservableObject
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public string? Warning { get; init; }
    public Risk Risk { get; init; } = Risk.Seguro;
    public Impact Impact { get; init; } = Impact.Baixo;
    public Evidence Evidence { get; init; } = Evidence.Moderada;
    public bool RequiresReboot { get; init; }
    public bool Recommended { get; init; }
    public Func<bool>? Availability { get; init; }

    private bool _applied;
    public bool IsApplied { get => _applied; private set { _applied = value; OnPropertyChanged(); } }

    private bool _busy;
    public bool IsBusy { get => _busy; set => Set(ref _busy, value); }

    public bool IsAdvanced => Risk == Risk.Avancado;
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);

    public string ImpactLabel => Impact switch
    {
        Impact.Alto => "IMPACTO ALTO",
        Impact.Medio => "IMPACTO MÉDIO",
        Impact.Baixo => "IMPACTO BAIXO",
        _ => "SITUACIONAL",
    };
    public Tone ImpactTone => Impact switch { Impact.Alto => Tone.Accent, Impact.Medio => Tone.Info, _ => Tone.Neutral };

    public string RiskLabel => Risk switch { Risk.Seguro => "SEGURO", Risk.Moderado => "MODERADO", _ => "AVANÇADO" };
    public Tone RiskTone => Risk switch { Risk.Seguro => Tone.Ok, Risk.Moderado => Tone.Warn, _ => Tone.Bad };

    public string EvidenceLabel => Evidence switch
    {
        Evidence.Forte => "EVIDÊNCIA FORTE",
        Evidence.Moderada => "EVIDÊNCIA MODERADA",
        _ => "EVIDÊNCIA FRACA",
    };
    public Tone EvidenceTone => Evidence switch { Evidence.Forte => Tone.Ok, Evidence.Moderada => Tone.Info, _ => Tone.Neutral };

    public bool IsAvailable
    {
        get { try { return Availability?.Invoke() ?? true; } catch { return false; } }
    }

    public void Refresh()
    {
        bool v;
        try { v = Detect(); } catch { v = false; }
        IsApplied = v; // sempre notifica, para o interruptor voltar ao estado real se algo falhar
    }

    public abstract bool Detect();
    protected abstract void ApplyCore();
    protected abstract void RevertCore();

    /// <summary>Valores de registro que este ajuste escreve (usado para validar o diário de backup).</summary>
    public virtual bool Touches(Hive hive, string path, string name) => false;

    /// <summary>O Hertzo guardou o estado anterior deste ajuste (então “Desfazer tudo” deve revertê-lo).</summary>
    public virtual bool HasHertzoBackup => Journal.HasBackup(Id);

    protected static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Apply()
    {
        ApplyCore();
        Journal.Log($"Aplicado: {Title}", "apply");
        Refresh();
    }

    public void Revert()
    {
        RevertCore();
        Journal.Log($"Revertido: {Title}", "revert");
        Refresh();
    }
}

/// <summary>Uma escrita de registro. Default = valor padrão do Windows (null = valor não existe por padrão).</summary>
public sealed record RegOp(Hive Hive, string Path, string Name, RegistryValueKind Kind, object Target, object? Default = null, bool MissingMatches = false)
{
    public bool Matches(object? current)
    {
        if (current is null) return MissingMatches;
        return Kind switch
        {
            RegistryValueKind.DWord => current is int i && i == Convert.ToInt32(Target),
            RegistryValueKind.QWord => current is long l && l == Convert.ToInt64(Target),
            _ => string.Equals(current.ToString(), Target.ToString(), StringComparison.OrdinalIgnoreCase),
        };
    }
}

public class RegistryTweak : Tweak
{
    public required RegOp[] Ops { get; init; }
    public Action? AfterChange { get; init; }

    public override bool Detect() => Ops.All(o => o.Matches(Reg.Get(o.Hive, o.Path, o.Name)));

    public override bool Touches(Hive hive, string path, string name)
        => Ops.Any(o => o.Hive == hive && Same(o.Path, path) && Same(o.Name, name));

    /// <summary>Chaves que o ajuste criou e que devem sumir se ficarem vazias ao reverter (da mais funda para a mais rasa).</summary>
    public (Hive Hive, string Path)[] CleanupKeys { get; init; } = [];

    protected override void ApplyCore()
    {
        foreach (var o in Ops)
        {
            Journal.Backup(Id, o.Hive, o.Path, o.Name);
            Reg.Set(o.Hive, o.Path, o.Name, o.Target, o.Kind);
        }
        AfterChange?.Invoke();
    }

    protected override void RevertCore()
    {
        if (Journal.HasBackup(Id)) Journal.RestoreOwner(Id);
        // Se o valor original já era igual ao alvo (ex.: outro programa aplicou antes), volta ao padrão do Windows.
        if (Detect()) ApplyDefaults();
        foreach (var (h, p) in CleanupKeys)
            try { Reg.DeleteKeyIfEmpty(h, p); } catch { }
        AfterChange?.Invoke();
    }

    private void ApplyDefaults()
    {
        foreach (var o in Ops)
        {
            if (o.Default is null) Reg.Delete(o.Hive, o.Path, o.Name);
            else Reg.Set(o.Hive, o.Path, o.Name, o.Default, o.Kind);
        }
    }
}

/// <summary>Valores do tipo "Chave=Valor;Chave2=Valor2;" (ex.: DirectXUserGlobalSettings, GpuPreference).</summary>
public sealed class TokenTweak : Tweak
{
    public required Hive Hive { get; init; }
    public required string Path { get; init; }
    public required string ValueName { get; init; }
    public required string TokenKey { get; init; }
    public required string TokenValue { get; init; }

    private static Dictionary<string, string> Parse(string? s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(s)) return d;
        foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.IndexOf('=');
            if (i > 0) d[part[..i]] = part[(i + 1)..];
        }
        return d;
    }

    private static string Join(Dictionary<string, string> d) => string.Concat(d.Select(kv => $"{kv.Key}={kv.Value};"));

    public override bool Detect()
        => Parse(Reg.GetString(Hive, Path, ValueName)).TryGetValue(TokenKey, out var v) && v == TokenValue;

    public override bool Touches(Hive hive, string path, string name)
        => hive == Hive && Same(path, Path) && Same(name, ValueName);

    protected override void ApplyCore()
    {
        Journal.Backup(Id, Hive, Path, ValueName);
        var d = Parse(Reg.GetString(Hive, Path, ValueName));
        d[TokenKey] = TokenValue;
        Reg.Set(Hive, Path, ValueName, Join(d), RegistryValueKind.String);
    }

    protected override void RevertCore()
    {
        var backup = Journal.GetBackup(Id, Hive, Path, ValueName);
        var d = Parse(Reg.GetString(Hive, Path, ValueName));
        d.Remove(TokenKey);
        if (backup is { Existed: true } && Parse(backup.Data).TryGetValue(TokenKey, out var orig) && orig != TokenValue)
            d[TokenKey] = orig;
        if (d.Count == 0) Reg.Delete(Hive, Path, ValueName);
        else Reg.Set(Hive, Path, ValueName, Join(d), RegistryValueKind.String);
        Journal.RemoveOwner(Id);
    }
}

/// <summary>Flags de compatibilidade por executável (AppCompatFlags\Layers), ex.: desativar otimizações de tela cheia.</summary>
public sealed class LayerTweak : Tweak
{
    public const string LayersPath = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    public required string ExePath { get; init; }
    public required string Flag { get; init; }

    private List<string> Tokens() =>
        (Reg.GetString(Hive.HKCU, LayersPath, ExePath) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    public override bool Detect() => Tokens().Contains(Flag, StringComparer.OrdinalIgnoreCase);

    protected override void ApplyCore()
    {
        Journal.Backup(Id, Hive.HKCU, LayersPath, ExePath);
        var t = Tokens().Where(x => x != "~").ToList();
        if (!t.Contains(Flag, StringComparer.OrdinalIgnoreCase)) t.Add(Flag);
        Reg.Set(Hive.HKCU, LayersPath, ExePath, "~ " + string.Join(' ', t), RegistryValueKind.String);
    }

    protected override void RevertCore()
    {
        var t = Tokens().Where(x => x != "~" && !x.Equals(Flag, StringComparison.OrdinalIgnoreCase)).ToList();
        if (t.Count == 0) Reg.Delete(Hive.HKCU, LayersPath, ExePath);
        else Reg.Set(Hive.HKCU, LayersPath, ExePath, "~ " + string.Join(' ', t), RegistryValueKind.String);
        Journal.RemoveOwner(Id);
    }
}

public sealed class PowerPlanTweak : Tweak
{
    public required string BaseGuid { get; init; }
    public required string ExtraKey { get; init; }

    public override bool Detect()
    {
        var a = Power.Active();
        return a != null && (a == BaseGuid || a == Journal.GetExtra(ExtraKey));
    }

    public override bool HasHertzoBackup => IsApplied && Journal.GetExtra("power.previous") != null;

    protected override void ApplyCore()
    {
        var active = Power.Active();
        if (Journal.GetExtra("power.previous") == null && active != null && !Power.IsHertzoPlan(active))
            Journal.SetExtra("power.previous", active);

        var target = Power.EnsurePlan(BaseGuid, ExtraKey)
            ?? throw new InvalidOperationException("Este PC não permite criar esse plano de energia (comum em notebooks com Modern Standby).");
        if (!Power.SetActive(target)) throw new InvalidOperationException("O Windows recusou ativar o plano de energia.");
    }

    protected override void RevertCore()
    {
        var prev = Journal.GetExtra("power.previous");
        if (prev == null || !Power.Exists(prev)) prev = Power.Balanced;
        Power.SetActive(prev);
        Journal.SetExtra("power.previous", null);
    }
}

public sealed class ServiceTweak : Tweak
{
    public required string ServiceName { get; init; }
    public int TargetStart { get; init; } = 4; // 4 = desativado, 3 = manual

    private string KeyPath => $@"SYSTEM\CurrentControlSet\Services\{ServiceName}";
    public bool ServiceExists => Reg.Get(Hive.HKLM, KeyPath, "Start") != null;

    private static string Map(int start) => start switch { 0 => "boot", 1 => "system", 2 => "auto", 3 => "demand", _ => "disabled" };

    public override bool Detect() => Reg.GetDword(Hive.HKLM, KeyPath, "Start") == TargetStart;

    public override bool Touches(Hive hive, string path, string name)
        => hive == Hive.HKLM && Same(path, KeyPath) && Same(name, "Start");

    protected override void ApplyCore()
    {
        Journal.Backup(Id, Hive.HKLM, KeyPath, "Start");
        Shell.Run(Shell.Sys("sc.exe"), $"config {ServiceName} start= {Map(TargetStart)}");
        if (TargetStart == 4) Shell.Run(Shell.Sys("sc.exe"), $"stop {ServiceName}");
    }

    protected override void RevertCore()
    {
        var b = Journal.GetBackup(Id, Hive.HKLM, KeyPath, "Start");
        var orig = b is { Existed: true } && int.TryParse(b.Data, out var s) ? s : 2;
        Shell.Run(Shell.Sys("sc.exe"), $"config {ServiceName} start= {Map(orig)}");
        if (orig == 2) Shell.Run(Shell.Sys("sc.exe"), $"start {ServiceName}");
        Journal.RemoveOwner(Id);
    }
}

/// <summary>
/// Uma configuração do plano de energia ATIVO (valor “na tomada”), pela API oficial do Windows (powrprof).
/// O valor original é guardado por plano no diário, então reverter devolve exatamente o que estava antes.
/// </summary>
public sealed class PowerSettingTweak : Tweak
{
    public required Guid Subgroup { get; init; }
    public required Guid Setting { get; init; }
    public required uint Target { get; init; }
    public required uint WindowsDefault { get; init; }

    private string Key => "pset." + Id;

    public static bool IsAvailableHere(Guid sub, Guid setting)
        => Power.ActiveGuid() is { } s && PowerApi.ReadAc(s, sub, setting) != null;

    public override bool Detect()
        => Power.ActiveGuid() is { } s && PowerApi.ReadAc(s, Subgroup, Setting) == Target;

    public override bool HasHertzoBackup => Journal.GetExtra(Key) != null;

    /// <summary>Formato do backup: "guid=valor;guid=valor".</summary>
    public static bool IsValidBackup(string v)
        => v.Split(';', StringSplitOptions.RemoveEmptyEntries).All(p =>
        {
            var kv = p.Split('=');
            return kv.Length == 2 && Guard.IsGuid(kv[0]) && uint.TryParse(kv[1], out _);
        });

    private Dictionary<Guid, uint> Backups()
    {
        var d = new Dictionary<Guid, uint>();
        var raw = Journal.GetExtra(Key);
        if (raw == null || !IsValidBackup(raw)) return d;
        foreach (var p in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = p.Split('=');
            d[Guid.Parse(kv[0])] = uint.Parse(kv[1]);
        }
        return d;
    }

    private void SaveBackups(Dictionary<Guid, uint> d)
        => Journal.SetExtra(Key, d.Count == 0 ? null : string.Join(';', d.Select(kv => $"{kv.Key:D}={kv.Value}")));

    protected override void ApplyCore()
    {
        var s = Power.ActiveGuid() ?? throw new InvalidOperationException("Não foi possível ler o plano de energia ativo.");
        var cur = PowerApi.ReadAc(s, Subgroup, Setting) ?? throw new InvalidOperationException("Esta configuração de energia não existe neste PC.");
        var b = Backups();
        if (!b.ContainsKey(s)) { b[s] = cur; SaveBackups(b); }
        PowerApi.WriteAc(s, Subgroup, Setting, Target);
    }

    protected override void RevertCore()
    {
        var b = Backups();
        var active = Power.ActiveGuid();
        foreach (var (scheme, value) in b)
            try { PowerApi.WriteAc(scheme, Subgroup, Setting, value); } catch { /* plano apagado */ }
        if (active is { } a && !b.ContainsKey(a) && PowerApi.ReadAc(a, Subgroup, Setting) == Target)
            PowerApi.WriteAc(a, Subgroup, Setting, WindowsDefault);
        SaveBackups([]);
    }
}

/// <summary>powrprof.dll: lê e grava valores do plano de energia sem depender do texto (traduzido) do powercfg.</summary>
public static class PowerApi
{
    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr p);

    public static Guid? Active()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return null;
        try { return System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    public static uint? ReadAc(Guid scheme, Guid sub, Guid setting)
        => PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var v) == 0 ? v : null;

    public static void WriteAc(Guid scheme, Guid sub, Guid setting, uint value)
    {
        var rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (rc != 0) throw new InvalidOperationException($"O Windows recusou alterar a configuração de energia (código {rc}).");
        // reaplica o plano ativo para o valor valer na hora
        if (Active() is { } a && a == scheme) PowerSetActiveScheme(IntPtr.Zero, ref scheme);
    }
}
