using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Hertzo.Core;

namespace Hertzo.ViewModels;

// =====================================================================
// ESTADO COMPARTILHADO
// =====================================================================
public static class AppState
{
    public static HardwareSnapshot? Hw { get; private set; }
    public static List<CheckItem> Checks { get; private set; } = [];
    public static int Score { get; private set; }
    public static event Action? Updated;

    private static bool _refreshing;

    public static async Task LoadAsync()
    {
        Hw = await Task.Run(SystemInfo.Read);
        await RefreshChecksAsync();
    }

    public static async Task RefreshChecksAsync()
    {
        if (Hw == null || _refreshing) return;
        _refreshing = true;
        try
        {
            var hw = Hw;
            var checks = await Task.Run(() => Checkup.Run(hw));
            Checks = checks;
            Score = Checkup.Score(checks);
            Updated?.Invoke();
        }
        finally { _refreshing = false; }
    }
}

// =====================================================================
// JANELA PRINCIPAL
// =====================================================================
public sealed class NavItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Number { get; init; }
    public required string Title { get; init; }
    public required Geometry Icon { get; init; }
    public required object Page { get; init; }
    /// <summary>Pilar do Hertzo a que a tela pertence (agrupa o menu).</summary>
    public string Group { get; init; } = "";

    private bool _selected;
    public bool IsSelected { get => _selected; set { if (Set(ref _selected, value) && value) Selected?.Invoke(this); } }
    public event Action<NavItem>? Selected;

    private string _badge = "";
    public string Badge { get => _badge; set { Set(ref _badge, value); OnPropertyChanged(nameof(HasBadge)); } }
    public bool HasBadge => Badge.Length > 0;
}

/// <summary>Um pilar do Hertzo no menu: título, uma frase e as telas dele.</summary>
public sealed record NavGroup(string Title, string Hint, List<NavItem> Items);

public sealed class ToastVm
{
    public required string Message { get; init; }
    public required Tone Tone { get; init; }
}

public sealed class MainViewModel : ObservableObject
{
    public List<NavItem> Nav { get; }
    public List<NavGroup> NavGroups { get; }
    public ObservableCollection<ToastVm> Toasts { get; } = [];

    private NavItem _current;
    public NavItem Current { get => _current; private set { Set(ref _current, value); OnPropertyChanged(nameof(Crumb)); } }
    public string Crumb => $"{Current.Number} / {Current.Title.ToUpperInvariant()}";

    private DialogRequest? _dialog;
    public DialogRequest? Dialog { get => _dialog; set { Set(ref _dialog, value); OnPropertyChanged(nameof(HasDialog)); } }
    public bool HasDialog => Dialog != null;

    private string? _busy;
    public string? BusyMessage { get => _busy; set { Set(ref _busy, value); OnPropertyChanged(nameof(IsBusy)); } }
    public bool IsBusy => _busy != null;

    public RelayCommand DialogConfirm { get; }
    public RelayCommand DialogCancel { get; }

    public string Version => "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    private readonly NavItem _checkupNav;
    private DispatcherTimer? _countdown;
    private readonly LiveMonitor _mon = new();

    private string _clock = "";
    public string Clock { get => _clock; set => Set(ref _clock, value); }
    private string _ticker = "HERTZO · TELEMETRIA DE SISTEMA · LENDO HARDWARE";
    public string TickerText { get => _ticker; set => Set(ref _ticker, value); }

    private void Tick()
    {
        Clock = DateTime.Now.ToString("HH:mm:ss");
        var cpu = _mon.CpuPercent();
        var (ram, _, _) = LiveMonitor.Ram();
        var hw = AppState.Hw;
        const string sep = "   ///   ";
        var parts = new List<string> { $"CPU {cpu,3:0}%", $"RAM {ram,3:0}%" };
        if (hw != null)
        {
            parts.Add($"NOTA {AppState.Score:000}/100");
            parts.Add(hw.CpuName.ToUpperInvariant());
            if (hw.MainGpu != null) parts.Add(hw.MainGpu.Name.ToUpperInvariant());
            parts.Add($"{SystemInfo.Bytes(hw.RamTotal)} {hw.RamType} {hw.RamMHz} MT/S");
            parts.Add($"{hw.OsName.ToUpperInvariant()} {hw.OsVersion.ToUpperInvariant()}");
            var issues = AppState.Checks.Count(c => c.IsIssue);
            parts.Add(issues == 0 ? "CHECK-UP LIMPO" : $"{issues} PENDÊNCIA(S) NO CHECK-UP");
            if (ReplayEngine.Active is { Running: true } r) parts.Add($"REPLAY GRAVANDO {Math.Min(r.BufferedSeconds, r.MaxSeconds):0}/{r.MaxSeconds} S");
        }
        TickerText = string.Join(sep, parts) + sep;
    }

