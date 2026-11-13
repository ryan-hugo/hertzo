using System.Diagnostics;
using System.Globalization;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Interop;

namespace Hertzo.Core;

// =====================================================================
// MEDIÇÃO DE FPS
// Usa o PresentMon (Intel, MIT) embutido no Hertzo. Ele lê os eventos de
// apresentação de quadros que o próprio Windows publica (ETW): não injeta
// nada no jogo, então é compatível com Vanguard, VAC, EAC e BattlEye.
// =====================================================================

/// <summary>Cópia embutida do PresentMon, extraída e conferida pelo hash oficial antes de cada uso.</summary>
public static class PresentMonTool
{
    public const string Version = "2.6.0";
    public const string Session = "Hertzo_Medicao";
    private const string Sha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
    private const string Resource = "Hertzo.PresentMon.exe";

    public static string Ensure()
    {
        var dir = Path.Combine(AppPaths.MachineData, "tools");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"PresentMon-{Version}-x64.exe");
        // Só reaproveita a cópia se ela for dos Administradores (ninguém sem admin a trocou) e o hash conferir.
        if (File.Exists(path) && Guard.OwnedByAdmins(path) && HashOf(path) == Sha256) return path;

        using var src = typeof(PresentMonTool).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("O PresentMon não está embutido nesta versão do Hertzo.");
        var tmp = path + ".tmp";
        using (var f = File.Create(tmp)) src.CopyTo(f);
        if (HashOf(tmp) != Sha256)
        {
            File.Delete(tmp);
            throw new InvalidOperationException("A cópia do PresentMon não confere com o hash oficial. A medição foi cancelada por segurança.");
        }
        File.Move(tmp, path, true);

        // A licença MIT pede que o aviso de copyright acompanhe as cópias.
        try
        {
            using var lic = typeof(PresentMonTool).Assembly.GetManifestResourceStream("Hertzo.PresentMon-LICENSE.txt");
            if (lic != null) { using var lf = File.Create(Path.Combine(dir, "PresentMon-LICENSE.txt")); lic.CopyTo(lf); }
        }
        catch { }
        return path;
    }

    private static string HashOf(string p)
    {
        using var s = File.OpenRead(p);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>Encerra uma sessão de captura que tenha ficado aberta (ex.: Hertzo fechado no meio da medição).</summary>
    public static void StopSession()
    {
        try { Shell.Run(Ensure(), $"--terminate_existing_session --session_name {Session}", 8000); } catch { }
    }
}

// ---------------------------------------------------------------------
// CSV do PresentMon
// ---------------------------------------------------------------------
public readonly record struct FrameRow(
    int Pid, string Swap, string Runtime, int Sync, bool Tearing, string Mode,
    double Time, double Ms, double? CpuBusy, double? GpuBusy, double? Latency);

public static class FrameCsv
{
    public sealed class Header
    {
        public int Pid, Swap, Runtime, Sync, Tearing, Mode, Time, Ms, Cpu, Gpu, Lat;
        public double TimeScale = 1;
        public bool Valid => Ms >= 0;

        public static Header Parse(string line)
        {
            var c = line.Split(',').Select(x => x.Trim()).ToArray();
            int I(params string[] names)
            {
                foreach (var n in names)
                {
                    var i = Array.FindIndex(c, h => h.Equals(n, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) return i;
                }
                return -1;
            }
            var h = new Header
            {
                Pid = I("ProcessID"),
                Swap = I("SwapChainAddress"),
                Runtime = I("PresentRuntime", "Runtime"),
                Sync = I("SyncInterval"),
                Tearing = I("AllowsTearing"),
                Mode = I("PresentMode"),
                Time = I("TimeInMs", "CPUStartTimeInMs"),
                Ms = I("MsBetweenPresents", "FrameTime"),
                Cpu = I("MsCPUBusy", "CPUBusy"),
                Gpu = I("MsGPUBusy", "GPUBusy"),
                Lat = I("MsAllInputToPhotonLatency", "AllInputToPhotonLatency"),
            };
            if (h.Time < 0) { h.Time = I("TimeInSeconds"); h.TimeScale = 1000; }
            return h;
        }
    }

