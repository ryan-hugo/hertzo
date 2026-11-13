using System.Text.Json;
using Microsoft.Win32;

namespace Hertzo.Core;

/// <summary>Cópia do valor ORIGINAL de registro antes do Hertzo mexer nele.</summary>
public sealed class RegBackup
{
    public string Owner { get; set; } = "";
    public Hive Hive { get; set; }
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Existed { get; set; }
    public RegistryValueKind Kind { get; set; }
    public string? Data { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
}

public sealed class HistoryEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "info"; // apply | revert | info | clean
    public string TimeLabel => Time.ToString("dd/MM HH:mm");
}

/// <summary>
/// Diário de alterações persistido em %ProgramData%\Hertzo\journal.json.
/// Toda escrita de registro passa por aqui antes, para que qualquer ajuste possa ser desfeito
/// exatamente para o estado anterior — mesmo depois de reiniciar o PC ou reinstalar o Hertzo.
/// </summary>
public static class Journal
{
    private sealed class Data
    {
        public List<RegBackup> Registry { get; set; } = [];
        public List<HistoryEntry> History { get; set; } = [];
        public Dictionary<string, string> Extra { get; set; } = [];
    }

    private static readonly object Gate = new();
    private static Data? _data;
    private static string FilePath => System.IO.Path.Combine(AppPaths.MachineData, "journal.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static Data D
    {
        get
        {
            if (_data != null) return _data;
            try
            {
                if (File.Exists(FilePath))
                {
                    _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
                    // Entradas que o Hertzo nunca escreveria são descartadas (ver Guard.IsAllowed).
                    var bad = _data.Registry.Where(b => !Guard.IsAllowed(b)).ToList();
                    foreach (var b in bad)
                    {
                        _data.Registry.Remove(b);
                        UiHost.Log(new InvalidDataException($"Entrada do backup ignorada por segurança: {b.Owner} → {b.Hive}\\{b.Path}\\{b.Name}"));
                    }
                    foreach (var k in _data.Extra.Keys.ToList())
                        if (!ExtraValid(k, _data.Extra[k])) _data.Extra.Remove(k);
                }
            }
            catch
            {
                // Arquivo corrompido: guarda uma cópia e recomeça, sem apagar nada.
                try { File.Copy(FilePath, FilePath + ".corrompido", true); } catch { }
            }
            return _data ??= new Data();
        }
    }

    public static void Save()
    {
        lock (Gate)
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(D, Json));
            File.Move(tmp, FilePath, true);
        }
    }

    // ---------- Registro ----------

    public static void Backup(string owner, Hive hive, string path, string name)
    {
        lock (Gate)
        {
            if (D.Registry.Any(b => b.Owner == owner && b.Hive == hive && Same(b.Path, path) && Same(b.Name, name))) return;

            var val = Reg.Get(hive, path, name);
            var entry = new RegBackup { Owner = owner, Hive = hive, Path = path, Name = name, Existed = val != null };
            if (val != null)
            {
                entry.Kind = Reg.KindOf(hive, path, name) ?? RegistryValueKind.String;
                entry.Data = Encode(val, entry.Kind);
            }
            D.Registry.Add(entry);
        }
        Save();
    }

    public static RegBackup? GetBackup(string owner, Hive hive, string path, string name)
    {
        lock (Gate)
            return D.Registry.FirstOrDefault(b => b.Owner == owner && b.Hive == hive && Same(b.Path, path) && Same(b.Name, name));
    }

    public static bool HasBackup(string owner)
    {
        lock (Gate) return D.Registry.Any(b => b.Owner == owner);
    }

    public static IReadOnlyList<string> Owners()
    {
        lock (Gate) return D.Registry.Select(b => b.Owner).Distinct().ToList();
    }

    /// <summary>Restaura todos os valores originais registrados para um ajuste.</summary>
    public static void RestoreOwner(string owner)
    {
        List<RegBackup> list;
        lock (Gate) list = D.Registry.Where(b => b.Owner == owner).ToList();
        foreach (var b in list) RestoreEntry(b);
        RemoveOwner(owner);
    }

    public static void RestoreEntry(RegBackup b)
    {
        if (!Guard.IsAllowed(b)) throw new InvalidOperationException($"Backup recusado: {b.Path}\\{b.Name} não é um valor que o Hertzo altera.");
        if (!b.Existed) Reg.Delete(b.Hive, b.Path, b.Name);
        else Reg.Set(b.Hive, b.Path, b.Name, Decode(b.Data, b.Kind), b.Kind);
    }

    public static void RemoveOwner(string owner)
    {
        lock (Gate) D.Registry.RemoveAll(b => b.Owner == owner);
        Save();
    }

    // ---------- Extras (plano de energia anterior etc.) ----------

    public static string? GetExtra(string key) { lock (Gate) return D.Extra.TryGetValue(key, out var v) ? v : null; }
    public static void SetExtra(string key, string? value)
    {
        lock (Gate) { if (value == null) D.Extra.Remove(key); else D.Extra[key] = value; }
        Save();
    }

    /// <summary>Extras guardam GUIDs de planos de energia e valores de configurações de energia: nada além disso é aceito.</summary>
    private static bool ExtraValid(string key, string value) => key switch
    {
        "power.previous" or "power.high" or "power.ultimate" or "session.previous" => Guard.IsGuid(value),
        _ when key.StartsWith("pset.", StringComparison.Ordinal) => PowerSettingTweak.IsValidBackup(value),
        _ => false,
    };

    // ---------- Histórico ----------

    public static void Log(string title, string kind = "info")
    {
        lock (Gate)
        {
            D.History.Insert(0, new HistoryEntry { Title = title, Kind = kind });
            if (D.History.Count > 300) D.History.RemoveRange(300, D.History.Count - 300);
        }
        try { Save(); } catch { }
    }

    public static IReadOnlyList<HistoryEntry> History() { lock (Gate) return D.History.ToList(); }

    public static int PendingCount() { lock (Gate) return D.Registry.Select(b => b.Owner).Distinct().Count(); }

    /// <summary>Tudo que o Hertzo alterou e sabe desfazer: registro + plano de energia + configurações de energia.</summary>
    public static int ActiveChanges()
    {
        lock (Gate)
            return D.Registry.Select(b => b.Owner).Distinct().Count()
                   + (D.Extra.ContainsKey("power.previous") ? 1 : 0)
                   + D.Extra.Keys.Count(k => k.StartsWith("pset.", StringComparison.Ordinal));
    }

    // ---------- Serialização de valores ----------

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Encode(object v, RegistryValueKind k) => k switch
    {
        RegistryValueKind.DWord => ((int)v).ToString(),
        RegistryValueKind.QWord => ((long)v).ToString(),
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])v),
        RegistryValueKind.MultiString => string.Join('\0', (string[])v),
        _ => v.ToString() ?? "",
    };

    private static object Decode(string? s, RegistryValueKind k) => k switch
    {
        RegistryValueKind.DWord => int.Parse(s ?? "0"),
        RegistryValueKind.QWord => long.Parse(s ?? "0"),
        RegistryValueKind.Binary => Convert.FromBase64String(s ?? ""),
        RegistryValueKind.MultiString => (s ?? "").Split('\0'),
        _ => s ?? "",
    };
}