    public MainViewModel()
    {
        static Geometry G(string s) => Geometry.Parse(s);
        Nav =
        [
            new NavItem { Group = "INÍCIO", Key = "dashboard", Number = "01", Title = "Painel", Icon = G("M3,3 H10 V10 H3 Z M14,3 H21 V8 H14 Z M14,12 H21 V21 H14 Z M3,14 H10 V21 H3 Z"), Page = new DashboardViewModel() },
            new NavItem { Group = "OTIMIZAR", Key = "checkup", Number = "02", Title = "Check-up", Icon = G("M2,12 H6.5 L9,5 L13.5,19 L16,12 H22"), Page = new CheckupViewModel() },
            new NavItem { Group = "OTIMIZAR", Key = "tweaks", Number = "03", Title = "Otimizações", Icon = G("M4,6 H20 M4,12 H20 M4,18 H20 M9,3.5 V8.5 M15,9.5 V14.5 M7,15.5 V20.5"), Page = new TweaksViewModel() },
            new NavItem { Group = "OTIMIZAR", Key = "games", Number = "04", Title = "Jogos", Icon = G("M7,7 H17 C20,7 22,9.5 22,13 C22,16.5 20,18 18,18 C16.5,18 15.5,16.5 14.5,15 H9.5 C8.5,16.5 7.5,18 6,18 C4,18 2,16.5 2,13 C2,9.5 4,7 7,7 Z M7,10.5 V13.5 M5.5,12 H8.5 M16,11 L16,11 M18,13 L18,13"), Page = new GamesViewModel() },
            new NavItem { Group = "MEDIR", Key = "bench", Number = "05", Title = "Medição", Icon = G("M3,20 H21 M6,16 V10 M10,16 V6 M14,16 V12 M18,16 V8"), Page = new BenchViewModel() },
            new NavItem { Group = "CAPTURAR", Key = "clips", Number = "06", Title = "Clipes", Icon = G("M3,6 H21 V18 H3 Z M10,9.5 L15,12 L10,14.5 Z"), Page = new ClipsViewModel() },
            new NavItem { Group = "MANTER", Key = "cleanup", Number = "07", Title = "Limpeza", Icon = G("M4,7 H20 M9,7 V4 H15 V7 M6,7 L7,20 H17 L18,7 M10,11 V16 M14,11 V16"), Page = new CleanupViewModel() },
            new NavItem { Group = "MANTER", Key = "startup", Number = "08", Title = "Inicialização", Icon = G("M12,3 V11 M7.5,5.8 C5.4,7.2 4,9.6 4,12.3 C4,16.6 7.6,20 12,20 C16.4,20 20,16.6 20,12.3 C20,9.6 18.6,7.2 16.5,5.8"), Page = new StartupViewModel() },
            new NavItem { Group = "PROTEGER", Key = "backup", Number = "09", Title = "Backup", Icon = G("M12,3 L20,6 V12 C20,16.5 16.5,19.8 12,21 C7.5,19.8 4,16.5 4,12 V6 Z M8.8,12 L11,14.2 L15.2,10"), Page = new BackupViewModel() },
            new NavItem { Group = "PROTEGER", Key = "transparency", Number = "10", Title = "Transparência", Icon = G("M2,12 C4.8,7 8.2,5 12,5 C15.8,5 19.2,7 22,12 C19.2,17 15.8,19 12,19 C8.2,19 4.8,17 2,12 Z M12,9 C13.7,9 15,10.3 15,12 C15,13.7 13.7,15 12,15 C10.3,15 9,13.7 9,12 C9,10.3 10.3,9 12,9 Z"), Page = new TransparencyViewModel() },
        ];
        var hints = new Dictionary<string, string>
        {
            ["INÍCIO"] = "VISÃO GERAL", ["OTIMIZAR"] = "PC PRONTO PARA JOGAR", ["MEDIR"] = "PROVA COM NÚMEROS",
            ["CAPTURAR"] = "SUAS JOGADAS", ["MANTER"] = "LIMPEZA E BOOT", ["PROTEGER"] = "BACKUP E HONESTIDADE",
        };
        NavGroups = Nav.GroupBy(n => n.Group).Select(g => new NavGroup(g.Key, hints.GetValueOrDefault(g.Key, ""), g.ToList())).ToList();
        _checkupNav = Nav[1];
        foreach (var n in Nav) n.Selected += OnSelected;
        _current = Nav[0];
        Nav[0].IsSelected = true;

        DialogConfirm = new RelayCommand(_ => CloseDialog(true));
        DialogCancel = new RelayCommand(_ => CloseDialog(false));

        UiHost.ToastImpl = ShowToast;
        UiHost.NavigateImpl = Navigate;
        UiHost.BusyImpl = m => BusyMessage = m;
        UiHost.ConfirmImpl = ShowDialog;

        AppState.Updated += () =>
        {
            var issues = AppState.Checks.Count(c => c.IsIssue);
            _checkupNav.Badge = issues > 0 ? issues.ToString() : "";
        };
        AppEvents.StateChanged += () => _ = AppState.RefreshChecksAsync();

        _mon.CpuPercent();
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => Tick();
        t.Start();
        Tick();
    }

    public async Task InitAsync()
    {
        try { await AppState.LoadAsync(); }
        catch (Exception ex) { UiHost.Error(ex); }
    }

    private void OnSelected(NavItem item)
    {
        foreach (var n in Nav) if (n != item) n.IsSelected = false;
        Current = item;
        if (item.Page is IPage p) p.OnShown();
    }

    public void Navigate(string key)
    {
        var n = Nav.FirstOrDefault(x => x.Key == key);
        if (n != null) n.IsSelected = true;
    }

    private void ShowToast(string message, Tone tone)
    {
        var t = new ToastVm { Message = message, Tone = tone };
        Toasts.Add(t);
        while (Toasts.Count > 4) Toasts.RemoveAt(0);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(tone == Tone.Bad ? 7 : 4.5) };
        timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(t); };
        timer.Start();
    }

    private Task<bool> ShowDialog(DialogRequest r)
    {
        if (Dialog != null) CloseDialog(false);
        Dialog = r;
        if (r.TimeoutSeconds > 0)
        {
            var left = r.TimeoutSeconds;
            r.Countdown = $"Voltando automaticamente em {left}s";
            _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdown.Tick += (_, _) =>
            {
                left--;
                if (left <= 0) { CloseDialog(false); return; }
                r.Countdown = $"Voltando automaticamente em {left}s";
            };
            _countdown.Start();
        }
        return r.Result.Task;
    }

    private void CloseDialog(bool result)
    {
        _countdown?.Stop();
        _countdown = null;
        var d = Dialog;
        Dialog = null;
        d?.Result.TrySetResult(result);
    }
}

public interface IPage { void OnShown(); }

// =====================================================================
// 01 PAINEL
// =====================================================================
public sealed record SpecRow(string Label, string Value, string Sub, Tone Tone);