    private static double? Num(string[] c, int i)
    {
        if (i < 0 || i >= c.Length) return null;
        return double.TryParse(c[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string Str(string[] c, int i) => i >= 0 && i < c.Length ? c[i] : "";

    public static bool TryRow(Header h, string line, out FrameRow row)
    {
        row = default;
        var c = line.Split(',');
        var ms = Num(c, h.Ms);
        if (ms is not > 0) return false;
        row = new FrameRow(
            (int)(Num(c, h.Pid) ?? 0), Str(c, h.Swap), Str(c, h.Runtime), (int)(Num(c, h.Sync) ?? 0),
            Num(c, h.Tearing) == 1, Str(c, h.Mode), (Num(c, h.Time) ?? 0) * h.TimeScale, ms.Value,
            Num(c, h.Cpu), Num(c, h.Gpu), Num(c, h.Lat));
        return true;
    }

    public static List<FrameRow> Read(string path)
    {
        var rows = new List<FrameRow>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var first = sr.ReadLine();
        if (first == null) return rows;
        var h = Header.Parse(first);
        if (!h.Valid) return rows;
        string? line;
        while ((line = sr.ReadLine()) != null)
            if (TryRow(h, line, out var r)) rows.Add(r);
        return rows;
    }
}

// ---------------------------------------------------------------------
// Estatísticas
// ---------------------------------------------------------------------
public sealed class FrameStats
{
    public int Frames { get; set; }
    public double Seconds { get; set; }
    public double AvgFps { get; set; }
    public double Low1 { get; set; }
    public double Low01 { get; set; }
    public double MedianMs { get; set; }
    public double P99Ms { get; set; }
    public double MaxMs { get; set; }
    public double StdMs { get; set; }
    public int Stutters { get; set; }
    public double StuttersPerMin { get; set; }
    public string PresentMode { get; set; } = "";
    public string Runtime { get; set; } = "";
    public int SyncInterval { get; set; }
    public bool Tearing { get; set; }
    public double? LatencyMs { get; set; }
    public double? GpuBusyRatio { get; set; }
    public int RefreshHz { get; set; }
    public double BelowRefreshPct { get; set; }
    /// <summary>Pior tempo de quadro de cada fatia da medição (mantém os picos visíveis no gráfico).</summary>
    public float[] Peaks { get; set; } = [];

    public static FrameStats? Compute(IReadOnlyList<FrameRow> rows, int refreshHz, int buckets = 480)
    {
        if (rows.Count < 30) return null;

        // O jogo pode ter mais de uma cadeia de troca (launcher, overlay). Fica a que mais apresentou quadros.
        var main = rows.GroupBy(r => (r.Pid, r.Swap)).OrderByDescending(g => g.Count()).First().ToList();
        var ft = main.Select(r => r.Ms).Where(ms => ms > 0 && ms < 5000).ToArray();
        int n = ft.Length;
        if (n < 30) return null;

        var sorted = (double[])ft.Clone();
        Array.Sort(sorted);
        double P(double q) => sorted[(int)Math.Clamp(Math.Ceiling(q * n) - 1, 0, n - 1)];

        double total = ft.Sum();
        double mean = total / n;
        double median = P(0.5);
        double std = Math.Sqrt(ft.Sum(x => (x - mean) * (x - mean)) / n);
        double stutterAt = Math.Max(2.5 * median, median + 6);
        int stutters = ft.Count(x => x > stutterAt);

        double refreshMs = refreshHz > 0 ? 1000.0 / refreshHz : 0;
        double below = refreshMs > 0 ? ft.Where(x => x > refreshMs * 1.03).Sum() / total * 100 : 0;

        var lat = main.Where(r => r.Latency is > 0 and < 1000).Select(r => r.Latency!.Value).OrderBy(x => x).ToList();
        var gpu = main.Where(r => r.GpuBusy is >= 0).ToList();

        var s = new FrameStats
        {
            Frames = n,
            Seconds = total / 1000,
            AvgFps = 1000 * n / total,
            Low1 = 1000 / P(0.99),
            Low01 = 1000 / P(0.999),
            MedianMs = median,
            P99Ms = P(0.99),
            MaxMs = sorted[^1],
            StdMs = std,
            Stutters = stutters,
            StuttersPerMin = stutters / Math.Max(total / 60000, 1.0 / 60),
            PresentMode = Dominant(main.Select(r => r.Mode)),
            Runtime = Dominant(main.Select(r => r.Runtime)),
            SyncInterval = main.GroupBy(r => r.Sync).OrderByDescending(g => g.Count()).First().Key,
            Tearing = main.Count(r => r.Tearing) > main.Count / 2,
            LatencyMs = lat.Count >= 20 ? lat[lat.Count / 2] : null,
            GpuBusyRatio = gpu.Count > main.Count / 2 ? gpu.Sum(r => r.GpuBusy!.Value) / gpu.Sum(r => r.Ms) : null,
            RefreshHz = refreshHz,
            BelowRefreshPct = below,
        };

        int size = (int)Math.Ceiling(n / (double)Math.Min(buckets, n));
        var peaks = new List<float>();
        for (int i = 0; i < n; i += size)
        {
            double mx = 0;
            for (int j = i; j < Math.Min(n, i + size); j++) mx = Math.Max(mx, ft[j]);
            peaks.Add((float)mx);
        }
        s.Peaks = peaks.ToArray();
        return s;
    }

    private static string Dominant(IEnumerable<string> v) =>
        v.Where(x => x.Length > 0).GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
}

public sealed record BenchNote(Tone Tone, string Title, string Detail);

public static class BenchAdvisor
{
    /// <summary>Leitura honesta do resultado: o que os números indicam e o que provavelmente ajuda.</summary>
    public static List<BenchNote> Diagnose(FrameStats s)
    {
        var notes = new List<BenchNote>();
        var mode = s.PresentMode;

        if (mode.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
            notes.Add(new(Tone.Ok, "Caminho direto para a tela",
                $"Modo de apresentação: {mode}. O quadro vai direto ao monitor, sem passar pelo compositor do Windows — é o caminho de menor atraso."));
        else if (mode.Contains("Composed: Flip", StringComparison.OrdinalIgnoreCase))
            notes.Add(new(Tone.Warn, "Imagem passa pelo compositor do Windows",
                "Modo “Composed: Flip”: o jogo está em janela e o Windows compõe a imagem, o que costuma somar atraso. Use tela cheia, ou tela cheia em janela com “Otimizações para jogos em janela” ligadas."));
        else if (mode.Contains("Copy", StringComparison.OrdinalIgnoreCase))
            notes.Add(new(Tone.Bad, "Modo de cópia antigo",
                $"Modo: {mode}. É o caminho mais lento até a tela. Troque o jogo para tela cheia."));

        if (s.SyncInterval >= 1 && !s.Tearing)
            notes.Add(new(Tone.Warn, "VSync parece ligado",
                "Em jogos competitivos o VSync tradicional adiciona atraso. Se você usa FreeSync/G-Sync, VSync ligado com limite de FPS um pouco abaixo da taxa do monitor é uma combinação válida; sem isso, prefira desligar."));

        double cv = s.StdMs / Math.Max(s.MedianMs, 0.01);
        bool capped = cv < 0.06;
        if (s.GpuBusyRatio is double g)
        {
            if (g >= 0.88)
                notes.Add(new(Tone.Info, "Limitado pela placa de vídeo",
                    $"A GPU ficou ocupada {g * 100:0}% do tempo de cada quadro. Baixar resolução, sombras ou qualidade gráfica é o que mais aumenta o FPS aqui."));
            else if (capped)
                notes.Add(new(Tone.Info, "FPS preso num teto",
                    $"O tempo de quadro quase não variou e a GPU ficou ocupada só {g * 100:0}% do tempo: deve haver um limite de FPS (no jogo, no driver ou VSync)."));
            else
                notes.Add(new(Tone.Info, "Limitado pelo processador ou pelo jogo",
                    $"A GPU ficou ociosa parte do tempo (ocupada {g * 100:0}%). Qualidade gráfica muda pouco o FPS; pesa mais o processador, a RAM e o que está aberto em segundo plano."));
        }

        if (s.StuttersPerMin >= 12)
            notes.Add(new(Tone.Bad, $"Engasgos frequentes: {s.StuttersPerMin:0} por minuto",
                "Quadros que demoraram mais de 2,5× o normal. Causas comuns: compilação de shaders (primeira partida depois de atualizar o driver ou limpar o cache), pouca RAM livre, disco cheio e programas pesados em segundo plano."));
        else if (s.StuttersPerMin >= 3)
            notes.Add(new(Tone.Warn, $"Alguns engasgos: {s.StuttersPerMin:0.#} por minuto",
                "Picos isolados são normais em partidas reais. Se aparecerem sempre no mesmo lugar, provavelmente é carregamento do próprio jogo."));
        else
            notes.Add(new(Tone.Ok, "Ritmo de quadros estável",
                "Quase nenhum quadro demorou muito mais que o normal durante a medição."));

        if (s.RefreshHz > 0)
        {
            if (s.BelowRefreshPct >= 25)
                notes.Add(new(Tone.Warn, $"Abaixo de {s.RefreshHz} Hz em {s.BelowRefreshPct:0}% do tempo",
                    $"Seu monitor mostra até {s.RefreshHz} quadros por segundo e o jogo não acompanhou boa parte do tempo."));
            else
                notes.Add(new(Tone.Ok, $"Acompanhou o monitor de {s.RefreshHz} Hz",
                    $"O jogo ficou abaixo da taxa do monitor só {s.BelowRefreshPct:0}% do tempo."));
        }

        if (s.LatencyMs is double l)
            notes.Add(new(Tone.Info, $"Atraso do clique até a tela: ~{l:0} ms",
                "Mediana medida pelo PresentMon nos quadros em que houve clique ou tecla. Não inclui o atraso do mouse e do monitor."));

        return notes;
    }

    public static string Method(string exe)
    {
        var e = exe.ToLowerInvariant();
        if (e.Contains("valorant")) return "VALORANT: use o Campo de treino e faça sempre a mesma rota. Partidas reais variam demais para comparar.";
        if (e.StartsWith("cs2")) return "CS2: use um mapa de benchmark da Oficina ou a mesma demo. Partidas online variam demais para comparar.";
        if (e.Contains("fortnite")) return "Fortnite: use um Replay ou uma ilha do Criativo, sempre no mesmo trecho.";
        if (e.Contains("league")) return "LoL: use o modo Prática no mesmo ponto do mapa, com a mesma câmera.";
        return "Use sempre o mesmo lugar, a mesma rota e as mesmas configurações gráficas. Partidas online variam demais para comparar.";
    }
}

// ---------------------------------------------------------------------
// Histórico
// ---------------------------------------------------------------------
public sealed class BenchRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public DateTime When { get; set; } = DateTime.Now;
    public string Game { get; set; } = "";
    public string Exe { get; set; } = "";
    public int Number { get; set; }
    public int Duration { get; set; }
    public FrameStats Stats { get; set; } = new();
    public List<string> Tweaks { get; set; } = [];
    public string PowerPlan { get; set; } = "";
    public string? CsvPath { get; set; }
}

public static class BenchStore
{
    private static string FilePath => Path.Combine(AppPaths.UserData, "medicoes.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static List<BenchRun> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<BenchRun>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch { return []; }
    }

    public static void Save(IEnumerable<BenchRun> runs)
    {
        var list = runs.OrderByDescending(r => r.When).Take(200).ToList();
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, FilePath, true);
    }
}

// ---------------------------------------------------------------------
// Captura
// ---------------------------------------------------------------------
public sealed class FrameCapture
{
    public static FrameCapture? Current { get; private set; }

    private Process? _p;
    private string _tool = "";
    private bool _stopRequested;

    public static bool IsRunning(string exe) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Length > 0;

    /// <summary>Encerra antes do tempo, mantendo o que já foi gravado.</summary>
    public void StopEarly()
    {
        _stopRequested = true;
        Task.Run(() =>
        {
            // Encerrar a sessão faz o PresentMon gravar o resto do CSV e sair sozinho; só força se travar.
            Shell.Run(_tool, $"--terminate_existing_session --session_name {PresentMonTool.Session}", 8000);
            try { if (_p is { HasExited: false } p && !p.WaitForExit(6000)) p.Kill(); } catch { }
        });
    }

    public static void Abort()
    {
        var c = Current;
        if (c == null) return;
        c._stopRequested = true;
        try { if (c._p is { HasExited: false }) c._p.Kill(); } catch { }
        PresentMonTool.StopSession();
    }

    /// <param name="onStarted">Chamado quando a sessão de captura sobe.</param>
    /// <param name="onProgress">Segundos decorridos.</param>
    public async Task<(List<FrameRow> Rows, string Csv)> RunAsync(string exe, int seconds,
        Action onStarted, Action<double> onProgress, CancellationToken ct)
    {
        if (exe.IndexOfAny(['"', '\\', '/', '\r', '\n']) >= 0) throw new ArgumentException("Nome de processo inválido.", nameof(exe));
        _tool = await Task.Run(PresentMonTool.Ensure);
        var dir = Path.Combine(AppPaths.UserData, "capturas");
        Directory.CreateDirectory(dir);
        Prune(dir, 12);
        var csv = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(exe)}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        var args = $"--process_name \"{exe}\" --timed {seconds} --terminate_after_timed --terminate_on_proc_exit " +
                   $"--no_console_stats --stop_existing_session --session_name {PresentMonTool.Session} --output_file \"{csv}\"";
        var psi = new ProcessStartInfo(_tool, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        var log = new StringBuilder();
        var started = new TaskCompletionSource();
        _p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void OnLine(string? l)
        {
            if (l == null) return;
            lock (log) log.AppendLine(l);
            if (l.Contains("Started recording", StringComparison.OrdinalIgnoreCase)) started.TrySetResult();
        }
        _p.OutputDataReceived += (_, e) => OnLine(e.Data);
        _p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        _p.Start();
        _p.BeginOutputReadLine();
        _p.BeginErrorReadLine();
        Current = this;

        // O PresentMon conta o --timed a partir do início da sessão, então o relógio começa aqui.
        // A saída dele vem em buffer (o "Started recording" só chega no fim) e o CSV fica travado
        // enquanto é escrito; por isso o início é anunciado assim que a sessão sobe.
        var sw = Stopwatch.StartNew();
        bool announced = false;
        try
        {
            while (!_p.HasExited)
            {
                await Task.Delay(250, CancellationToken.None);
                if (ct.IsCancellationRequested) { Abort(); break; }
                if (!announced && (sw.ElapsedMilliseconds >= 500 || started.Task.IsCompleted)) { announced = true; onStarted(); }
                onProgress(Math.Min(sw.Elapsed.TotalSeconds, seconds));
                if (sw.Elapsed.TotalSeconds > seconds + 20) { StopEarly(); break; }
            }
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try { await _p.WaitForExitAsync(wait.Token); } catch (OperationCanceledException) { try { _p.Kill(); } catch { } }
        }
        finally { Current = null; }

        ct.ThrowIfCancellationRequested();
        if (!File.Exists(csv))
        {
            string text; lock (log) text = log.ToString().Trim();
            throw new InvalidOperationException(_stopRequested
                ? "A medição foi encerrada antes de gravar quadros."
                : "O PresentMon não registrou nenhum quadro do jogo. Ele precisa estar aberto e renderizando (fora do menu minimizado)."
                  + (text.Length > 0 ? "\n\n" + text : ""));
        }
        var rows = await Task.Run(() => FrameCsv.Read(csv));
        return (rows, csv);
    }

    public static string CsvDir => Path.Combine(AppPaths.UserData, "capturas");

    /// <summary>Só CSVs da pasta de capturas do Hertzo podem ser apagados/abertos (o medicoes.json pode ter sido editado por outro programa).</summary>
    public static bool IsOurCsv(string? path)
        => path != null && path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && Guard.IsUnder(path, CsvDir) && File.Exists(path);

    private static void Prune(string dir, int keep)
    {
        try
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.csv").OrderByDescending(f => f.LastWriteTime).Skip(keep))
                f.Delete();
        }
        catch { }
    }

}

// ---------------------------------------------------------------------
// Atalho global e bipes (para usar com o jogo em primeiro plano)
// ---------------------------------------------------------------------
public sealed class GlobalHotkey : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    public const string Label = "CTRL + SHIFT + F9";

