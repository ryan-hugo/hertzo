using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using Hertzo.Core;

namespace Hertzo.ViewModels;

// =====================================================================
// 05 MEDIÇÃO DE FPS
// =====================================================================
public sealed class BenchTarget : ObservableObject
{
    public required string Name { get; init; }
    public required string Exe { get; init; }
    public required string Short { get; init; }
    public string Source { get; init; } = "";
    public GameEntry? Game { get; init; }

    private bool _running;
    public bool IsRunning { get => _running; set { if (Set(ref _running, value)) OnPropertyChanged(nameof(Status)); } }
    public string Status => IsRunning ? "ABERTO AGORA" : Source.ToUpperInvariant();

    private bool _sel;
    public bool IsSelected { get => _sel; set { if (Set(ref _sel, value) && value) Selected?.Invoke(this); } }
    public event Action<BenchTarget>? Selected;
}

public sealed class DurationOption : ObservableObject
{
    public required int Seconds { get; init; }
    public string Label => $"{Seconds} S";
    private bool _sel;
    public bool IsSelected { get => _sel; set { if (Set(ref _sel, value) && value) Selected?.Invoke(this); } }
    public event Action<DurationOption>? Selected;
}

public sealed class BenchRunVm : ObservableObject
{
    public BenchRun Run { get; }
    public BenchRunVm(BenchRun r) { Run = r; Notes = BenchAdvisor.Diagnose(r.Stats); }

    private FrameStats S => Run.Stats;
    public string Game => Run.Game.ToUpperInvariant();
    public string Code => $"#{Run.Number:00}";
    public string WhenLabel => Run.When.ToString("dd/MM HH:mm");
    public string Avg => $"{S.AvgFps:0}";
    public string Low1 => $"{S.Low1:0}";
    public string Low01 => $"{S.Low01:0}";
    public string P99 => $"{S.P99Ms:0.0}";
    public string Stutter => $"{S.StuttersPerMin:0.#}";
    public string Meta => $"{S.Seconds:0} S  ·  {S.Frames:N0} QUADROS  ·  {(S.Runtime.Length > 0 ? S.Runtime.ToUpperInvariant() : "API ?")}  ·  {S.RefreshHz} HZ";
    public string Plan => Run.PowerPlan.Length > 0 ? $"Plano de energia: {Run.PowerPlan}" : "";
    public List<BenchNote> Notes { get; }
    public float[] Peaks => S.Peaks;
    public double RefreshMs => S.RefreshHz > 0 ? 1000.0 / S.RefreshHz : 0;
    public List<string> Tweaks => Run.Tweaks;
    public string TweaksLabel => Run.Tweaks.Count == 0 ? "NENHUM AJUSTE DO HERTZO ATIVO" : $"{Run.Tweaks.Count} AJUSTE(S) DO HERTZO ATIVO(S) NESTA MEDIÇÃO";
    public bool HasCsv => Run.CsvPath != null && File.Exists(Run.CsvPath);

    private bool _a, _b, _cur;
    public bool IsA { get => _a; set => Set(ref _a, value); }
    public bool IsB { get => _b; set => Set(ref _b, value); }
    public bool IsCurrent { get => _cur; set => Set(ref _cur, value); }
}

public sealed record CompareRow(string Label, string A, string B, string Delta, Tone Tone, string Verdict);

public sealed class BenchCompare
{
    public BenchRunVm A { get; }
    public BenchRunVm B { get; }
    public List<CompareRow> Rows { get; } = [];
    public string Headline { get; }
    public Tone HeadlineTone { get; }
    public string Sub { get; }
    public List<string> Added { get; }
    public List<string> Removed { get; }
    public bool NoTweakChange => Added.Count == 0 && Removed.Count == 0;
    public bool HasAdded => Added.Count > 0;
    public bool HasRemoved => Removed.Count > 0;
    public bool DifferentGame { get; }
    public string Title => $"{A.Code} × {B.Code}";

    // Variação normal entre duas medições iguais. Abaixo disso não dá para afirmar que algo mudou.
    private const double NoiseAvg = 3, NoiseLow = 6, NoiseLow01 = 10;