/// <summary>Cartão de um pilar do Hertzo no Painel: o dado vivo mais importante e o atalho para a tela.</summary>
public sealed record PillarCard(string Key, string Letter, string Title, string Big, string Caption, string Detail, Tone Tone);

public sealed class DashboardViewModel : ObservableObject, IPage
{
    private readonly LiveMonitor _mon = new();
    private readonly List<double> _cpu = [];
    private readonly DispatcherTimer _timer;
    private DispatcherTimer? _scoreAnim;

    public DashboardViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
        _mon.CpuPercent();

        OptimizeCommand = new RelayCommand(_ => UiHost.RunRecommended());
        CheckupCommand = new RelayCommand(_ => UiHost.Navigate("checkup"));
        FixCommand = new RelayCommand(p => p is CheckItem { Action: not null } c ? c.Action() : Task.CompletedTask);
        SessionCommand = new RelayCommand(_ => ToggleSession());
        AppState.Updated += Load;
    }

    public RelayCommand OptimizeCommand { get; }
    public RelayCommand CheckupCommand { get; }
    public RelayCommand FixCommand { get; }
    public RelayCommand SessionCommand { get; }

    private bool _loading = true;
    public bool Loading { get => _loading; set => Set(ref _loading, value); }

    // Pontuação
    private double _displayScore;
    public double DisplayScore { get => _displayScore; set { Set(ref _displayScore, value); OnPropertyChanged(nameof(ScoreText)); } }
    public string ScoreText => Loading ? "--" : ((int)Math.Round(DisplayScore)).ToString();
    private Tone _scoreTone = Tone.Accent;
    public Tone ScoreTone { get => _scoreTone; set => Set(ref _scoreTone, value); }
    private string _scoreTitle = "Lendo o seu PC…";
    public string ScoreTitle { get => _scoreTitle; set => Set(ref _scoreTitle, value); }
    private string _scoreHint = "Coletando hardware, monitores e configurações do Windows.";
    public string ScoreHint { get => _scoreHint; set => Set(ref _scoreHint, value); }

    public ObservableCollection<CheckItem> Issues { get; } = [];
    private bool _noIssues;
    public bool NoIssues { get => _noIssues; set => Set(ref _noIssues, value); }

    // Hardware
    private string _cpuName = "—", _cpuSub = "", _gpuName = "—", _gpuSub = "", _ramMain = "—", _ramSub = "", _monMain = "—", _monSub = "", _osMain = "—", _osSub = "", _diskMain = "—", _diskSub = "";
    public string CpuName { get => _cpuName; set => Set(ref _cpuName, value); }
    public string CpuSub { get => _cpuSub; set => Set(ref _cpuSub, value); }
    public string GpuName { get => _gpuName; set => Set(ref _gpuName, value); }
    public string GpuSub { get => _gpuSub; set => Set(ref _gpuSub, value); }
    public string RamMain { get => _ramMain; set => Set(ref _ramMain, value); }
    public string RamSub { get => _ramSub; set => Set(ref _ramSub, value); }
    public string MonitorMain { get => _monMain; set => Set(ref _monMain, value); }
    public string MonitorSub { get => _monSub; set => Set(ref _monSub, value); }
    private Tone _monTone = Tone.Ok;
    public Tone MonitorTone { get => _monTone; set => Set(ref _monTone, value); }
    public string OsMain { get => _osMain; set => Set(ref _osMain, value); }
    public string OsSub { get => _osSub; set => Set(ref _osSub, value); }
    public string DiskMain { get => _diskMain; set => Set(ref _diskMain, value); }
    public string DiskSub { get => _diskSub; set => Set(ref _diskSub, value); }
    private double _diskPct;
    public double DiskPct { get => _diskPct; set => Set(ref _diskPct, value); }

    // Ao vivo
    public IReadOnlyList<double> CpuSamples => _cpu;
    private int _version;
    public int CpuVersion { get => _version; set => Set(ref _version, value); }
    private string _cpuNow = "0%", _ramNow = "0%", _ramDetail = "";
    public string CpuNow { get => _cpuNow; set => Set(ref _cpuNow, value); }
    public string RamNow { get => _ramNow; set => Set(ref _ramNow, value); }
    public string RamDetail { get => _ramDetail; set => Set(ref _ramDetail, value); }
    private double _ramPct;
    public double RamPct { get => _ramPct; set => Set(ref _ramPct, value); }

    // Sessão
    private bool _session;
    public bool SessionActive { get => _session; set { Set(ref _session, value); OnPropertyChanged(nameof(SessionLabel)); OnPropertyChanged(nameof(SessionState)); } }
    public string SessionLabel => SessionActive ? "ENCERRAR SESSÃO" : "INICIAR SESSÃO";
    public string SessionState => SessionActive ? "ATIVO" : "EM ESPERA";

    public ObservableCollection<SpecRow> Specs { get; } = [];
    private string _sessionInfo = "Liga o Alto Desempenho e libera a memória em espera só enquanto você joga. Ao encerrar, tudo volta como estava.";
    public string SessionInfo { get => _sessionInfo; set => Set(ref _sessionInfo, value); }

    public void OnShown() => _ = LoadPillars();

    private void Sample()
    {
        var c = _mon.CpuPercent();
        _cpu.Add(c);
        if (_cpu.Count > 60) _cpu.RemoveAt(0);
        CpuNow = $"{c:0}%";
        var (pct, used, total) = LiveMonitor.Ram();
        RamNow = $"{pct:0}%";
        RamPct = pct;
        RamDetail = $"{SystemInfo.Bytes(used)} de {SystemInfo.Bytes(total)}";
        CpuVersion++;
    }

    private async Task ToggleSession()
    {
        if (!SessionActive)
        {
            var info = await Task.Run(GameSession.Start);
            SessionActive = true;
            SessionInfo = "Sessão ativa: " + info + ". Encerre quando terminar de jogar.";
            UiHost.Toast("Sessão de jogo iniciada. Bom jogo!", Tone.Ok);
        }
        else
        {
            await Task.Run(GameSession.Stop);
            SessionActive = false;
            SessionInfo = "Sessão encerrada: plano de energia anterior restaurado.";
            UiHost.Toast("Sessão de jogo encerrada.", Tone.Info);
        }
        AppEvents.RaiseStateChanged();
    }

    // ---- pilares ----
    private List<PillarCard> _pillars = [];
    public List<PillarCard> Pillars { get => _pillars; set => Set(ref _pillars, value); }
    public RelayCommand GoCommand { get; } = new(p => { if (p is string k) UiHost.Navigate(k); });

    private async Task LoadPillars()
    {
        var score = AppState.Score;
        var issues = AppState.Checks.Count(c => c.IsIssue);
        var list = await Task.Run(() =>
        {
            var cards = new List<PillarCard>();
            var active = TweakCatalog.All.Count(t => t.IsApplied);
            cards.Add(new("tweaks", "A", "OTIMIZAR", score.ToString("000"), "NOTA DE PRONTIDÃO",
                (issues == 0 ? "Check-up limpo" : $"{issues} pendência(s) no check-up") + $" · {active} ajustes ativos",
                score >= 85 ? Tone.Ok : score >= 65 ? Tone.Warn : Tone.Bad));

            var last = BenchStore.Load().OrderByDescending(r => r.When).FirstOrDefault();
            cards.Add(last == null
                ? new("bench", "B", "MEDIR", "--", "SEM MEDIÇÕES AINDA", "Meça o FPS antes e depois de otimizar", Tone.Neutral)
                : new("bench", "B", "MEDIR", last.Stats.AvgFps.ToString("0"), $"FPS MÉDIO · {last.Game.ToUpperInvariant()}",
                    $"1% low {last.Stats.Low1:0} · {last.When:dd/MM HH:mm}", Tone.Accent));

            var cs = ClipSettings.Load();
            var clips = ClipIndex.Load().Count(c => File.Exists(c.Path));
            var rec = ReplayEngine.Active is { Running: true };
            cards.Add(new("clips", "C", "CAPTURAR", clips.ToString(), "CLIPES SALVOS",
                rec ? $"Replay gravando · {cs.SaveKey.Label} salva"
                    : cs.Armed ? (cs.OnlyInGame ? "Replay ligado · espera um jogo abrir" : "Replay ligado")
                    : "Replay desligado",
                rec ? Tone.Accent : cs.Armed ? Tone.Ok : Tone.Neutral));

            int startup = 0;
            try { startup = StartupManager.Read().Count(s => s.Enabled); } catch { }
            cards.Add(new("startup", "D", "MANTER", startup.ToString(), "PROGRAMAS AO LIGAR O PC",
                "Limpeza de temporários e caches de shader", startup > 12 ? Tone.Warn : Tone.Neutral));

            var backups = Journal.ActiveChanges();
            cards.Add(new("backup", "E", "PROTEGER", backups.ToString(), "ALTERAÇÕES COM BACKUP",
                "Tudo volta ao original com um clique", Tone.Ok));
            return cards;
        });
        Pillars = list;
    }

    private void Load()
    {
        _ = LoadPillars();
        var hw = AppState.Hw;
        if (hw == null) return;
        Loading = false;

        CpuName = hw.CpuName;
        CpuSub = $"{hw.Cores} núcleos · {hw.Threads} threads";
        var g = hw.MainGpu;
        GpuName = g?.Name ?? "GPU não identificada";
        GpuSub = g == null ? "" : $"{(g.VramBytes > 0 ? SystemInfo.Bytes(g.VramBytes) + " · " : "")}driver {(g.DriverDate?.ToString("dd/MM/yyyy") ?? g.DriverVersion)}";
        RamMain = SystemInfo.Bytes(hw.RamTotal).Replace(",0", "");
        RamSub = string.Join(" · ", new[] { hw.RamType, hw.RamMHz > 0 ? $"{hw.RamMHz} MT/s" : "", hw.Modules.Count > 0 ? $"{hw.Modules.Count} pente(s)" : "" }.Where(s => s.Length > 0));
        var d = SystemInfo.Displays().FirstOrDefault();
        if (d != null)
        {
            MonitorMain = $"{d.Width}×{d.Height}";
            MonitorSub = d.BelowMax ? $"{d.CurrentHz} Hz — suporta {d.MaxHz} Hz" : $"{d.CurrentHz} Hz · taxa máxima";
            MonitorTone = d.BelowMax ? Tone.Bad : Tone.Ok;
        }
        OsMain = hw.OsName;
        OsSub = hw.OsVersion + (hw.IsLaptop ? " · notebook" : "");
        var (free, total) = SystemInfo.SystemDrive();
        DiskMain = $"{SystemInfo.Bytes(free)} livres";
        DiskSub = total > 0 ? $"de {SystemInfo.Bytes(total)} no disco do sistema" : "";
        DiskPct = total > 0 ? 100 - free * 100.0 / total : 0;

        Specs.Clear();
        Specs.Add(new SpecRow("PROCESSADOR", CpuName, CpuSub, Tone.Neutral));
        Specs.Add(new SpecRow("PLACA DE VÍDEO", GpuName, GpuSub, Tone.Neutral));
        Specs.Add(new SpecRow("MEMÓRIA", RamMain, RamSub, hw.Modules.Count == 1 && !hw.IsLaptop ? Tone.Warn : Tone.Neutral));
        Specs.Add(new SpecRow("MONITOR", MonitorMain, MonitorSub, MonitorTone == Tone.Bad ? Tone.Bad : Tone.Ok));
        Specs.Add(new SpecRow("SISTEMA", OsMain, OsSub, Tone.Neutral));
        Specs.Add(new SpecRow("ARMAZENAMENTO", DiskMain, DiskSub, DiskPct > 90 ? Tone.Bad : DiskPct > 80 ? Tone.Warn : Tone.Neutral));

        var score = AppState.Score;
        ScoreTone = score >= 85 ? Tone.Ok : score >= 65 ? Tone.Warn : Tone.Bad;
        ScoreTitle = score >= 85 ? "Pronto para jogar" : score >= 65 ? "Bom, mas dá para melhorar" : "Precisa de atenção";
        var issues = AppState.Checks.Where(c => c.IsIssue).OrderByDescending(c => c.Tone == Tone.Bad).ThenByDescending(c => c.Weight).ToList();
        ScoreHint = issues.Count == 0
            ? "Nenhum problema encontrado no check-up. Seu Windows está afinado."
            : $"{issues.Count} ponto(s) para corrigir. Comece pelos de maior peso abaixo.";
        Issues.Clear();
        foreach (var i in issues.Take(3)) Issues.Add(i);
        NoIssues = issues.Count == 0;
        AnimateScore(score);
    }

    private void AnimateScore(int target)
    {
        _scoreAnim?.Stop();
        var start = DisplayScore;
        var t0 = DateTime.Now;
        _scoreAnim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scoreAnim.Tick += (_, _) =>
        {
            var p = Math.Min(1, (DateTime.Now - t0).TotalMilliseconds / 900);
            var e = 1 - Math.Pow(1 - p, 3);
            DisplayScore = start + (target - start) * e;
            if (p >= 1) _scoreAnim?.Stop();
        };
        _scoreAnim.Start();
    }
}