    private readonly int Id;
    private uint _vk, _mods;
    private bool _on;

    /// <summary>Ctrl+Shift+F9 (medição) por padrão; os clipes usam atalhos configuráveis.</summary>
    public GlobalHotkey(int id = 0x5051, uint vk = 0x78, uint mods = MOD_CONTROL | MOD_SHIFT) { Id = id; _vk = vk; _mods = mods; }

    /// <summary>Troca a combinação. Se o Windows recusar (outro programa usa), volta para a anterior e retorna false.</summary>
    public bool Change(uint mods, uint vk)
    {
        bool wasOn = _on;
        var (oldMods, oldVk) = (_mods, _vk);
        Dispose();
        (_mods, _vk) = (mods, vk);
        if (!wasOn) return true;
        if (Register()) return true;
        (_mods, _vk) = (oldMods, oldVk);
        Register();
        return false;
    }

    /// <summary>Testa se a combinação está livre no Windows agora.</summary>
    public static bool IsFree(uint mods, uint vk)
    {
        const int probe = 0x5FFF;
        if (!RegisterHotKey(IntPtr.Zero, probe, mods | MOD_NOREPEAT, vk)) return false;
        UnregisterHotKey(IntPtr.Zero, probe);
        return true;
    }
    public event Action? Pressed;