    public BenchCompare(BenchRunVm a, BenchRunVm b)
    {
        A = a; B = b;
        var sa = a.Run.Stats; var sb = b.Run.Stats;
        DifferentGame = !a.Run.Exe.Equals(b.Run.Exe, StringComparison.OrdinalIgnoreCase);

        var avg = Rel("FPS MÉDIO", sa.AvgFps, sb.AvgFps, "0", true, NoiseAvg);
        Rows.Add(avg);
        var low = Rel("1% LOW", sa.Low1, sb.Low1, "0", true, NoiseLow);
        Rows.Add(low);
        Rows.Add(Rel("0,1% LOW", sa.Low01, sb.Low01, "0", true, NoiseLow01));
        Rows.Add(Rel("TEMPO DE QUADRO P99", sa.P99Ms, sb.P99Ms, "0.0", false, NoiseLow, " ms"));
        Rows.Add(Abs("ENGASGOS / MIN", sa.StuttersPerMin, sb.StuttersPerMin, 2));
        if (sa.LatencyMs is double la && sb.LatencyMs is double lb)
            Rows.Add(Rel("ATRASO CLIQUE → TELA", la, lb, "0", false, 8, " ms"));

        double d = Pct(sa.AvgFps, sb.AvgFps);
        if (avg.Tone == Tone.Ok) { Headline = $"+{d:0.#}% DE FPS MÉDIO"; HeadlineTone = Tone.Ok; }
        else if (avg.Tone == Tone.Bad) { Headline = $"{d:0.#}% DE FPS MÉDIO"; HeadlineTone = Tone.Bad; }
        else { Headline = "SEM DIFERENÇA REAL"; HeadlineTone = Tone.Neutral; }

        Sub = low.Tone switch
        {
            Tone.Ok => $"O 1% low também melhorou ({low.Delta}): menos quedas bruscas de FPS.",
            Tone.Bad => $"O 1% low piorou ({low.Delta}): as quedas de FPS ficaram mais fortes.",
            _ => avg.Tone == Tone.Neutral
                ? $"A diferença ({avg.Delta}) está dentro da variação normal entre duas medições (±{NoiseAvg:0}% no FPS médio, ±{NoiseLow:0}% no 1% low)."
                : "O 1% low ficou dentro da variação normal.",
        };

        Added = b.Run.Tweaks.Except(a.Run.Tweaks).ToList();
        Removed = a.Run.Tweaks.Except(b.Run.Tweaks).ToList();
    }

    private static double Pct(double a, double b) => a == 0 ? 0 : (b - a) / a * 100;

    private static CompareRow Rel(string label, double a, double b, string fmt, bool higherBetter, double noise, string unit = "")
    {
        var d = Pct(a, b);
        var (tone, verdict) = Math.Abs(d) < noise ? (Tone.Neutral, "RUÍDO")
            : (d > 0) == higherBetter ? (Tone.Ok, "MELHOR") : (Tone.Bad, "PIOR");
        return new(label, a.ToString(fmt) + unit, b.ToString(fmt) + unit, d.ToString("+0.0;-0.0;0.0") + "%", tone, verdict);
    }

    private static CompareRow Abs(string label, double a, double b, double noise)
    {
        var d = b - a;
        var (tone, verdict) = Math.Abs(d) < noise ? (Tone.Neutral, "RUÍDO") : d < 0 ? (Tone.Ok, "MELHOR") : (Tone.Bad, "PIOR");
        return new(label, a.ToString("0.#"), b.ToString("0.#"), d.ToString("+0.#;-0.#;0"), tone, verdict);
    }
}

public sealed class BenchViewModel : ObservableObject, IPage
{
    private enum Phase { Idle, Armed, Countdown, Recording, Processing }

    private Phase _phase;
    private readonly GlobalHotkey _hotkey = new();
    private FrameCapture? _cap;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _countTimer;
    private int _count;
    private bool _targetsLoaded;
    private readonly List<BenchRun> _all;
    private BenchRunVm? _a, _b;