// =====================================================================
// 02 CHECK-UP
// =====================================================================
public sealed class CheckGroup
{
    public required string Name { get; init; }
    public required List<CheckItem> Items { get; init; }
}

public sealed class CheckupViewModel : ObservableObject, IPage
{
    public CheckupViewModel()
    {
        RefreshCommand = new RelayCommand(async _ => { Scanning = true; await AppState.RefreshChecksAsync(); Scanning = false; });
        FixCommand = new RelayCommand(p => p is CheckItem { Action: not null } c ? c.Action() : Task.CompletedTask);
        AppState.Updated += Load;
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand FixCommand { get; }
    public ObservableCollection<CheckGroup> Groups { get; } = [];

    private bool _scanning;
    public bool Scanning { get => _scanning; set => Set(ref _scanning, value); }
    private string _summary = "Analisando…";
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    private int _score;
    public int Score { get => _score; set => Set(ref _score, value); }
    private Tone _tone = Tone.Accent;
    public Tone ScoreTone { get => _tone; set => Set(ref _tone, value); }
    private int _ok, _warn, _bad;
    public int OkCount { get => _ok; set => Set(ref _ok, value); }
    public int WarnCount { get => _warn; set => Set(ref _warn, value); }
    public int BadCount { get => _bad; set => Set(ref _bad, value); }

    public void OnShown() { }

    private void Load()
    {
        var checks = AppState.Checks;
        Groups.Clear();
        foreach (var g in checks.GroupBy(c => c.Group))
            Groups.Add(new CheckGroup
            {
                Name = g.Key,
                Items = g.OrderBy(c => c.Tone switch { Tone.Bad => 0, Tone.Warn => 1, Tone.Ok => 2, _ => 3 }).ToList(),
            });
        Score = AppState.Score;
        ScoreTone = Score >= 85 ? Tone.Ok : Score >= 65 ? Tone.Warn : Tone.Bad;
        OkCount = checks.Count(c => c.Tone == Tone.Ok);
        WarnCount = checks.Count(c => c.Tone == Tone.Warn);
        BadCount = checks.Count(c => c.Tone == Tone.Bad);
        Summary = BadCount + WarnCount == 0 ? "Tudo certo. Nada importante para corrigir."
            : $"{BadCount} problema(s) e {WarnCount} ponto(s) de atenção.";
    }
}

// =====================================================================
// 03 OTIMIZAÇÕES
// =====================================================================
public sealed class TweakGroup
{
    public required string Number { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required List<Tweak> Items { get; init; }
    public bool IsAdvanced { get; init; }
}

public sealed class TweaksViewModel : ObservableObject, IPage
{
    private static readonly Dictionary<string, string> Descriptions = new()
    {
        [TweakCatalog.CatSistema] = "Como o Windows distribui CPU e energia.",
        [TweakCatalog.CatGraficos] = "Apresentação de quadros, captura e agendamento da GPU.",
        [TweakCatalog.CatEntrada] = "Precisão do mouse e atalhos que atrapalham no meio da partida.",
        [TweakCatalog.CatSegundoPlano] = "Menos coisa rodando escondida enquanto você joga.",
        [TweakCatalog.CatRede] = "Ajustes de rede. Lembrete: nada disso reduz o ping até o servidor.",
        [TweakCatalog.CatAvancado] = "Trocas entre segurança/consumo e desempenho. Leia o aviso de cada um.",
    };