    public bool Register()
    {
        if (_on) return true;
        _on = RegisterHotKey(IntPtr.Zero, Id, _mods | MOD_NOREPEAT, _vk);
        if (_on) ComponentDispatcher.ThreadFilterMessage += Filter;
        return _on;
    }

    private void Filter(ref MSG m, ref bool handled)
    {
        if (m.message == WM_HOTKEY && (int)m.wParam == Id) { handled = true; Pressed?.Invoke(); }
    }

    public void Dispose()
    {
        if (!_on) return;
        UnregisterHotKey(IntPtr.Zero, Id);
        ComponentDispatcher.ThreadFilterMessage -= Filter;
        _on = false;
    }
}

public static class Beeper
{
    /// <summary>Sons do Hertzo ligados (opção da tela Clipes).</summary>
    public static bool Enabled { get; set; } = true;
    private static SoundPlayer? _player;

    public static void Start() => Play((1320, 90));
    public static void Done() => Play((990, 90), (0, 60), (1320, 160));
    public static void Fail() => Play((330, 260));
    public static void Tick() => Play((880, 45));

    private static void Play(params (double Hz, int Ms)[] seq)
    {
        if (!Enabled) return;
        try
        {
            _player?.Stop();
            _player = new SoundPlayer(new MemoryStream(Wav(seq)));
            _player.Play();
        }
        catch { }
    }

    private static byte[] Wav((double Hz, int Ms)[] seq)
    {
        const int rate = 44100;
        var samples = new List<short>();
        foreach (var (hz, ms) in seq)
        {
            int n = rate * ms / 1000;
            int ramp = Math.Min(n / 4, rate * 6 / 1000);
            for (int i = 0; i < n; i++)
            {
                double env = Math.Min(1, Math.Min(i, n - 1 - i) / (double)Math.Max(1, ramp));
                double v = hz <= 0 ? 0 : Math.Sin(2 * Math.PI * hz * i / rate) * env * 0.32;
                samples.Add((short)(v * short.MaxValue));
            }
        }
        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        int data = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(data);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms2.ToArray();
    }
}