    public BenchViewModel()
    {
        foreach (var s in new[] { 30, 60, 90, 120 })
        {
            var d = new DurationOption { Seconds = s };
            d.Selected += x => { foreach (var o in Durations) if (o != x) o.IsSelected = false; OnPropertyChanged(nameof(SelectedDuration)); UpdateIdleText(); };
            Durations.Add(d);
        }
        Durations[1].IsSelected = true;

        _all = BenchStore.Load();
        foreach (var r in _all.OrderByDescending(r => r.When)) Runs.Add(new BenchRunVm(r));
        if (Runs.Count > 0) Show(Runs[0]);
        var prev = Runs.Skip(1).FirstOrDefault(r => Runs.Count > 0 && SameExe(r, Runs[0]));
        if (prev != null) SetCompare(prev, Runs[0]);

        ArmCommand = new RelayCommand(_ => Arm());
        DelayCommand = new RelayCommand(_ => StartCountdown());
        CancelCommand = new RelayCommand(_ => Cancel());
        RefreshTargetsCommand = new RelayCommand(_ => LoadTargets());
        ShowCommand = new RelayCommand(p => { if (p is BenchRunVm r) Show(r); });
        SetACommand = new RelayCommand(p => { if (p is BenchRunVm r) PickA(r); });
        SetBCommand = new RelayCommand(p => { if (p is BenchRunVm r) PickB(r); });
        DeleteCommand = new RelayCommand(p => { if (p is BenchRunVm r) Delete(r); });
        OpenCsvCommand = new RelayCommand(p =>
        {
            if (p is BenchRunVm { HasCsv: true } r && FrameCapture.IsOurCsv(r.Run.CsvPath)) Shell.Reveal(r.Run.CsvPath!);
        });

        _hotkey.Pressed += OnHotkey;
        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        poll.Tick += (_, _) => PollRunning();
        poll.Start();
        UpdateIdleText();
    }

    public RelayCommand ArmCommand { get; }
    public RelayCommand DelayCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RefreshTargetsCommand { get; }
    public RelayCommand ShowCommand { get; }
    public RelayCommand SetACommand { get; }
    public RelayCommand SetBCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand OpenCsvCommand { get; }

    public ObservableCollection<BenchTarget> Targets { get; } = [];
    public ObservableCollection<BenchTarget> GameTargets { get; } = [];
    public ObservableCollection<BenchTarget> OtherTargets { get; } = [];
    public string OthersLabel => $"OUTROS PROGRAMAS ABERTOS ({OtherTargets.Count})";
    public bool HasOthers => OtherTargets.Count > 0;
    private bool _showOthers;
    public bool ShowOthers { get => _showOthers; set => Set(ref _showOthers, value); }
    public ObservableCollection<DurationOption> Durations { get; } = [];
    public ObservableCollection<BenchRunVm> Runs { get; } = [];

    public BenchTarget? SelectedTarget => Targets.FirstOrDefault(t => t.IsSelected);
    public DurationOption SelectedDuration => Durations.FirstOrDefault(d => d.IsSelected) ?? Durations[1];
    public string HotkeyLabel => GlobalHotkey.Label;
    public string MethodText => BenchAdvisor.Method(SelectedTarget?.Exe ?? "");

    private bool _loadingTargets;
    public bool LoadingTargets { get => _loadingTargets; set => Set(ref _loadingTargets, value); }

    private BenchRunVm? _current;
    public BenchRunVm? Current { get => _current; set { Set(ref _current, value); OnPropertyChanged(nameof(HasRuns)); OnPropertyChanged(nameof(NoRuns)); OnPropertyChanged(nameof(GraphCompare)); OnPropertyChanged(nameof(GraphCompareLabel)); } }
    public bool HasRuns => Current != null;
    public bool NoRuns => Current == null;

    private BenchCompare? _compare;
    public BenchCompare? Compare { get => _compare; set { Set(ref _compare, value); OnPropertyChanged(nameof(HasCompare)); OnPropertyChanged(nameof(GraphCompare)); OnPropertyChanged(nameof(GraphCompareLabel)); } }

    /// <summary>A outra medição da comparação, desenhada tracejada no gráfico.</summary>
    public float[]? GraphCompare => Other()?.Peaks;
    public string GraphCompareLabel => Other() is { } o ? $"MEDIÇÃO {o.Code}" : "";
    private BenchRunVm? Other() => Compare == null || Current == null ? null : Current == Compare.B ? Compare.A : Current == Compare.A ? Compare.B : null;
    public bool HasCompare => Compare != null;
    public bool CanCompareHint => Runs.Count >= 2 && Compare == null;