    public TweaksViewModel()
    {
        ToggleCommand = new RelayCommand(p => p is Tweak t ? UiHost.ToggleTweak(t, !t.IsApplied) : Task.CompletedTask);
        OptimizeCommand = new RelayCommand(_ => UiHost.RunRecommended());
        int i = 1;
        foreach (var g in TweakCatalog.All.GroupBy(t => t.Category))
        {
            Groups.Add(new TweakGroup
            {
                Number = (i++).ToString("00"),
                Name = g.Key,
                Description = Descriptions.GetValueOrDefault(g.Key, ""),
                Items = g.ToList(),
                IsAdvanced = g.Key == TweakCatalog.CatAvancado,
            });
        }
        AppEvents.StateChanged += UpdateCounts;
    }

    public RelayCommand ToggleCommand { get; }
    public RelayCommand OptimizeCommand { get; }
    public List<TweakGroup> Groups { get; } = [];

    private bool _showAdvanced;
    public bool ShowAdvanced { get => _showAdvanced; set => Set(ref _showAdvanced, value); }

    private string _counts = "";
    public string Counts { get => _counts; set => Set(ref _counts, value); }

    public async void OnShown()
    {
        await Task.Run(() => { foreach (var t in TweakCatalog.All) t.Refresh(); });
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var all = TweakCatalog.All;
        var rec = all.Where(t => t.Recommended).ToList();
        Counts = $"{all.Count(t => t.IsApplied)} de {all.Count} ativos · {rec.Count(t => t.IsApplied)}/{rec.Count} recomendados";
    }
}

// =====================================================================
// 04 JOGOS
// =====================================================================
public sealed class GamesViewModel : ObservableObject, IPage
{
    private bool _scanned;

