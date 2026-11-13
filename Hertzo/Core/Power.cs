using System.Text.RegularExpressions;

namespace Hertzo.Core;

/// <summary>Planos de energia via powercfg.exe (ferramenta oficial). Só lê GUIDs, então funciona em qualquer idioma.</summary>
public static partial class Power
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string Saver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidRx();

    [GeneratedRegex(@"\(([^)]*)\)")]
    private static partial Regex NameRx();

    private static string Exe => Shell.Sys("powercfg.exe");

    public static string? Active()
    {
        var (_, o) = Shell.Run(Exe, "/getactivescheme");
        var m = GuidRx().Match(o);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    public static string ActiveName()
    {
        var (_, o) = Shell.Run(Exe, "/getactivescheme");
        var m = NameRx().Match(o);
        return m.Success ? m.Groups[1].Value.Trim() : "Desconhecido";
    }

    public static List<(string Guid, string Name)> List()
    {
        var (_, o) = Shell.Run(Exe, "/list");
        var list = new List<(string, string)>();
        foreach (var line in o.Split('\n'))
        {
            var g = GuidRx().Match(line);
            if (!g.Success) continue;
            var n = NameRx().Match(line);
            list.Add((g.Value.ToLowerInvariant(), n.Success ? n.Groups[1].Value.Trim() : ""));
        }
        return list;
    }

    public static bool Exists(string guid) => List().Any(x => x.Guid == guid.ToLowerInvariant());

    public static bool SetActive(string guid) => Guard.IsGuid(guid) && Shell.Run(Exe, $"/setactive {guid}").Code == 0;

    public static bool IsHertzoPlan(string guid) =>
        guid == High || guid == Ultimate || guid == Journal.GetExtra("power.high") || guid == Journal.GetExtra("power.ultimate");

    public static bool IsPerformancePlan(string? guid) => guid != null && IsHertzoPlan(guid);

    /// <summary>Garante que o plano existe (duplica o modelo oculto se necessário) e devolve o GUID utilizável.</summary>
    public static string? EnsurePlan(string baseGuid, string extraKey)
    {
        var plans = List();
        if (plans.Any(p => p.Guid == baseGuid)) return baseGuid;

        var stored = Journal.GetExtra(extraKey);
        if (stored != null && plans.Any(p => p.Guid == stored)) return stored;

        var (code, o) = Shell.Run(Exe, $"/duplicatescheme {baseGuid}");
        if (code != 0) return null;
        var m = GuidRx().Match(o);
        if (!m.Success) return null;
        var created = m.Value.ToLowerInvariant();
        Journal.SetExtra(extraKey, created);
        return created;
    }
}

public static partial class Power
{
    public static Guid? ActiveGuid() => PowerApi.Active();
}