    // ---- painel de estado ----
    private string _phaseLabel = "PRONTO", _big = "--", _unit = "FPS", _caption = "", _detail = "", _time = "";
    private Tone _phaseTone = Tone.Neutral;
    private double _progress;
    public string PhaseLabel { get => _phaseLabel; set => Set(ref _phaseLabel, value); }
    public Tone PhaseTone { get => _phaseTone; set => Set(ref _phaseTone, value); }
    public string BigValue { get => _big; set => Set(ref _big, value); }
    public string BigUnit { get => _unit; set => Set(ref _unit, value); }
    public string Caption { get => _caption; set => Set(ref _caption, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string TimeLabel { get => _time; set => Set(ref _time, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public bool IsIdle => _phase == Phase.Idle;
    public bool IsActive => _phase != Phase.Idle;

    private void SetPhase(Phase p)
    {
        _phase = p;
        (PhaseLabel, PhaseTone) = p switch
        {
            Phase.Armed => ("ARMADO", Tone.Warn),
            Phase.Countdown => ("CONTAGEM", Tone.Warn),
            Phase.Recording => ("MEDINDO", Tone.Accent),
            Phase.Processing => ("CALCULANDO", Tone.Info),
            _ => ("PRONTO", Tone.Neutral),
        };
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsActive));
        if (p == Phase.Idle) UpdateIdleText();
    }

    private void UpdateIdleText()
    {
        if (_phase != Phase.Idle) return;
        var t = SelectedTarget;
        var last = t == null ? null : Runs.FirstOrDefault(r => r.Run.Exe.Equals(t.Exe, StringComparison.OrdinalIgnoreCase));
        BigValue = last?.Avg ?? SelectedDuration.Seconds.ToString();
        BigUnit = last != null ? "FPS" : "S";
        Progress = 0;
        TimeLabel = $"00:00 / {Fmt(SelectedDuration.Seconds)}";
        Caption = t == null ? "ESCOLHA UM JOGO"
            : last != null ? $"ÚLTIMA MEDIÇÃO · {t.Name.ToUpperInvariant()}"
            : $"{t.Name.ToUpperInvariant()} · PRONTO PARA MEDIR";
        Detail = t == null
            ? "Escolha ao lado o jogo que você quer medir."
            : $"Arme a medição, entre no jogo e aperte {GlobalHotkey.Label} no ponto de teste. Ou use “Começar em 10 s”.";
    }

    private static string Fmt(double s) => TimeSpan.FromSeconds(s).ToString(@"mm\:ss");

    public void OnShown()
    {
        if (!_targetsLoaded) LoadTargets();
    }

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "Hertzo", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "ShellExperienceHost",
        "StartMenuExperienceHost", "SearchHost", "LockApp", "dwm", "taskmgr", "conhost", "WindowsTerminal", "cmd", "powershell", "pwsh",
    };

    private async void LoadTargets()
    {
        if (LoadingTargets) return;
        LoadingTargets = true;
        try
        {
            var prev = SelectedTarget?.Exe;
            var games = await Task.Run(GameScanner.Scan);
            var windows = await Task.Run(() =>
            {
                var list = new List<(string Name, string Exe)>();
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero || p.SessionId == 0 || Skip.Contains(p.ProcessName)) continue;
                        var title = p.MainWindowTitle.Trim();
                        if (title.Length == 0) continue;
                        list.Add((title.Length > 34 ? title[..34] + "…" : title, p.ProcessName + ".exe"));
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                return list;
            });

            Targets.Clear();
            GameTargets.Clear();
            OtherTargets.Clear();
            foreach (var g in games)
                Add(new BenchTarget { Name = g.Name, Exe = g.ExeName, Short = g.Short, Source = g.Source, Game = g });
            foreach (var w in windows.DistinctBy(w => w.Exe.ToLowerInvariant()))
                if (!Targets.Any(t => t.Exe.Equals(w.Exe, StringComparison.OrdinalIgnoreCase)))
                    Add(new BenchTarget { Name = w.Name, Exe = w.Exe, Short = Abbr(w.Exe), Source = "Janela aberta" });

            var sel = Targets.FirstOrDefault(t => t.Exe.Equals(prev ?? "", StringComparison.OrdinalIgnoreCase))
                      ?? Targets.FirstOrDefault(t => t.Game != null && t.IsRunning)
                      ?? Targets.FirstOrDefault(t => t.Game != null);
            if (sel != null) sel.IsSelected = true;
            if (sel != null && sel.Game == null) ShowOthers = true;
            OnPropertyChanged(nameof(OthersLabel));
            OnPropertyChanged(nameof(HasOthers));
            _targetsLoaded = true;
        }
        catch (Exception ex) { UiHost.Error(ex); }
        finally { LoadingTargets = false; }
    }

    private void Add(BenchTarget t)
    {
        t.IsRunning = FrameCapture.IsRunning(t.Exe);
        t.Selected += x =>
        {
            foreach (var o in Targets) if (o != x) o.IsSelected = false;
            OnPropertyChanged(nameof(SelectedTarget));
            OnPropertyChanged(nameof(MethodText));
            UpdateIdleText();
        };
        Targets.Add(t);
        (t.Game != null ? GameTargets : OtherTargets).Add(t);
    }

