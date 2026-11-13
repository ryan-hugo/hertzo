using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hertzo.Core;
using Hertzo.Ui;

namespace Hertzo.ViewModels;

// =====================================================================
// 06 CLIPES — replay instantâneo
// =====================================================================
public sealed class ChoiceOption : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Sub { get; init; } = "";
    private bool _sel;
    public bool IsSelected { get => _sel; set { if (Set(ref _sel, value) && value) Selected?.Invoke(this); } }
    public event Action<ChoiceOption>? Selected;
}

public sealed class ClipItem : ObservableObject
{
    public ClipInfo Info { get; }
    public ClipItem(ClipInfo info)
    {
        Info = info;
        try
        {
            var thumb = ClipIndex.ThumbFor(info.Path);
            if (File.Exists(thumb))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // não trava o arquivo
                bmp.UriSource = new Uri(thumb);
                bmp.EndInit();
                bmp.Freeze();
                Thumb = bmp;
            }
        }
        catch { }
    }

    public ImageSource? Thumb { get; }
    public bool HasThumb => Thumb != null;
    public string Title => (Info.Game.Length > 0 ? Info.Game : Path.GetFileNameWithoutExtension(Info.Path)).ToUpperInvariant();
    public string WhenLabel => Info.When.ToString("dd/MM HH:mm");
    public string Duration => Info.Seconds > 0 ? TimeSpan.FromSeconds(Math.Round(Info.Seconds)).ToString(@"m\:ss") : "--";
    public string Meta
    {
        get
        {
            var size = File.Exists(Info.Path) ? SystemInfo.Bytes(new FileInfo(Info.Path).Length) : "?";
            return Info.Height > 0 ? $"{Info.Height}P{Info.Fps}  ·  {size}" : size;
        }
    }
}

public sealed class ClipsViewModel : ObservableObject, IPage
{
    private readonly ClipSettings _s = ClipSettings.Load();
    private readonly GlobalHotkey _hotkey;
    private ReplayEngine? _engine;
    private string? _game;
    private DateTime _retryAfter = DateTime.MinValue;
    private bool _saving;
    private bool _loaded;
    private readonly HashSet<string> _gameExes;
    private readonly Dictionary<string, string> _gameNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly (int W, int H) _screen;

    public string HotkeyLabel => _s.SaveKey.Label;

    public ClipsViewModel()
    {
        var d0 = SystemInfo.Displays().FirstOrDefault();
        _screen = d0 != null ? (d0.Width, d0.Height) : (1920, 1080);

        _hotkey = new GlobalHotkey(0x5052, _s.SaveKey.Vk, _s.SaveKey.Mods);
        _recHotkey = new GlobalHotkey(0x5053, _s.RecordKey.Vk, _s.RecordKey.Mods);
        Beeper.Enabled = _s.Sounds;
        GameOverlay.Instance.Corner = _s.Overlay;
        GameOverlay.Instance.ShowRecIndicator = _s.OverlayRec;

        // ---- duração do replay ----
        foreach (var s in new[] { 15, 30, 60, 120, 300 })
        {
            var o = new ChoiceOption { Key = s.ToString(), Label = s < 60 ? $"{s} S" : $"{s / 60} MIN" };
            o.Selected += x => { Exclusive(SecondsOptions, x); SetSeconds(int.Parse(x.Key)); };
            SecondsOptions.Add(o);
        }
        SyncSeconds();

        // ---- qualidade / fps / resolução ----
        foreach (var (k, l) in new[] { ("leve", "LEVE"), ("padrao", "PADRÃO"), ("alta", "ALTA"), ("maxima", "MÁXIMA") })
        {
            var o = new ChoiceOption { Key = k, Label = l };
            o.IsSelected = k == _s.Quality;
            o.Selected += x => { Exclusive(QualityOptions, x); _s.Quality = x.Key; Changed(restart: true); };
            QualityOptions.Add(o);
        }
        foreach (var f in new[] { 30, 60, 120 })
        {
            var o = new ChoiceOption { Key = f.ToString(), Label = $"{f} FPS", Sub = f == 120 ? "exige mais da placa" : f == 30 ? "arquivo menor" : "recomendado" };
            o.IsSelected = f == _s.Fps;
            o.Selected += x => { Exclusive(FpsOptions, x); _s.Fps = int.Parse(x.Key); Changed(restart: true); };
            FpsOptions.Add(o);
        }
        var resList = new List<(int H, string L)> { (0, $"NATIVA · {_screen.H}P") };
        if (_screen.H > 1080) resList.Add((1080, "1080P"));
        if (_screen.H > 720) resList.Add((720, "720P"));
        foreach (var (h, l) in resList)
        {
            var o = new ChoiceOption { Key = h.ToString(), Label = l };
            o.IsSelected = h == _s.OutputHeight || (h == 0 && !resList.Any(r => r.H == _s.OutputHeight));
            o.Selected += x => { Exclusive(ResOptions, x); _s.OutputHeight = int.Parse(x.Key); Changed(restart: true); };
            ResOptions.Add(o);
        }

        // ---- aviso na tela do jogo ----
        foreach (var (k, l) in new[] { ("off", "DESLIGADO"), ("tl", "↖ SUP. ESQ."), ("tr", "↗ SUP. DIR."), ("bl", "↙ INF. ESQ."), ("br", "↘ INF. DIR.") })
        {
            var o = new ChoiceOption { Key = k, Label = l };
            o.IsSelected = k == _s.Overlay;
            o.Selected += x =>
            {
                Exclusive(OverlayOptions, x);
                _s.Overlay = x.Key; _s.Save();
                GameOverlay.Instance.HideAll();
                GameOverlay.Instance.Corner = x.Key;
                if (x.Key != "off") GameOverlay.Instance.Toast("PRÉVIA DO AVISO", "É ASSIM QUE APARECE NO JOGO");
            };
            OverlayOptions.Add(o);
        }

        // nomes de executáveis de jogos conhecidos + os adicionados à mão
        foreach (var d in GameScanner.Catalog)
            foreach (var e in d.Exes) _gameNames[Path.GetFileNameWithoutExtension(e)] = d.Name;
        foreach (var m in GameScanner.LoadManual())
            _gameNames.TryAdd(Path.GetFileNameWithoutExtension(m), Path.GetFileNameWithoutExtension(m));
        _gameExes = new HashSet<string>(_gameNames.Keys, StringComparer.OrdinalIgnoreCase);

        ToggleCommand = new RelayCommand(_ => Toggle());
        SaveCommand = new RelayCommand(_ => SaveClip());
        // Abre pelo Explorer (sem admin) e só se for um .mp4 de verdade: o índice de clipes fica numa pasta que outros programas podem editar.
        OpenCommand = new RelayCommand(p => { if (p is ClipItem c && Guard.IsClipFile(c.Info.Path)) Shell.Open(c.Info.Path); });
        RevealCommand = new RelayCommand(p => { if (p is ClipItem c && Guard.IsClipFile(c.Info.Path)) Shell.Reveal(c.Info.Path); });
        DeleteCommand = new RelayCommand(p => p is ClipItem c ? Delete(c) : Task.CompletedTask);
        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder());
        OpenFolderCommand = new RelayCommand(_ => { Directory.CreateDirectory(_s.Folder); Shell.Open(_s.Folder); });
        ApplyCustomCommand = new RelayCommand(_ => ApplyCustom());
        EditKeyCommand = new RelayCommand(p => BeginCapture(p as string));
        ResetKeysCommand = new RelayCommand(_ => ResetKeys());

