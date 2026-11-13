using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Hertzo.Core;

public sealed record GameDef(string Key, string Name, string Short, string[] Folders, string[] Exes, string[] Tips);

public sealed class GameEntry : ObservableObject
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Short { get; init; }
    public required string ExePath { get; init; }
    public required string Source { get; init; }
    public string[] Tips { get; init; } = [];
    public bool IsManual => Source == "Manual";
    public string Folder => Path.GetDirectoryName(ExePath) ?? "";
    public string ExeName => Path.GetFileName(ExePath);

    public TokenTweak GpuTweak { get; }
    public LayerTweak FsoTweak { get; }
    public RegistryTweak PriorityTweak { get; }
    public IReadOnlyList<Tweak> Tweaks { get; }

    private bool _expanded;
    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }

    public GameEntry(string exePath, string name)
    {
        GpuTweak = new TokenTweak
        {
            Id = "game.gpu|" + exePath,
            Category = "Jogo",
            Title = "Usar a GPU de alto desempenho",
            Description = "Força a placa dedicada. Essencial em notebooks com 2 GPUs.",
            Hive = Hive.HKCU,
            Path = @"Software\Microsoft\DirectX\UserGpuPreferences",
            ValueName = exePath,
            TokenKey = "GpuPreference",
            TokenValue = "2",
            Impact = Impact.Situacional,
            Evidence = Evidence.Forte,
        };
        FsoTweak = new LayerTweak
        {
            Id = "game.fso|" + exePath,
            Category = "Jogo",
            Title = "Desativar otimizações de tela cheia",
            Description = "Tela cheia exclusiva clássica. Varia por jogo: teste e compare.",
            ExePath = exePath,
            Flag = "DISABLEDXMAXIMIZEDWINDOWEDMODE",
            Impact = Impact.Situacional,
            Evidence = Evidence.Fraca,
        };
        var exe = Path.GetFileName(exePath);
        PriorityTweak = new RegistryTweak
        {
            Id = "game.prio|" + exe,
            Category = "Jogo",
            Title = "Prioridade de CPU alta",
            Description = "Abre sempre com prioridade Alta (nunca Tempo real).",
            Warning = "Ganho pequeno. Pode deixar Discord/stream com engasgos em CPUs de poucos núcleos.",
            Ops = [new(Hive.HKLM, $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{exe}\PerfOptions", "CpuPriorityClass", RegistryValueKind.DWord, 3)],
            CleanupKeys =
            [
                (Hive.HKLM, $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{exe}\PerfOptions"),
                (Hive.HKLM, $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\{exe}"),
            ],
            Impact = Impact.Baixo,
            Evidence = Evidence.Fraca,
            Risk = Risk.Moderado,
        };
        Tweaks = [GpuTweak, FsoTweak, PriorityTweak];
        foreach (var t in Tweaks) t.Refresh();
    }
}

public static partial class GameScanner
{
    private static readonly string[] GenericTips =
    [
        "Ative NVIDIA Reflex (On + Boost) ou AMD Anti-Lag 2 se o jogo oferecer.",
        "Prefira tela cheia; em janela sem bordas, mantenha as “Otimizações para jogos em janela” ligadas.",
        "Um limite de FPS estável (ex.: 3 abaixo da taxa do monitor com G-SYNC/FreeSync) deixa o frametime mais uniforme.",
    ];

    public static readonly GameDef[] Catalog =
    [
        new("valorant", "VALORANT", "VAL", ["VALORANT"], ["VALORANT-Win64-Shipping.exe"],
        [
            "NVIDIA Reflex: Ativado + Boost (Vídeo › Qualidade gráfica).",
            "Renderização multithread: Ativada em CPUs com 4 núcleos ou mais.",
            "O VALORANT é limitado pela CPU: deixe o FPS sem limite ou bem acima da taxa do monitor.",
            "Use “Tela cheia” em vez de janela para menor latência.",
        ]),
        new("cs2", "Counter-Strike 2", "CS2", ["Counter-Strike Global Offensive"], ["cs2.exe"],
        [
            "NVIDIA Reflex: Ativado (Vídeo › Avançado).",
            "Boost Player Contrast: Ativado — destaca os modelos dos jogadores.",
            "Evite opções de inicialização antigas do CS:GO (-threads, -high, +cl_forcepreload): no CS2 elas não ajudam.",
            "Se o frametime oscilar muito, defina fps_max num valor estável que seu PC sustente.",
        ]),
        new("fortnite", "Fortnite", "FN", ["Fortnite"], ["FortniteClient-Win64-Shipping.exe"],
        [
            "Modo de renderização “Desempenho” em PCs modestos; DirectX 12 em GPUs modernas.",
            "NVIDIA Reflex: Ativado + Boost.",
            "Desative Nanite, Lumen e Ray Tracing no competitivo.",
            "Limite de FPS igual ou acima da taxa do monitor.",
        ]),
        new("apex", "Apex Legends", "APX", ["Apex Legends", "Apex"], ["r5apex_dx12.exe", "r5apex.exe"],
        [
            "NVIDIA Reflex: Ativado + Boost.",
            "Desative a “Resolução adaptativa” (alvo de FPS = 0).",
            "Sombras e iluminação volumétrica são os maiores custos de FPS.",
        ]),
        new("overwatch", "Overwatch 2", "OW2", ["Overwatch"], ["Overwatch.exe"],
        [
            "NVIDIA Reflex: Ativado + Boost.",
            "Reduzir buffering: Ativado.",
            "Limite de FPS: Personalizado, acima da taxa do monitor.",
        ]),
        new("r6", "Rainbow Six Siege", "R6", ["Tom Clancy's Rainbow Six Siege", "Rainbow Six Siege", "Tom Clancy's Rainbow Six Siege X"], ["RainbowSix_Vulkan.exe", "RainbowSix.exe"],
        [
            "Teste a versão Vulkan: em muitas GPUs ela entrega mais FPS que a DX11.",
            "NVIDIA Reflex: Ativado + Boost.",
        ]),
        new("lol", "League of Legends", "LOL", ["League of Legends"], ["League of Legends.exe"],
        [
            "O LoL é muito leve: limite o FPS (ex.: 240) para reduzir calor e manter o frametime estável.",
            "Desative “Aguardar sincronização vertical”.",
        ]),
        new("pubg", "PUBG: Battlegrounds", "PUBG", ["PUBG"], ["TslGame.exe"],
        [
            "Antisserrilhamento e Texturas altos; Sombras, Folhagem e Efeitos baixos é a combinação clássica competitiva.",
            "NVIDIA Reflex: Ativado + Boost.",
        ]),
        new("cod", "Call of Duty", "COD", ["Call of Duty", "Call of Duty HQ"], ["cod.exe"],
        [
            "NVIDIA Reflex: Ativado + Boost.",
            "Use “Alocação de VRAM” em torno de 80–90% para evitar travadas de streaming de texturas.",
        ]),
        new("rivals", "Marvel Rivals", "MR", ["MarvelRivals", "Marvel Rivals"], ["Marvel-Win64-Shipping.exe"],
        [
            "Desative Lumen (Iluminação global) e reflexos para competitivo.",
            "NVIDIA Reflex: Ativado + Boost.",
        ]),
        new("thefinals", "THE FINALS", "TF", ["THE FINALS"], ["Discovery.exe"],
        [
            "Iluminação global RTX “Estática” ou desligada para mais FPS.",
            "NVIDIA Reflex: Ativado + Boost.",
        ]),
        new("rocket", "Rocket League", "RL", ["rocketleague", "Rocket League"], ["RocketLeague.exe"],
        [
            "Desative Vsync e use limite de FPS acima da taxa do monitor.",
        ]),
        new("dota2", "Dota 2", "D2", ["dota 2 beta"], ["dota2.exe"],
        [
            "Vulkan costuma ter frametime mais estável que DX11 em várias GPUs; teste os dois.",
        ]),
        new("gta5", "GTA V", "GTA", ["Grand Theft Auto V Enhanced", "Grand Theft Auto V", "GTAV"], ["GTA5_Enhanced.exe", "GTA5.exe"],
        [
            "Grama e MSAA são os maiores custos de FPS.",
        ]),
        new("rust", "Rust", "RST", ["Rust"], ["RustClient.exe"],
        [
            "Rust usa muita RAM: 16 GB é o mínimo confortável; feche o navegador antes de jogar.",
        ]),
        new("tarkov", "Escape from Tarkov", "EFT", ["EscapeFromTarkov", "Escape from Tarkov"], ["EscapeFromTarkov.exe"],
        [
            "Tarkov depende muito de RAM rápida: confira se o XMP/EXPO está ativo na BIOS.",
        ]),
    ];

    [GeneratedRegex("\"path\"\\s+\"(.+?)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathRx();

    public static List<GameEntry> Scan()
    {
        var found = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<(string Path, string Source)>();

        // Steam
        try
        {
            var steam = Reg.GetString(Hive.HKCU, @"Software\Valve\Steam", "SteamPath")?.Replace('/', '\\');
            if (steam != null)
            {
                var libs = new List<string> { steam };
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in SteamPathRx().Matches(File.ReadAllText(vdf)))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
                    roots.Add((Path.Combine(lib, "steamapps", "common"), "Steam"));
            }
        }
        catch { }

        // Epic Games
        try
        {
            var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (Directory.Exists(manifests))
                foreach (var item in Directory.EnumerateFiles(manifests, "*.item"))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(item));
                    if (doc.RootElement.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { } l)
                    {
                        var parent = Path.GetDirectoryName(l.TrimEnd('\\', '/'));
                        if (parent != null) roots.Add((parent, "Epic Games"));
                    }
                }
        }
        catch { }

        // Riot, Battle.net, EA, pastas comuns em todos os discos
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var r = drive.RootDirectory.FullName;
            roots.Add((Path.Combine(r, "Riot Games"), "Riot"));
            roots.Add((Path.Combine(r, "Program Files (x86)"), "Instalado"));
            roots.Add((Path.Combine(r, "Program Files"), "Instalado"));
            roots.Add((Path.Combine(r, "Program Files", "EA Games"), "EA"));
            roots.Add((Path.Combine(r, "Program Files (x86)", "Steam", "steamapps", "common"), "Steam"));
            roots.Add((Path.Combine(r, "SteamLibrary", "steamapps", "common"), "Steam"));
            roots.Add((Path.Combine(r, "Epic Games"), "Epic Games"));
            roots.Add((Path.Combine(r, "Games"), "Instalado"));
            roots.Add((Path.Combine(r, "Jogos"), "Instalado"));
        }

        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, source) in roots)
        {
            if (!seenRoots.Add(root) || !Directory.Exists(root)) continue;
            foreach (var def in Catalog)
            {
                if (found.ContainsKey(def.Key)) continue;
                foreach (var folder in def.Folders)
                {
                    var dir = Path.Combine(root, folder);
                    if (!Directory.Exists(dir)) continue;
                    var exe = FindExe(dir, def.Exes);
                    if (exe == null) continue;
                    found[def.Key] = new GameEntry(exe, def.Name) { Key = def.Key, Name = def.Name, Short = def.Short, ExePath = exe, Source = source, Tips = def.Tips };
                    break;
                }
            }
        }

        var result = found.Values.ToList();
        foreach (var path in LoadManual())
        {
            if (!File.Exists(path) || result.Any(g => g.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(Manual(path));
        }
        return result.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static GameEntry Manual(string exePath)
    {
        var name = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).ProductName;
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(exePath);
        var shortName = new string(name.Where(char.IsLetterOrDigit).Take(3).ToArray()).ToUpperInvariant();
        return new GameEntry(exePath, name) { Key = "manual:" + exePath, Name = name.Trim(), Short = shortName, ExePath = exePath, Source = "Manual", Tips = GenericTips };
    }

    private static string? FindExe(string dir, string[] exes)
    {
        foreach (var exe in exes)
        {
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((dir, 0));
            int visited = 0;
            while (queue.Count > 0 && visited < 3000)
            {
                var (d, depth) = queue.Dequeue();
                visited++;
                try
                {
                    var candidate = Path.Combine(d, exe);
                    if (File.Exists(candidate)) return candidate;
                    if (depth >= 6) continue;
                    foreach (var sub in Directory.EnumerateDirectories(d)) queue.Enqueue((sub, depth + 1));
                }
                catch { }
            }
        }
        return null;
    }

    private static string ManualFile => Path.Combine(AppPaths.UserData, "games.json");

    public static List<string> LoadManual()
    {
        try { return File.Exists(ManualFile) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ManualFile)) ?? [] : []; }
        catch { return []; }
    }

    public static void SaveManual(IEnumerable<string> paths)
        => File.WriteAllText(ManualFile, JsonSerializer.Serialize(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));

    public static readonly string[] GpuPanelTips =
    [
        "NVIDIA › Gerenciar configurações 3D › Modo de gerenciamento de energia: “Preferir desempenho máximo”.",
        "NVIDIA › Modo de baixa latência: “Ativado” (o Reflex dentro do jogo tem prioridade quando disponível).",
        "AMD Adrenalin › Gráficos: ative o Anti-Lag 2 nos jogos compatíveis (CS2, entre outros).",
        "G-SYNC/FreeSync + V-Sync no painel da GPU + limite de FPS 3 abaixo da taxa do monitor = zero tearing com baixa latência.",
        "Atualize o driver de vídeo pelo site oficial do fabricante, não pelo Windows Update.",
    ];
}