    private static string Abbr(string exe)
    {
        var n = Path.GetFileNameWithoutExtension(exe).ToUpperInvariant();
        return n.Length <= 3 ? n : n[..3];
    }

    private void PollRunning()
    {
        foreach (var t in Targets.Where(t => t.Game != null || t.IsSelected)) t.IsRunning = FrameCapture.IsRunning(t.Exe);
    }

    // ---- fluxo ----
    private bool RequireTarget()
    {
        if (SelectedTarget != null) return true;
        UiHost.Toast("Escolha primeiro o jogo que você quer medir.", Tone.Info);
        return false;
    }

    private void Arm()
    {
        if (_phase != Phase.Idle || !RequireTarget()) return;
        if (!_hotkey.Register())
        {
            UiHost.Toast($"O atalho {GlobalHotkey.Label} está em uso por outro programa. Use “Começar em 10 s”.", Tone.Warn);
            return;
        }
        SetPhase(Phase.Armed);
        BigValue = "F9";
        BigUnit = "";
        Caption = "AGUARDANDO O ATALHO";
        Detail = $"Abra {SelectedTarget!.Name}, vá até o ponto de teste e aperte {GlobalHotkey.Label}. Um bipe: começou. Dois bipes: terminou. Apertar de novo encerra antes.";
    }

    private void OnHotkey()
    {
        switch (_phase)
        {
            case Phase.Armed:
                if (!FrameCapture.IsRunning(SelectedTarget!.Exe))
                {
                    Beeper.Fail();
                    Detail = $"{SelectedTarget.Name} não está aberto. Abra o jogo e aperte {GlobalHotkey.Label} de novo.";
                    return;
                }
                _ = Record();
                break;
            case Phase.Recording:
                Caption = "ENCERRANDO…";
                _cap?.StopEarly();
                break;
        }
    }

    private void StartCountdown()
    {
        if (_phase != Phase.Idle || !RequireTarget()) return;
        SetPhase(Phase.Countdown);
        _count = 10;
        BigValue = "10";
        BigUnit = "S";
        Caption = "COMEÇA EM";
        Detail = $"Volte para {SelectedTarget!.Name} agora. A medição começa sozinha; você ouve um bipe no começo e dois no fim.";
        _countTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countTimer.Tick += (_, _) =>
        {
            _count--;
            BigValue = _count.ToString();
            if (_count is > 0 and <= 3) Beeper.Tick();
            if (_count > 0) return;
            _countTimer!.Stop();
            if (!FrameCapture.IsRunning(SelectedTarget!.Exe))
            {
                Beeper.Fail();
                SetPhase(Phase.Idle);
                UiHost.Toast($"{SelectedTarget.Name} não estava aberto. Abra o jogo e tente de novo.", Tone.Warn);
                return;
            }
            _ = Record();
        };
        _countTimer.Start();
    }

    private void Cancel()
    {
        switch (_phase)
        {
            case Phase.Armed:
                _hotkey.Dispose();
                SetPhase(Phase.Idle);
                break;
            case Phase.Countdown:
                _countTimer?.Stop();
                SetPhase(Phase.Idle);
                break;
            case Phase.Recording:
                _cts?.Cancel();
                break;
        }
    }