    public GamesViewModel()
    {
        ScanCommand = new RelayCommand(_ => Scan());
        AddCommand = new RelayCommand(_ => Add());
        RemoveCommand = new RelayCommand(p => Remove(p as GameEntry));
        ToggleCommand = new RelayCommand(p => p is Tweak t ? UiHost.ToggleTweak(t, !t.IsApplied) : Task.CompletedTask);
        OpenFolderCommand = new RelayCommand(p => { if (p is GameEntry g && Directory.Exists(g.Folder)) Shell.Open(g.Folder); });
    }

    public RelayCommand ScanCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public ObservableCollection<GameEntry> Games { get; } = [];
    public string[] GpuTips => GameScanner.GpuPanelTips;

    private bool _scanning;
    public bool Scanning { get => _scanning; set { Set(ref _scanning, value); OnPropertyChanged(nameof(Empty)); } }
    public bool Empty => !Scanning && Games.Count == 0;

    public void OnShown()
    {
        if (!_scanned) _ = Scan();
    }

    private async Task Scan()
    {
        Scanning = true;
        try
        {
            var list = await Task.Run(GameScanner.Scan);
            Games.Clear();
            foreach (var g in list) Games.Add(g);
            _scanned = true;
        }
        finally { Scanning = false; }
    }

    private void Add()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Escolha o executável do jogo",
            Filter = "Executável (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        if (Games.Any(g => g.ExePath.Equals(dlg.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            UiHost.Toast("Esse jogo já está na lista.", Tone.Info);
            return;
        }
        var manual = GameScanner.LoadManual();
        manual.Add(dlg.FileName);
        GameScanner.SaveManual(manual);
        Games.Add(GameScanner.Manual(dlg.FileName));
        OnPropertyChanged(nameof(Empty));
        UiHost.Toast("Jogo adicionado.", Tone.Ok);
    }

    private async Task Remove(GameEntry? g)
    {
        if (g == null) return;
        await Task.Run(() => { foreach (var t in g.Tweaks.Where(t => t.IsApplied)) t.Revert(); });
        if (g.IsManual)
        {
            var manual = GameScanner.LoadManual();
            manual.RemoveAll(p => p.Equals(g.ExePath, StringComparison.OrdinalIgnoreCase));
            GameScanner.SaveManual(manual);
            Games.Remove(g);
            OnPropertyChanged(nameof(Empty));
        }
        UiHost.Toast($"Ajustes de {g.Name} revertidos.", Tone.Info);
    }
}

// =====================================================================
// 05 LIMPEZA
// =====================================================================
public sealed class CleanupViewModel : ObservableObject, IPage
{
    public CleanupViewModel()
    {
        foreach (var t in Cleaner.Targets())
        {
            t.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(CleanTarget.Selected) or nameof(CleanTarget.Size)) UpdateTotal(); };
            Targets.Add(t);
        }
        ScanCommand = new RelayCommand(_ => Scan());
        CleanCommand = new RelayCommand(_ => Clean());
        DnsCommand = new RelayCommand(async _ =>
        {
            var ok = await Task.Run(SystemActions.FlushDns);
            UiHost.Toast(ok ? "Cache DNS limpo." : "Não foi possível limpar o cache DNS.", ok ? Tone.Ok : Tone.Bad);
            if (ok) Journal.Log("Cache DNS limpo", "clean");
        });
        StandbyCommand = new RelayCommand(async _ =>
        {
            var ok = await Task.Run(SystemActions.PurgeStandbyList);
            UiHost.Toast(ok ? "Memória em espera liberada." : "O Windows recusou liberar a memória em espera.", ok ? Tone.Ok : Tone.Bad);
            if (ok) Journal.Log("Memória em espera liberada", "clean");
        });
    }

    public ObservableCollection<CleanTarget> Targets { get; } = [];
    public RelayCommand ScanCommand { get; }
    public RelayCommand CleanCommand { get; }
    public RelayCommand DnsCommand { get; }
    public RelayCommand StandbyCommand { get; }

    private string _total = "—";
    public string Total { get => _total; set => Set(ref _total, value); }
    private bool _scanned;
    public bool Scanned { get => _scanned; set => Set(ref _scanned, value); }
    private bool _working;
    public bool Working { get => _working; set => Set(ref _working, value); }

    public void OnShown() { if (!Scanned && !Working) _ = Scan(); }

    private void UpdateTotal()
    {
        var sel = Targets.Where(t => t.Selected && t.Size > 0).Sum(t => t.Size);
        Total = Scanned ? SystemInfo.Bytes(sel) : "—";
    }

    private async Task Scan()
    {
        Working = true;
        try
        {
            foreach (var t in Targets)
            {
                var target = t;
                target.Size = await Task.Run(() => Cleaner.Measure(target));
            }
            Scanned = true;
            UpdateTotal();
        }
        finally { Working = false; }
    }

    private async Task Clean()
    {
        var sel = Targets.Where(t => t.Selected).ToList();
        if (sel.Count == 0) { UiHost.Toast("Selecione pelo menos um item.", Tone.Info); return; }
        var warn = sel.Where(t => t.Warning != null).Select(t => $"{t.Name}: {t.Warning}").ToList();
        var ok = await UiHost.Confirm(new DialogRequest
        {
            Title = "Limpar arquivos",
            Message = $"Serão removidos aproximadamente {Total}. Arquivos em uso são ignorados automaticamente."
                      + (warn.Count > 0 ? "\n\nAtenção:" : ""),
            Items = warn.Count > 0 ? warn : sel.Select(t => t.Name).ToList(),
            ConfirmText = "Limpar agora",
            Danger = sel.Any(t => t.IsRecycleBin),
        });
        if (!ok) return;

        Working = true;
        try
        {
            long freed = 0;
            foreach (var t in sel)
            {
                var target = t;
                freed += await Task.Run(() => Cleaner.Clean(target));
                target.Size = await Task.Run(() => Cleaner.Measure(target));
            }
            Journal.Log($"Limpeza: {SystemInfo.Bytes(freed)} liberados", "clean");
            UiHost.Toast($"{SystemInfo.Bytes(freed)} liberados.", Tone.Ok);
            UpdateTotal();
            AppEvents.RaiseStateChanged();
        }
        finally { Working = false; }
    }
}