        _hotkey.Pressed += SaveClip;
        _recHotkey.Pressed += () => _ = ToggleManual();
        _recHotkey.Register(); // se outro programa usar o atalho, o botão continua funcionando
        ManualCommand = new RelayCommand(_ => ToggleManual());
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => Tick();
        t.Start();

        InitAudio();
        if (_s.Armed) _hotkey.Register();
        Tick();
    }

    public RelayCommand ToggleCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ManualCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ApplyCustomCommand { get; }
    public RelayCommand EditKeyCommand { get; }
    public RelayCommand ResetKeysCommand { get; }

    public ObservableCollection<ChoiceOption> SecondsOptions { get; } = [];
    public ObservableCollection<ChoiceOption> QualityOptions { get; } = [];
    public ObservableCollection<ChoiceOption> FpsOptions { get; } = [];
    public ObservableCollection<ChoiceOption> ResOptions { get; } = [];
    public ObservableCollection<ChoiceOption> OverlayOptions { get; } = [];
    public ObservableCollection<ClipItem> Clips { get; } = [];
    public bool HasClips => Clips.Count > 0;
    public bool NoClips => Clips.Count == 0;

    public bool Armed => _s.Armed;
    public bool NotArmed => !_s.Armed;
    public bool OnlyInGame
    {
        get => _s.OnlyInGame;
        set { _s.OnlyInGame = value; _s.Save(); OnPropertyChanged(); Tick(); }
    }
    public bool Sounds
    {
        get => _s.Sounds;
        set { _s.Sounds = value; Beeper.Enabled = value; _s.Save(); OnPropertyChanged(); if (value) Beeper.Done(); }
    }
    public bool FolderPerGame
    {
        get => _s.FolderPerGame;
        set { _s.FolderPerGame = value; _s.Save(); OnPropertyChanged(); }
    }
    public bool OverlayRec
    {
        get => _s.OverlayRec;
        set { _s.OverlayRec = value; GameOverlay.Instance.ShowRecIndicator = value; _s.Save(); OnPropertyChanged(); }
    }
    // ---- áudio ----
    public ObservableCollection<ChoiceOption> MicOptions { get; } = [];
    public bool GameAudio { get => _s.GameAudio; set { _s.GameAudio = value; OnPropertyChanged(); AudioChanged(); } }
    public bool MicOn { get => _s.Mic; set { _s.Mic = value; OnPropertyChanged(); AudioChanged(); } }
    public bool MicTrack { get => _s.MicTrack; set { _s.MicTrack = value; OnPropertyChanged(); AudioChanged(); } }
    public double GameVolume
    {
        get => _s.GameVolume;
        set { _s.GameVolume = (int)Math.Round(value); OnPropertyChanged(); OnPropertyChanged(nameof(GameVolumeLabel)); LiveGains(); }
    }
    public double MicVolume
    {
        get => _s.MicVolume;
        set { _s.MicVolume = (int)Math.Round(value); OnPropertyChanged(); OnPropertyChanged(nameof(MicVolumeLabel)); LiveGains(); }
    }
    public string GameVolumeLabel => $"{_s.GameVolume}%";
    public string MicVolumeLabel => $"{_s.MicVolume}%";
    public string GameDeviceLabel => (AudioDevices.DefaultName(false) ?? "SAÍDA PADRÃO").ToUpperInvariant();

    private double _gameLevel, _micLevel;
    public double GameLevel { get => _gameLevel; set => Set(ref _gameLevel, value); }
    public double MicLevel { get => _micLevel; set => Set(ref _micLevel, value); }

    private AudioMixer? _testMixer;
    private DateTime _testUntil;
    public bool TestingAudio => _testMixer != null;
    public string TestLabel => _testMixer != null ? "PARAR TESTE" : "TESTAR ÁUDIO";
    public RelayCommand TestAudioCommand { get; private set; } = null!;
    public RelayCommand RefreshMicsCommand { get; private set; } = null!;
    private DispatcherTimer? _volSave;

    private void InitAudio()
    {
        TestAudioCommand = new RelayCommand(_ => ToggleTest());
        RefreshMicsCommand = new RelayCommand(_ => LoadMics());
        LoadMics();
        var meters = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        meters.Tick += (_, _) =>
        {
            var m = (_manualEngine ?? _engine)?.Mixer ?? _testMixer;
            GameLevel = m?.GameLevel ?? 0;
            MicLevel = m?.MicLevel ?? 0;
            if (_testMixer != null && DateTime.Now > _testUntil) ToggleTest();
        };
        meters.Start();
    }

    private void LoadMics()
    {
        MicOptions.Clear();
        var def = AudioDevices.DefaultName(true);
        var items = new List<(string Id, string Name)> { ("", "PADRÃO DO WINDOWS" + (def != null ? " · " + def : "")) };
        items.AddRange(AudioDevices.Microphones().Select(d => (d.Id, d.Name)));
        if (_s.MicDevice.Length > 0 && !items.Any(i => i.Id == _s.MicDevice)) _s.MicDevice = ""; // microfone desconectado: volta ao padrão
        foreach (var (id, name) in items)
        {
            var o = new ChoiceOption { Key = id, Label = name.ToUpperInvariant() };
            o.IsSelected = id == _s.MicDevice;
            o.Selected += x => { Exclusive(MicOptions, x); _s.MicDevice = x.Key; AudioChanged(); };
            MicOptions.Add(o);
        }
    }

    private void AudioChanged()
    {
        _s.Save();
        if (_testMixer != null) { ToggleTest(); ToggleTest(); }
        Changed(restart: true);
    }

    private void LiveGains()
    {
        double g = _s.GameVolume / 100.0, m = _s.MicVolume / 100.0;
        _engine?.Mixer?.SetGains(g, m);
        _manualEngine?.Mixer?.SetGains(g, m);
        _testMixer?.SetGains(g, m);
        // grava a configuração só quando o controle parar de mexer
        _volSave ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _volSave.Stop();
        _volSave.Tick -= SaveVol;
        _volSave.Tick += SaveVol;
        _volSave.Start();
    }

    private void SaveVol(object? sender, EventArgs e) { _volSave?.Stop(); _s.Save(); }

    /// <summary>Liga os medidores sem gravar nada (30 s), para conferir jogo e microfone.</summary>
    private void ToggleTest()
    {
        if (_testMixer != null)
        {
            var t = _testMixer; _testMixer = null;
            Task.Run(t.Dispose);
        }
        else if (_s.GameAudio || _s.Mic)
        {
            _testMixer = new AudioMixer(_s.GameAudio, _s.GameVolume / 100.0, _s.Mic, _s.MicVolume / 100.0, _s.MicDevice, false, AudioMixer.NowQpc100ns());
            _testUntil = DateTime.Now.AddSeconds(30);
        }
        else UiHost.Toast("Ligue o som do jogo ou o microfone primeiro.", Tone.Info);
        OnPropertyChanged(nameof(TestingAudio));
        OnPropertyChanged(nameof(TestLabel));
    }

    public string Folder => _s.Folder;
    public string ToggleLabel => _s.Armed ? "DESLIGAR REPLAY" : "LIGAR REPLAY";

    // ---- duração personalizada ----
    private string _customText = "";
    public string CustomText { get => _customText; set => Set(ref _customText, value); }
    public bool IsCustomSeconds => !SecondsOptions.Any(o => o.IsSelected);

    private void SyncSeconds()
    {
        foreach (var o in SecondsOptions) o.IsSelected = int.Parse(o.Key) == _s.Seconds;
        CustomText = _s.Seconds.ToString();
        OnPropertyChanged(nameof(IsCustomSeconds));
        OnPropertyChanged(nameof(Estimate));
        OnPropertyChanged(nameof(SaveLabel));
    }

    private void SetSeconds(int s)
    {
        _s.Seconds = Math.Clamp(s, ClipSettings.MinSeconds, ClipSettings.MaxSecondsLimit);
        SyncSeconds();
        Changed(restart: true);
    }

    private void ApplyCustom()
    {
        var txt = CustomText.Trim().ToLowerInvariant().Replace(" ", "");
        int secs;
        if (txt.EndsWith("min") && int.TryParse(txt[..^3], out var mm)) secs = mm * 60;
        else if (txt.EndsWith('m') && int.TryParse(txt[..^1], out var m2)) secs = m2 * 60;
        else if (txt.EndsWith('s') && int.TryParse(txt[..^1], out var s2)) secs = s2;
        else if (!int.TryParse(txt, out secs))
        {
            UiHost.Toast("Digite os segundos (ex.: 45) ou minutos (ex.: 4min).", Tone.Warn);
            return;
        }
        if (secs < ClipSettings.MinSeconds || secs > ClipSettings.MaxSecondsLimit)
            UiHost.Toast($"O replay aceita de {ClipSettings.MinSeconds} s a {ClipSettings.MaxSecondsLimit / 60} min.", Tone.Info);
        SetSeconds(secs);
    }

    /// <summary>Estimativa honesta de RAM e de tamanho do arquivo para a configuração atual.</summary>
    public string Estimate
    {
        get
        {
            var (w, h) = _s.EncodeSize(_screen.W, _screen.H);
            var mbps = _s.BitrateFor(w, h) / 1e6;
            var mb = mbps / 8 * _s.Seconds;
            return $"{h}P{_s.Fps} · ATÉ ~{mbps:0} MBPS · BUFFER DE {FormatDur(_s.Seconds)} ≈ {mb:0} MB DE RAM";
        }
    }

    private static string FormatDur(int s) => s < 60 ? $"{s} S" : s % 60 == 0 ? $"{s / 60} MIN" : $"{s / 60} MIN {s % 60} S";

    // ---- atalhos ----
    public string SaveKeyLabel => _s.SaveKey.Label;
    public string RecordKeyLabel => _s.RecordKey.Label;
    public string[] SaveKeyParts => _s.SaveKey.Keys;
    public string[] RecordKeyParts => _s.RecordKey.Keys;
    private string? _capturing;
    public bool CapturingSave => _capturing == "save";
    public bool CapturingRecord => _capturing == "record";
    public bool IsCapturingKey => _capturing != null;

    private void BeginCapture(string? which)
    {
        _capturing = which;
        // libera os atalhos enquanto o usuário escolhe, para a combinação não disparar a ação
        _hotkey.Dispose();
        _recHotkey.Dispose();
        KeyStateChanged();
    }

    private void KeyStateChanged()
    {
        OnPropertyChanged(nameof(CapturingSave));
        OnPropertyChanged(nameof(CapturingRecord));
        OnPropertyChanged(nameof(IsCapturingKey));
        OnPropertyChanged(nameof(SaveKeyLabel));
        OnPropertyChanged(nameof(RecordKeyLabel));
        OnPropertyChanged(nameof(SaveKeyParts));
        OnPropertyChanged(nameof(RecordKeyParts));
        OnPropertyChanged(nameof(HotkeyLabel));
        Tick();
    }

    private void EndCapture()
    {
        _capturing = null;
        if (_s.Armed) _hotkey.Register();
        _recHotkey.Register();
        KeyStateChanged();
    }

    /// <summary>Chamado pela tela com a tecla apertada enquanto um atalho está sendo escolhido.</summary>
    public void CaptureKey(uint mods, uint vk, bool escape)
    {
        if (_capturing == null) return;
        if (escape) { EndCapture(); return; }
        var spec = new HotkeySpec(mods, vk);
        bool fkey = vk is >= 0x70 and <= 0x87;
        if (mods == 0 && !fkey)
        {
            UiHost.Toast("Use pelo menos um modificador (Ctrl, Shift, Alt) ou uma tecla F1–F24.", Tone.Warn);
            return;
        }
        var other = _capturing == "save" ? _s.RecordKey : _s.SaveKey;
        if (spec.SameAs(other))
        {
            UiHost.Toast("Esse atalho já está sendo usado pela outra ação.", Tone.Warn);
            return;
        }
        if (!GlobalHotkey.IsFree(mods, vk))
        {
            UiHost.Toast($"{spec.Label} já é usado por outro programa. Escolha outra combinação.", Tone.Warn);
            return;
        }
        if (_capturing == "save") { _s.SaveKey = spec; _hotkey.Change(mods, vk); }
        else { _s.RecordKey = spec; _recHotkey.Change(mods, vk); }
        _s.Save();
        UiHost.Toast($"Atalho definido: {spec.Label}", Tone.Ok);
        EndCapture();
    }

    private void ResetKeys()
    {
        _s.SaveKey = new(HotkeySpec.Ctrl | HotkeySpec.Shift, 0x79);
        _s.RecordKey = new(HotkeySpec.Ctrl | HotkeySpec.Shift, 0x7A);
        _hotkey.Change(_s.SaveKey.Mods, _s.SaveKey.Vk);
        _recHotkey.Change(_s.RecordKey.Mods, _s.RecordKey.Vk);
        _s.Save();
        KeyStateChanged();
        UiHost.Toast("Atalhos de volta ao padrão: Ctrl+Shift+F10 e Ctrl+Shift+F11.", Tone.Info);
    }

    private string _diskLabel = "";
    private Tone _diskTone = Tone.Neutral;
    public string DiskLabel { get => _diskLabel; set => Set(ref _diskLabel, value); }
    public Tone DiskTone { get => _diskTone; set => Set(ref _diskTone, value); }

    // ---- instrumento ----
    private string _phase = "DESLIGADO", _big = "0", _caption = "", _detail = "", _spec = "";
    private Tone _tone = Tone.Neutral;
    private double _fill;
    public string PhaseLabel { get => _phase; set => Set(ref _phase, value); }
    public Tone PhaseTone { get => _tone; set => Set(ref _tone, value); }
    public string BigValue { get => _big; set => Set(ref _big, value); }
    public string Caption { get => _caption; set => Set(ref _caption, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Spec { get => _spec; set => Set(ref _spec, value); }
    public double Fill { get => _fill; set => Set(ref _fill, value); }
    public bool IsRecording => _engine?.Running == true;
    public string SaveLabel => $"SALVAR ÚLTIMOS {FormatDur(_s.Seconds)}";

    public void OnShown()
    {
        if (_loaded) return;
        _loaded = true;
        LoadClips();
    }

    private static void Exclusive(IEnumerable<ChoiceOption> list, ChoiceOption keep)
    {
        foreach (var o in list) if (o != keep) o.IsSelected = false;
    }

    private void Changed(bool restart)
    {
        _s.Save();
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(Estimate));
        if (restart && _manual)
        {
            UiHost.Toast("A nova configuração vale a partir da próxima gravação.", Tone.Info);
            return;
        }
        if (restart && _engine != null)
        {
            StopEngine();
            UiHost.Toast("Replay reiniciado com a nova configuração. O buffer começou do zero.", Tone.Info);
            Tick();
        }
    }

    // ---- liga / desliga ----
    private void Toggle()
    {
        _s.Armed = !_s.Armed;
        _s.Save();
        if (_s.Armed)
        {
            if (!_hotkey.Register())
                UiHost.Toast($"O atalho {HotkeyLabel} está em uso por outro programa. Use o botão de salvar.", Tone.Warn);
            _retryAfter = DateTime.MinValue;
        }
        else
        {
            _hotkey.Dispose();
            // a gravação manual em andamento continua usando a mesma captura até ser parada
            if (_manual && _manualEngine == null) { _manualEngine = _engine; _engine = null; }
            else StopEngine();
        }
        OnPropertyChanged(nameof(Armed));
        OnPropertyChanged(nameof(NotArmed));
        OnPropertyChanged(nameof(ToggleLabel));
        Tick();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// <summary>Jogo aberto agora — de preferência o da janela em primeiro plano.</summary>
    private string? RunningGame()
    {
        if (_gameExes.Count == 0) return null;
        GetWindowThreadProcessId(GetForegroundWindow(), out var fg);
        string? first = null;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!_gameExes.Contains(p.ProcessName)) continue;
                var name = _gameNames[p.ProcessName];
                if (p.Id == fg) return name;
                first ??= name;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return first;
    }

    private bool _starting;
    private DateTime _noGameSince = DateTime.MaxValue;

    private async void StartEngine()
    {
        if (_starting) return;
        _starting = true;
        try
        {
            var e = new ReplayEngine(_s);
            e.Faulted += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_manual && _manualEngine == null) ManualLost(e);
                _engine = null;
                _retryAfter = DateTime.Now.AddSeconds(20);
                UiHost.Toast(e.Error ?? "O replay parou.", Tone.Bad);
                Tick();
            });
            await Task.Run(e.Start);
            if (_s.Armed)
            {
                _engine = e;
                if (_game != null) GameOverlay.Instance.Toast("REPLAY ATIVO", $"{HotkeyLabel} SALVA OS ÚLTIMOS {FormatDur(_s.Seconds)}");
            }
            else e.Stop();
        }
        catch (Exception ex)
        {
            _engine = null;
            _retryAfter = DateTime.Now.AddSeconds(20);
            Detail = ex.Message;
            UiHost.Log(ex);
        }
        finally { _starting = false; Tick(); }
    }

    private void StopEngine()
    {
        var e = _engine;
        _engine = null;
        if (e != null) Task.Run(e.Stop);
    }

    private int _pollCount;

    private void Tick()
    {
        // a cada 2 s verifica se há jogo aberto (modo “só com jogo”)
        if (_pollCount++ % 2 == 0 || _game == null) _game = _s.Armed && _s.OnlyInGame ? RunningGame() : null;

        if (_s.Armed && !_saving)
        {
            // quando o jogo fecha, espera 30 s antes de descartar o buffer (dá tempo de salvar o final)
            if (_game != null) _noGameSince = DateTime.MaxValue;
            else if (_noGameSince == DateTime.MaxValue) _noGameSince = DateTime.Now;
            bool want = !_s.OnlyInGame || _game != null;
            bool linger = _s.OnlyInGame && _game == null && DateTime.Now - _noGameSince < TimeSpan.FromSeconds(30);
            if (want && _engine == null && _manualEngine == null && !_starting && DateTime.Now >= _retryAfter) StartEngine();
            else if (!want && !linger && _engine != null && !(_manual && _manualEngine == null)) StopEngine();
        }

        var e = _engine;
        var me = _manualEngine ?? _engine;
        if (_manual && me != null)
        {
            var secs = me.FileSeconds;
            long size = 0;
            try { if (me.FilePath != null && File.Exists(me.FilePath)) size = new FileInfo(me.FilePath).Length; } catch { }
            (PhaseLabel, PhaseTone) = ("GRAVANDO · MANUAL", Tone.Bad);
            BigValue = TimeSpan.FromSeconds(secs).ToString(secs >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
            Fill = secs % 60 / 60 * 100;
            Caption = $"GRAVANDO EM DISCO · {SystemInfo.Bytes(size)}";
            Detail = $"Aperte {ManualHotkeyLabel} para parar." + (e is { Running: true } ? " O replay continua guardando os últimos segundos ao mesmo tempo." : "");
            Spec = $"{me.Width}×{me.Height} · {me.Fps} FPS · {me.Encoder.ToUpperInvariant()}";
        }
        else if (!_s.Armed)
        {
            (PhaseLabel, PhaseTone) = ("DESLIGADO", Tone.Neutral);
            BigValue = "0"; Fill = 0;
            Caption = "SEGUNDOS NA MEMÓRIA";
            Detail = "Ligue o replay para o Hertzo guardar os últimos segundos de jogo. Nada é salvo em disco até você apertar o atalho.";
            Spec = "";
        }
        else if (e is { Running: true })
        {
            var secs = Math.Min(e.BufferedSeconds, _s.Seconds);
            (PhaseLabel, PhaseTone) = _saving ? ("SALVANDO", Tone.Info) : ("GRAVANDO", Tone.Accent);
            BigValue = secs.ToString("0");
            Fill = secs / _s.Seconds * 100;
            Caption = $"SEGUNDOS NA MEMÓRIA · {SystemInfo.Bytes(e.BufferedBytes)} DE RAM";
            Detail = (_game != null ? $"{_game} aberto. " : "") + $"Aperte {HotkeyLabel} para salvar os últimos {_s.Seconds} s.";
            Spec = $"{e.Width}×{e.Height} · {e.Fps} FPS · {e.Encoder.ToUpperInvariant()}";
        }
        else
        {
            (PhaseLabel, PhaseTone) = _retryAfter > DateTime.Now ? ("ERRO", Tone.Bad) : ("AGUARDANDO JOGO", Tone.Warn);
            BigValue = "0"; Fill = 0;
            Caption = "SEGUNDOS NA MEMÓRIA";
            if (_retryAfter <= DateTime.Now)
                Detail = _s.OnlyInGame
                    ? "Começa a gravar sozinho quando um jogo da lista abrir, e para quando ele fechar."
                    : "Iniciando…";
            Spec = "";
        }
        GameOverlay.Instance.Rec(_manual && me != null, BigValue);
        OnPropertyChanged(nameof(IsRecording));

        if (_pollCount % 10 == 1) UpdateDisk();
        if (_manual && _pollCount % 5 == 0 && FreeBytes() is long free && free < 1L << 30)
        {
            UiHost.Toast("Menos de 1 GB livre no disco: a gravação foi parada para não encher o disco.", Tone.Warn);
            _ = StopManual();
        }
    }

    /// <summary>Pasta de destino: a pasta dos clipes, ou uma subpasta com o nome do jogo.</summary>
    private string TargetFolder(string safeGame)
    {
        var dir = _s.FolderPerGame && safeGame.Length > 0 ? Path.Combine(_s.Folder, safeGame) : _s.Folder;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private long? FreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_s.Folder))!).AvailableFreeSpace; }
        catch { return null; }
    }

    // ---- gravação manual (do momento em que você aperta até apertar de novo) ----
    private readonly GlobalHotkey _recHotkey;
    public string ManualHotkeyLabel => _s.RecordKey.Label;
    private bool _manual, _manualBusy;
    private ReplayEngine? _manualEngine; // captura própria quando o replay não está ligado
    private string _manualGame = "";

    public bool IsManual => _manual;
    public string BigUnit => _manual ? "" : "S";
    public string ManualLabel => _manual ? "PARAR GRAVAÇÃO" : "GRAVAR AGORA";

    private void ManualChanged()
    {
        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(BigUnit));
        OnPropertyChanged(nameof(ManualLabel));
        Tick();
    }

    private async Task ToggleManual()
    {
        if (_manualBusy) return;
        _manualBusy = true;
        try { if (_manual) await StopManual(); else await StartManual(); }
        finally { _manualBusy = false; }
    }

    private async Task StartManual()
    {
        if (FreeBytes() is long free && free < 2L << 30)
        {
            Beeper.Fail();
            UiHost.Toast($"Pouco espaço livre ({SystemInfo.Bytes(free)}). Troque a pasta dos clipes para outro disco.", Tone.Warn);
            return;
        }
        ReplayEngine? own = null;
        try
        {
            var game = _game ?? RunningGame() ?? "";
            _manualGame = game;
            var safe = string.Concat(game.Where(c => char.IsLetterOrDigit(c) || c == ' ')).Trim().Replace(' ', '-');
            var path = Path.Combine(TargetFolder(safe), $"Hertzo_{(safe.Length > 0 ? safe : "Tela")}_gravacao_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

            var engine = _engine is { Running: true } ? _engine : null;
            if (engine == null)
            {
                own = new ReplayEngine(_s, buffering: false);
                own.Faulted += () => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { if (_manualEngine == own) ManualLost(own); });
                await Task.Run(own.Start);
                engine = own;
            }
            await engine.StartFileAsync(path);
            _manualEngine = own;
            _manual = true;
            Beeper.Start();
            GameOverlay.Instance.Toast("GRAVANDO", $"{ManualHotkeyLabel} PARA PARAR", Tone.Bad);
            ManualChanged();
        }
        catch (Exception ex)
        {
            if (own != null) _ = Task.Run(own.Stop);
            Beeper.Fail();
            UiHost.Log(ex);
            UiHost.Toast("Não deu para começar a gravação: " + ex.Message, Tone.Bad);
        }
    }

    private async Task StopManual()
    {
        if (!_manual) return;
        var engine = _manualEngine ?? _engine;
        _manual = false;
        try
        {
            if (engine == null) throw new InvalidOperationException("A captura parou antes da hora.");
            var path = engine.FilePath!;
            var (secs, snap) = await engine.StopFileAsync();
            AddClip(path, _manualGame, secs, snap, engine);
            Beeper.Done();
            GameOverlay.Instance.Toast($"GRAVAÇÃO SALVA · {TimeSpan.FromSeconds(secs):mm\\:ss}",(_manualGame.Length > 0 ? _manualGame.ToUpperInvariant() + " · " : "") + "HERTZO / CLIPES", Tone.Ok);
            UiHost.Toast($"Gravação salva: {TimeSpan.FromSeconds(secs):mm\\:ss} em {Path.GetFileName(path)}", Tone.Ok);
        }
        catch (Exception ex)
        {
            Beeper.Fail();
            UiHost.Log(ex);
            UiHost.Toast("Não deu para fechar a gravação: " + ex.Message, Tone.Bad);
        }
        finally
        {
            if (_manualEngine != null) { var m = _manualEngine; _manualEngine = null; _ = Task.Run(m.Stop); }
            ManualChanged();
        }
    }

    /// <summary>A captura caiu no meio da gravação: o arquivo já foi fechado pelo motor, só registra.</summary>
    private void ManualLost(ReplayEngine e)
    {
        _manual = false;
        _manualEngine = null;
        if (e.FilePath != null && File.Exists(e.FilePath))
        {
            AddClip(e.FilePath, _manualGame, e.FileSeconds, null, e);
            UiHost.Toast("A captura parou, mas o que já tinha sido gravado foi salvo.", Tone.Warn);
        }
        ManualChanged();
    }

    private void AddClip(string path, string game, double secs, Snapshot? snap, ReplayEngine e)
    {
        if (snap != null) { try { WriteThumb(snap, ClipIndex.ThumbFor(path)); } catch { } }
        var info = new ClipInfo { Path = path, Game = game, Seconds = secs, Width = e.Width, Height = e.Height, Fps = e.Fps, When = DateTime.Now };
        var index = ClipIndex.Load();
        index.Add(info);
        ClipIndex.Save(index);
        Clips.Insert(0, new ClipItem(info));
        Notify();
        UpdateDisk();
    }

    private void UpdateDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_s.Folder))!;
            var free = new DriveInfo(root).AvailableFreeSpace;
            DiskLabel = $"{SystemInfo.Bytes(free)} LIVRES EM {root.TrimEnd('\\')}";
            DiskTone = free < 5L * 1024 * 1024 * 1024 ? Tone.Warn : Tone.Ok;
        }
        catch { DiskLabel = ""; }
    }

    // ---- salvar ----
    private async void SaveClip()
    {
        if (_saving) return;
        var e = _engine;
        if (e is not { Running: true })
        {
            Beeper.Fail();
            UiHost.Toast(_s.Armed ? "O replay ainda não está gravando." : "Ligue o replay primeiro.", Tone.Warn);
            return;
        }
        _saving = true;
        Tick();
        try
        {
            var game = _game ?? RunningGame() ?? "Tela";
            var safe = string.Concat(game.Where(c => char.IsLetterOrDigit(c) || c == ' ')).Trim().Replace(' ', '-');
            var path = Path.Combine(TargetFolder(game == "Tela" ? "" : safe), $"Hertzo_{(safe.Length > 0 ? safe : "Clipe")}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
            var (secs, snap) = await e.SaveAsync(path, _s.Seconds);
            if (snap != null) await Task.Run(() => WriteThumb(snap, ClipIndex.ThumbFor(path)));

            var info = new ClipInfo { Path = path, Game = game == "Tela" ? "" : game, Seconds = secs, Width = e.Width, Height = e.Height, Fps = e.Fps, When = DateTime.Now };
            var index = ClipIndex.Load();
            index.Add(info);
            ClipIndex.Save(index);
            Clips.Insert(0, new ClipItem(info));
            Notify();
            Beeper.Done();
            GameOverlay.Instance.Toast($"CLIPE SALVO · {secs:0} S", (game == "Tela" ? "" : game.ToUpperInvariant() + " · ") + "HERTZO / CLIPES", Tone.Ok);
            UiHost.Toast($"Clipe salvo: {secs:0} s em {Path.GetFileName(path)}", Tone.Ok);
            UpdateDisk();
        }
        catch (Exception ex)
        {
            Beeper.Fail();
            UiHost.Log(ex);
            UiHost.Toast("Não deu para salvar o clipe: " + ex.Message, Tone.Bad);
        }
        finally
        {
            _saving = false;
            Tick();
        }
    }

    private static void WriteThumb(Snapshot s, string file)
    {
        var src = BitmapSource.Create(s.Width, s.Height, 96, 96, PixelFormats.Bgr32, null, s.Pixels, s.Stride);
        var scale = 480.0 / s.Width;
        var small = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        var enc = new JpegBitmapEncoder { QualityLevel = 82 };
        enc.Frames.Add(BitmapFrame.Create(small));
        using var f = File.Create(file);
        enc.Save(f);
    }

    // ---- lista ----
    private void LoadClips()
    {
        Clips.Clear();
        var index = ClipIndex.Load().Where(c => File.Exists(c.Path)).ToList();
        var known = new HashSet<string>(index.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(_s.Folder))
                foreach (var f in Directory.EnumerateFiles(_s.Folder, "*.mp4", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 }))
                    if (!known.Contains(f))
                        index.Add(new ClipInfo { Path = f, When = File.GetLastWriteTime(f) });
        }
        catch { }
        foreach (var c in index.OrderByDescending(c => c.When).Take(60)) Clips.Add(new ClipItem(c));
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasClips));
        OnPropertyChanged(nameof(NoClips));
    }

    private async Task Delete(ClipItem c)
    {
        var ok = await UiHost.Confirm(new DialogRequest
        {
            Title = "Apagar este clipe?",
            Message = $"{Path.GetFileName(c.Info.Path)} vai para a Lixeira. Dá para recuperar de lá enquanto ela não for esvaziada.",
            ConfirmText = "Mover para a Lixeira",
            Danger = true,
        });
        if (!ok) return;
        try
        {
            if (Guard.IsClipFile(c.Info.Path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(c.Info.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            try { File.Delete(ClipIndex.ThumbFor(c.Info.Path)); } catch { }
            ClipIndex.Save(ClipIndex.Load().Where(x => !x.Path.Equals(c.Info.Path, StringComparison.OrdinalIgnoreCase)));
            Clips.Remove(c);
            Notify();
            UiHost.Toast("Clipe movido para a Lixeira.", Tone.Info);
            UpdateDisk();
        }
        catch (Exception ex) { UiHost.Error(ex); }
    }

    private void ChooseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Onde salvar os clipes", InitialDirectory = Directory.Exists(_s.Folder) ? _s.Folder : null };
        if (dlg.ShowDialog() != true) return;
        if (Guard.IsSystemPath(dlg.FolderName))
        {
            UiHost.Toast("Escolha uma pasta sua (Vídeos, outro disco…), não uma pasta do Windows ou de programas.", Tone.Warn);
            return;
        }
        _s.Folder = dlg.FolderName;
        _s.Save();
        OnPropertyChanged(nameof(Folder));
        UpdateDisk();
        LoadClips();
    }
}