    private async Task Record()
    {
        var target = SelectedTarget!;
        var seconds = SelectedDuration.Seconds;
        _hotkey.Register(); // permite encerrar antes com o mesmo atalho
        SetPhase(Phase.Recording);
        BigValue = seconds.ToString();
        BigUnit = "S";
        Caption = "INICIANDO O PRESENTMON";
        Progress = 0;
        TimeLabel = $"00:00 / {Fmt(seconds)}";
        Detail = $"Medindo {target.Name}. Jogue normalmente, sempre no mesmo trecho. Aperte {GlobalHotkey.Label} para encerrar antes.";
        _cts = new CancellationTokenSource();
        _cap = new FrameCapture();
        try
        {
            var (rows, csv) = await _cap.RunAsync(target.Exe, seconds,
                onStarted: () => { Beeper.Start(); Caption = "GRAVANDO · SEGUNDOS RESTANTES"; },
                onProgress: el =>
                {
                    BigValue = Math.Max(0, Math.Ceiling(seconds - el)).ToString("0");
                    Progress = el / seconds * 100;
                    TimeLabel = $"{Fmt(el)} / {Fmt(seconds)}";
                },
                _cts.Token);

            SetPhase(Phase.Processing);
            BigValue = "…";
            BigUnit = "";
            Caption = "CALCULANDO";
            var hz = await Task.Run(() => SystemInfo.Displays().FirstOrDefault()?.CurrentHz ?? 0);
            var stats = await Task.Run(() => FrameStats.Compute(rows, hz))
                ?? throw new InvalidOperationException("Poucos quadros gravados (menos de 30). Deixe o jogo aberto e renderizando durante a medição.");
            var (tweaks, plan) = await Task.Run(() => (SnapshotTweaks(target), Power.ActiveName()));

            var run = new BenchRun
            {
                Game = target.Name,
                Exe = target.Exe,
                Number = _all.Where(r => r.Exe.Equals(target.Exe, StringComparison.OrdinalIgnoreCase)).Select(r => r.Number).DefaultIfEmpty(0).Max() + 1,
                Duration = seconds,
                Stats = stats,
                Tweaks = tweaks,
                PowerPlan = plan,
                CsvPath = csv,
            };
            _all.Insert(0, run);
            BenchStore.Save(_all);

            var vm = new BenchRunVm(run);
            Runs.Insert(0, vm);
            Show(vm);
            var prev = Runs.Skip(1).FirstOrDefault(r => SameExe(r, vm));
            if (prev != null) SetCompare(prev, vm);
            Beeper.Done();
            UiHost.Toast($"Medição concluída: {stats.AvgFps:0} FPS médio, 1% low {stats.Low1:0}.", Tone.Ok);
            Journal.Log($"Medição de FPS: {target.Name} — {stats.AvgFps:0} FPS médio", "info");
        }
        catch (OperationCanceledException)
        {
            UiHost.Toast("Medição cancelada. Nada foi salvo.", Tone.Info);
        }
        catch (Exception ex)
        {
            Beeper.Fail();
            UiHost.Log(ex);
            await UiHost.Confirm(new DialogRequest
            {
                Title = "A medição não deu certo",
                Message = ex.Message,
                ConfirmText = "Entendi",
                CancelText = "Fechar",
            });
        }
        finally
        {
            _hotkey.Dispose();
            _cap = null;
            SetPhase(Phase.Idle);
            OnPropertyChanged(nameof(CanCompareHint));
        }
    }

    private static List<string> SnapshotTweaks(BenchTarget t)
    {
        var list = new List<string>();
        foreach (var tw in TweakCatalog.All)
        {
            try { tw.Refresh(); if (tw.IsApplied) list.Add(tw.Title); } catch { }
        }
        if (t.Game != null)
            foreach (var tw in t.Game.Tweaks)
            {
                try { tw.Refresh(); if (tw.IsApplied) list.Add($"{t.Game.Name}: {tw.Title}"); } catch { }
            }
        return list;
    }

    private static bool SameExe(BenchRunVm a, BenchRunVm b) => a.Run.Exe.Equals(b.Run.Exe, StringComparison.OrdinalIgnoreCase);

    // ---- histórico e comparação ----
    private void Show(BenchRunVm r)
    {
        foreach (var x in Runs) x.IsCurrent = x == r;
        Current = r;
    }

    private void SetCompare(BenchRunVm a, BenchRunVm b)
    {
        _a = a; _b = b;
        foreach (var x in Runs) { x.IsA = x == a; x.IsB = x == b; }
        Compare = new BenchCompare(a, b);
        OnPropertyChanged(nameof(CanCompareHint));
    }

    private void PickA(BenchRunVm r)
    {
        if (r == _b) _b = _a;
        _a = r;
        Recompute();
    }

    private void PickB(BenchRunVm r)
    {
        if (r == _a) _a = _b;
        _b = r;
        Recompute();
    }

    private void Recompute()
    {
        foreach (var x in Runs) { x.IsA = x == _a; x.IsB = x == _b; }
        Compare = _a != null && _b != null && _a != _b ? new BenchCompare(_a, _b) : null;
        OnPropertyChanged(nameof(CanCompareHint));
    }

    private void Delete(BenchRunVm r)
    {
        _all.Remove(r.Run);
        BenchStore.Save(_all);
        try { if (FrameCapture.IsOurCsv(r.Run.CsvPath)) File.Delete(r.Run.CsvPath!); } catch { }
        Runs.Remove(r);
        if (_a == r) _a = null;
        if (_b == r) _b = null;
        Recompute();
        if (Current == r) { if (Runs.Count > 0) Show(Runs[0]); else Current = null; }
        UiHost.Toast("Medição apagada do histórico.", Tone.Info);
    }
}