// =====================================================================
// 06 INICIALIZAÇÃO
// =====================================================================
public sealed class StartupViewModel : ObservableObject, IPage
{
    public StartupViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Load());
        ToggleCommand = new RelayCommand(async p =>
        {
            if (p is not StartupItem s) return;
            if (s.Enabled && s.KeepOn)
            {
                var ok = await UiHost.Confirm(new DialogRequest
                {
                    Title = $"Desligar {s.Name}?",
                    Message = "Este item parece ser anti-cheat, segurança do Windows ou driver de áudio. Desligá-lo pode impedir jogos de abrir (ex.: o Vanguard é obrigatório para o VALORANT) ou tirar o som. Tem certeza?",
                    ConfirmText = "Desligar mesmo assim",
                    Danger = true,
                });
                if (!ok) { s.Enabled = true; return; }
            }
            try { StartupManager.SetEnabled(s, !s.Enabled); UiHost.Toast($"{s.Name}: {(s.Enabled ? "ativado" : "não abre mais com o Windows")}.", s.Enabled ? Tone.Info : Tone.Ok); }
            catch (Exception ex) { s.Enabled = s.Enabled; UiHost.Error(ex); }
            UpdateSummary();
        });
    }

    public ObservableCollection<StartupItem> Items { get; } = [];
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleCommand { get; }
    private string _summary = "";
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    public void OnShown() => _ = Load();

    private async Task Load()
    {
        var list = await Task.Run(StartupManager.Read);
        Items.Clear();
        foreach (var i in list) Items.Add(i);
        UpdateSummary();
    }

    private void UpdateSummary() => Summary = $"{Items.Count(i => i.Enabled)} de {Items.Count} programas abrem junto com o Windows";
}

// =====================================================================
// 07 BACKUP
// =====================================================================
public sealed class BackupViewModel : ObservableObject, IPage
{
    public BackupViewModel()
    {
        RestorePointCommand = new RelayCommand(_ => CreateRestorePoint());
        UndoAllCommand = new RelayCommand(_ => UndoAll());
        OpenRestoreCommand = new RelayCommand(_ => { try { Process.Start(Shell.Sys("rstrui.exe")); } catch (Exception ex) { UiHost.Error(ex); } });
        OpenFolderCommand = new RelayCommand(_ => Shell.Open(AppPaths.MachineData));
        AppEvents.StateChanged += Load;
    }

    public RelayCommand RestorePointCommand { get; }
    public RelayCommand UndoAllCommand { get; }
    public RelayCommand OpenRestoreCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public ObservableCollection<HistoryEntry> History { get; } = [];
    private int _pending;
    public int Pending { get => _pending; set => Set(ref _pending, value); }
    public string PendingLabel => Pending == 0 ? "Nenhuma alteração ativa" : $"{Pending} alteração(ões) ativa(s) com backup";

    public void OnShown() => Load();

    private void Load()
    {
        History.Clear();
        foreach (var h in Journal.History().Take(120)) History.Add(h);
        Pending = Journal.ActiveChanges();
        OnPropertyChanged(nameof(PendingLabel));
    }

    private async Task CreateRestorePoint()
    {
        var (ok, msg) = await UiHost.Busy("Criando ponto de restauração… isso pode levar até 1 minuto", () => RestorePoint.Create("Hertzo — ponto manual"));
        if (ok) { UiHost.Toast(msg, Tone.Ok); Load(); return; }

        var enable = await UiHost.Confirm(new DialogRequest
        {
            Title = "Ponto de restauração não criado",
            Message = msg + "\n\nDeseja ativar a Proteção do Sistema no disco do Windows e tentar de novo? Ela reserva uma parte do disco para os pontos de restauração.",
            ConfirmText = "Ativar e tentar de novo",
        });
        if (!enable) return;
        var (eok, emsg) = await UiHost.Busy("Ativando a Proteção do Sistema…", RestorePoint.EnableProtection);
        if (!eok) { UiHost.Toast(emsg, Tone.Bad); return; }
        var (ok2, msg2) = await UiHost.Busy("Criando ponto de restauração…", () => RestorePoint.Create("Hertzo — ponto manual"));
        UiHost.Toast(msg2, ok2 ? Tone.Ok : Tone.Bad);
        Load();
    }

    private async Task UndoAll()
    {
        var ok = await UiHost.Confirm(new DialogRequest
        {
            Title = "Desfazer tudo",
            Message = "Todas as alterações feitas pelo Hertzo voltam ao valor original que estava no seu PC antes (registro, serviços, plano de energia e ajustes por jogo). Programas desativados na inicialização continuam como estão.",
            ConfirmText = "Desfazer tudo",
            Danger = true,
        });
        if (!ok) return;

        var (count, errors) = await UiHost.Busy("Desfazendo alterações…", () =>
        {
            int n = 0; var errs = new List<string>();
            foreach (var t in TweakCatalog.All)
            {
                try
                {
                    t.Refresh();
                    if (t.HasHertzoBackup) { t.Revert(); n++; }
                }
                catch (Exception ex) { errs.Add(t.Title + ": " + ex.Message); }
            }
            foreach (var owner in Journal.Owners())
            {
                try { Journal.RestoreOwner(owner); n++; }
                catch (Exception ex) { errs.Add(owner + ": " + ex.Message); }
            }
            Journal.Log($"Desfazer tudo: {n} item(ns) restaurados", "revert");
            return (n, errs);
        });

        UiHost.Toast(errors.Count == 0 ? $"{count} alteração(ões) desfeitas. Reinicie o PC para concluir." : $"{count} desfeitas, {errors.Count} com erro: {errors[0]}",
            errors.Count == 0 ? Tone.Ok : Tone.Warn);
        AppEvents.RaiseStateChanged();
        Load();
    }
}

// =====================================================================
// 08 TRANSPARÊNCIA
// =====================================================================
public sealed record InfoItem(string Title, string Detail);
public sealed record SourceItem(string Title, string Url);

public sealed class TransparencyViewModel : IPage
{
    public RelayCommand OpenCommand { get; } = new(p => { if (p is string u) Shell.OpenUrl(u); });

    public InfoItem[] Never { get; } =
    [
        new("Não desativa antivírus, Firewall ou Windows Update", "Eles quase não pesam em jogo e são sua principal proteção. Otimizadores que desligam isso deixam o PC exposto."),
        new("Não mexe em anti-cheat nem em arquivos de jogos", "Nada é injetado em processos. O Hertzo só usa configurações oficiais do Windows."),
        new("Não usa bcdedit (HPET, dynamic tick, platform clock)", "Mexer no timer do boot costuma piorar a latência em CPUs modernas e pode conflitar com anti-cheats."),
        new("Não desativa mitigações de Spectre/Meltdown", "O ganho é mínimo em jogos e abre uma brecha de segurança real."),
        new("Não “limpa a RAM” dos programas", "Esvaziar a memória de apps em uso força o Windows a recarregar tudo do disco. Só oferecemos a lista em espera, sob demanda."),
        new("Não remove apps do sistema", "Remover a Store, o Xbox ou serviços do Windows quebra o Game Pass, as atualizações e alguns jogos."),
        new("Não desativa o arquivo de paginação", "Sem ele, jogos que passam do limite de RAM simplesmente fecham."),
    ];

    public InfoItem[] Myths { get; } =
    [
        new("“Trocar o DNS diminui o ping”", "O DNS só traduz nomes em endereços antes de conectar. A rota até o servidor do jogo não muda."),
        new("“Desativar o algoritmo de Nagle reduz a latência”", "VALORANT, CS2, Fortnite e quase todo FPS competitivo usam UDP, e o Nagle só afeta TCP."),
        new("“Prioridade Tempo Real deixa o jogo mais rápido”", "Pode travar o mouse, o teclado e o áudio, porque o jogo passa na frente até dos drivers."),
        new("“Liberar todos os núcleos no msconfig”", "Essa opção só serve para limitar núcleos em testes. O padrão já usa todos."),
        new("“Ajustes de registro dão +50% de FPS”", "A maioria dos tweaks populares não tem teste controlado. Por isso cada ajuste do Hertzo mostra o nível de evidência."),
        new("“Desativar o SysMain (Superfetch) aumenta o FPS”", "O SysMain pré-carrega na RAM os programas que você mais usa. Em SSD o efeito é pequeno para os dois lados; desligá-lo não dá FPS e pode deixar a abertura de apps mais lenta."),
        new("“Apagar a pasta Prefetch deixa o PC mais rápido”", "O Prefetch existe justamente para acelerar a abertura de programas. Apagar faz o Windows reconstruir tudo e o próximo boot fica mais lento."),
        new("“Esse ajuste deu +20 FPS”", "Sem medir antes e depois no mesmo cenário não dá para saber. Duas medições iguais já variam alguns por cento sozinhas; a Medição do Hertzo mostra quando a diferença é só isso."),
    ];

    public SourceItem[] Sources { get; } =
    [
        new("Microsoft — Otimizações para jogos em janela", "https://support.microsoft.com/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952"),
        new("Riot — Requisitos de segurança do Vanguard", "https://support.riotgames.com/riot/performance/vanguard-security-requirements"),
        new("FACEIT — Requisitos de segurança do Windows", "https://support.faceit.com/hc/en-us/articles/23117181142556"),
        new("Tom's Hardware — Modo On-Demand do Vanguard", "https://www.tomshardware.com/video-games/pc-gaming/riot-vanguard-adds-an-on-demand-mode-that-stops-anti-cheat-loading-at-boot-on-secured-windows-11-pcs"),
        new("PCGamingWiki — Resolução de timer global no Windows 11", "https://community.pcgamingwiki.com/files/file/2563-global-timer-resolution-requests-windows-11"),
        new("Intel PresentMon 2.6.0 (MIT) — usado na Medição de FPS", "https://github.com/GameTechDev/PresentMon"),
        new("Vortice.Windows 3.8.3 (MIT) — acesso ao DirectX e Media Foundation nos Clipes", "https://github.com/amerkoleci/Vortice.Windows"),
        new("Microsoft — Windows.Graphics.Capture (captura dos Clipes)", "https://learn.microsoft.com/windows/uwp/audio-video-camera/screen-capture"),
        new("Microsoft — Políticas do Edge: StartupBoostEnabled", "https://learn.microsoft.com/deployedge/microsoft-edge-policies#startupboostenabled"),
        new("Microsoft — Suspensão seletiva de USB", "https://learn.microsoft.com/windows-hardware/drivers/usbcon/usb-selective-suspend"),
    ];

    public void OnShown() { }
}
