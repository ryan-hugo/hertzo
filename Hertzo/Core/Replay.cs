using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Hertzo.Core;

// =====================================================================
// REPLAY INSTANTÂNEO
// Captura: Desktop Duplication (API do Windows, a mesma família usada por
// gravadores de tela). Nada é injetado no jogo.
// Codificação: H.264 pelo encoder de hardware da placa (AMF / NVENC /
// QuickSync) via Media Foundation, com os quadros indo direto da GPU.
// Buffer: trechos curtos de MP4 guardados na RAM; ao salvar, os últimos
// trechos são juntados sem recodificar.
// =====================================================================

/// <summary>Atalho global: modificadores (MOD_*) + tecla virtual.</summary>
public sealed class HotkeySpec
{
    public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;
    public uint Mods { get; set; }
    public uint Vk { get; set; }

    public HotkeySpec() { }
    public HotkeySpec(uint mods, uint vk) { Mods = mods; Vk = vk; }

    public bool SameAs(HotkeySpec? o) => o != null && o.Mods == Mods && o.Vk == Vk;

    public string Label
    {
        get
        {
            var parts = new List<string>();
            if ((Mods & Ctrl) != 0) parts.Add("CTRL");
            if ((Mods & Shift) != 0) parts.Add("SHIFT");
            if ((Mods & Alt) != 0) parts.Add("ALT");
            if ((Mods & Win) != 0) parts.Add("WIN");
            parts.Add(KeyName(Vk));
            return string.Join(" + ", parts);
        }
    }

    public string[] Keys => Label.Split(" + ");

    public static string KeyName(uint vk) => vk switch
    {
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "NUM " + (vk - 0x60),
        0x6A => "NUM *", 0x6B => "NUM +", 0x6D => "NUM -", 0x6E => "NUM ,", 0x6F => "NUM /",
        0x13 => "PAUSE", 0x91 => "SCROLL LOCK", 0x2C => "PRINT", 0x2D => "INSERT", 0x2E => "DELETE",
        0x24 => "HOME", 0x23 => "END", 0x21 => "PAGE UP", 0x22 => "PAGE DOWN",
        0x26 => "↑", 0x28 => "↓", 0x25 => "←", 0x27 => "→", 0x20 => "ESPAÇO",
        0xC0 => "'", 0xBD => "-", 0xBB => "=", 0xDB => "´", 0xDD => "[", 0xDC => "]", 0xBA => "Ç", 0xDE => "~", 0xBC => ",", 0xBE => ".", 0xBF => ";", 0xC1 => "/",
        _ => $"TECLA {vk}",
    };
}

public sealed class ClipSettings
{
    public const int MinSeconds = 5, MaxSecondsLimit = 600;

    public int Seconds { get; set; } = 30;
    public string Quality { get; set; } = "padrao"; // leve | padrao | alta | maxima
    public int Fps { get; set; } = 60;               // 30 | 60 | 120
    public int OutputHeight { get; set; }            // 0 = nativa; 1080; 720
    public bool OnlyInGame { get; set; } = true;
    public bool Armed { get; set; }
    public string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Hertzo");
    public bool FolderPerGame { get; set; }
    public HotkeySpec SaveKey { get; set; } = new(HotkeySpec.Ctrl | HotkeySpec.Shift, 0x79);   // F10
    public HotkeySpec RecordKey { get; set; } = new(HotkeySpec.Ctrl | HotkeySpec.Shift, 0x7A); // F11
    public bool Sounds { get; set; } = true;
    public string Overlay { get; set; } = "tr";     // off | tl | tr | bl | br
    public bool OverlayRec { get; set; } = true;

    // áudio
    public bool GameAudio { get; set; } = true;
    public int GameVolume { get; set; } = 100;      // %
    public bool Mic { get; set; }
    public int MicVolume { get; set; } = 100;       // %
    public string MicDevice { get; set; } = "";     // "" = microfone padrão do Windows
    public bool MicTrack { get; set; }              // faixa extra só com o microfone (para edição)
    public int AudioTracks => GameAudio || Mic ? (Mic && MicTrack ? 2 : 1) : 0;

    /// <summary>Bits por pixel por quadro. 0,15 dá ~19 Mbps em 1080p60.</summary>
    public double Bpp => Quality switch { "leve" => 0.09, "alta" => 0.22, "maxima" => 0.32, _ => 0.15 };

    /// <summary>Bitrate para uma resolução — cai um pouco por quadro em 120 FPS (quadros mais parecidos).</summary>
    public int BitrateFor(int w, int h) => (int)Math.Min(80_000_000, w * (double)h * Fps * Bpp * (Fps >= 120 ? 0.7 : 1));

    public (int W, int H) EncodeSize(int capW, int capH)
    {
        if (OutputHeight <= 0 || OutputHeight >= capH) return (capW & ~1, capH & ~1);
        var w = (int)Math.Round(capW * (double)OutputHeight / capH);
        return (w & ~1, OutputHeight & ~1);
    }

    private static string FilePath => Path.Combine(AppPaths.UserData, "clipes.json");

    public static ClipSettings Load()
    {
        try
        {
            var s = File.Exists(FilePath) ? JsonSerializer.Deserialize<ClipSettings>(File.ReadAllText(FilePath)) ?? new() : new();
            s.Seconds = Math.Clamp(s.Seconds, MinSeconds, MaxSecondsLimit);
            if (s.Fps is not (30 or 60 or 120)) s.Fps = 60;
            s.SaveKey ??= new(HotkeySpec.Ctrl | HotkeySpec.Shift, 0x79);
            s.RecordKey ??= new(HotkeySpec.Ctrl | HotkeySpec.Shift, 0x7A);
            // O arquivo de configurações pode ser editado sem admin: nunca gravar vídeos em pasta do sistema.
            if (string.IsNullOrWhiteSpace(s.Folder) || !Path.IsPathFullyQualified(s.Folder) || Guard.IsSystemPath(s.Folder))
                s.Folder = new ClipSettings().Folder;
            if (s.Overlay is not ("off" or "tl" or "tr" or "bl" or "br")) s.Overlay = "tr";
            if (s.Quality is not ("leve" or "padrao" or "alta" or "maxima")) s.Quality = "padrao";
            if (s.OutputHeight is not (0 or 720 or 1080)) s.OutputHeight = 0;
            s.GameVolume = Math.Clamp(s.GameVolume, 0, 200);
            s.MicVolume = Math.Clamp(s.MicVolume, 0, 200);
            return s;
        }
        catch { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); } catch { }
    }
}

public sealed class ClipInfo
{
    public string Path { get; set; } = "";
    public string Game { get; set; } = "";
    public double Seconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }
    public DateTime When { get; set; }
}

public static class ClipIndex
{
    private static string FilePath => Path.Combine(AppPaths.UserData, "clipes-indice.json");
    public static string ThumbDir { get { var d = Path.Combine(AppPaths.UserData, "miniaturas"); Directory.CreateDirectory(d); return d; } }
    public static string ThumbFor(string clip) => Path.Combine(ThumbDir, Path.GetFileNameWithoutExtension(clip) + ".jpg");

    public static List<ClipInfo> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<ClipInfo>>(File.ReadAllText(FilePath)) ?? [] : []; }
        catch { return []; }
    }

    public static void Save(IEnumerable<ClipInfo> list)
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(list.Where(c => File.Exists(c.Path)).ToList())); } catch { }
    }
}

/// <summary>Quadro reduzido para miniatura (BGRA).</summary>
public sealed record Snapshot(byte[] Pixels, int Width, int Height, int Stride);

public sealed class ReplayEngine : IDisposable
{
    private const int PoolSize = 8; // texturas iniciais do alocador (cresce até 48 se o encoder atrasar)

    /// <summary>Tamanho do vídeo (pode ser menor que a tela, ver ClipSettings.OutputHeight).</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Tamanho da tela capturada no início.</summary>
    public int CaptureWidth { get; private set; }
    public int CaptureHeight { get; private set; }
    public int Fps { get; }
    public int Bitrate { get; private set; }
    public int MaxSeconds { get; }
    public string Encoder { get; private set; } = "H.264";
    public string? Error { get; private set; }
    public bool Running => _thread is { IsAlive: true } && !_stop;

    // ---- diagnóstico de ritmo (para provar que a captura não engasga) ----
    /// <summary>Quadros em que o laço de captura chegou atrasado mais de meio quadro.</summary>
    public long LateFrames => Interlocked.Read(ref _late);
    /// <summary>Quadros em que não havia imagem nova da tela (tela parada ou captura atrasada).</summary>
    public long RepeatedFrames => Interlocked.Read(ref _repeats);
    public long FramesWritten => Interlocked.Read(ref _written);
    public double MaxLateMs { get; private set; }
    private long _late, _repeats, _written;

    // ---- áudio: o mixer roda na própria thread e escreve nos mesmos codificadores ----
    public AudioMixer? Mixer { get; private set; }
    private volatile Writer? _liveW, _fileW;
    private long _fileT0;

    private void StartAudio(long startQpc)
    {
        if (_s.AudioTracks == 0) return;
        try
        {
            Mixer = new AudioMixer(_s.GameAudio, _s.GameVolume / 100.0, _s.Mic, _s.MicVolume / 100.0, _s.MicDevice, _s.MicTrack, startQpc);
            Mixer.Output += (mix, micOnly, start, frames) =>
            {
                long t = start * 10_000_000L / AudioMixer.Rate;
                var live = _liveW;
                if (live != null)
                {
                    live.WriteAudio(0, mix, frames, t);
                    if (micOnly != null) live.WriteAudio(1, micOnly, frames, t);
                }
                var file = _fileW;
                if (file != null && t >= Interlocked.Read(ref _fileT0))
                {
                    var ft = t - Interlocked.Read(ref _fileT0);
                    file.WriteAudio(0, mix, frames, ft);
                    if (micOnly != null) file.WriteAudio(1, micOnly, frames, ft);
                }
            };
        }
        catch (Exception ex) { UiHost.Log(ex); Mixer = null; }
    }
    private volatile bool _fresh;
    /// <summary>API de captura em uso.</summary>
    public string CaptureApi { get; private set; } = "Desktop Duplication";

    // ---- buffer do replay: um encoder contínuo gravando MP4 fragmentado (1 fragmento por segundo) ----
    private readonly List<Fragment> _ring = [];
    private byte[] _init = [];
    private long _fragCount, _framesCommitted;
    /// <param name="Origin">Posição do fragmento no fluxo original (o tfhd aponta para posições absolutas).</param>
    private sealed record Fragment(byte[] Data, int Frames, long Origin);

    /// <summary>Segundos guardados no buffer agora (inclui o segundo em andamento).</summary>
    public double BufferedSeconds
    {
        get
        {
            lock (_ring)
            {
                var pending = Math.Max(0, Interlocked.Read(ref _written) - _framesCommitted);
                return (_ring.Sum(f => f.Frames) + pending) / (double)Fps;
            }
        }
    }
    public long BufferedBytes { get { lock (_ring) return _ring.Sum(f => (long)f.Data.Length) + _init.Length; } }

    public event Action? Faulted;
    /// <summary>Diagnóstico: quadros de cada fragmento e o MP4 fragmentado bruto do buffer.</summary>
    public (int[] Frames, byte[] Raw) DebugBuffer()
    {
        lock (_ring)
        {
            var ms = new MemoryStream(); ms.Write(_init); foreach (var f in _ring) ms.Write(f.Data);
            return (_ring.Select(f => f.Frames).ToArray(), ms.ToArray());
        }
    }
    public static IReadOnlyList<string> EncoderProbe => Writer.Debug;

    private readonly ClipSettings _s;
    private Thread? _thread;
    private volatile bool _stop;
    private TaskCompletionSource<Snapshot?>? _snapRequest;
    private readonly object _snapLock = new();
    private static bool _mfStarted;

    /// <summary>false = só captura para a gravação manual, sem manter o buffer do replay.</summary>
    public bool Buffering { get; }

    public ReplayEngine(ClipSettings s, bool buffering = true)
    {
        _s = s;
        Fps = s.Fps;
        MaxSeconds = s.Seconds;
        Buffering = buffering;
    }

    // ---- gravação manual (direto no disco, em paralelo ao replay) ----
    private readonly object _fileLock = new();
    private string? _fileStartPath;
    private TaskCompletionSource? _fileStarted;
    private TaskCompletionSource<(double Seconds, Snapshot? Snap)>? _fileStop;
    private volatile bool _fileActive;
    private volatile float _fileSeconds;
    public bool FileRecording => _fileActive;
    public double FileSeconds => _fileSeconds;
    public string? FilePath { get; private set; }

    /// <summary>Começa a gravar a partir de agora num MP4, sem limite de tempo.</summary>
    public async Task StartFileAsync(string path)
    {
        if (!Running) throw new InvalidOperationException("A captura não está ligada.");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_fileLock) { _fileStartPath = path; _fileStarted = tcs; }
        if (await Task.WhenAny(tcs.Task, Task.Delay(8000)) != tcs.Task) throw new InvalidOperationException("A gravação não começou a tempo.");
        await tcs.Task;
    }

    /// <summary>Para a gravação manual e fecha o arquivo.</summary>
    public async Task<(double Seconds, Snapshot? Snap)> StopFileAsync()
    {
        var tcs = new TaskCompletionSource<(double, Snapshot?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_fileLock) _fileStop = tcs;
        if (await Task.WhenAny(tcs.Task, Task.Delay(15000)) != tcs.Task) throw new InvalidOperationException("A gravação não terminou a tempo.");
        return await tcs.Task;
    }

    private static readonly List<ReplayEngine> Live = [];

    /// <summary>Replay ligado agora (para o letreiro).</summary>
    public static ReplayEngine? Active { get { lock (Live) return Live.FirstOrDefault(e => e.Buffering && e.Running); } }

    /// <summary>Ao fechar o Hertzo: para tudo e fecha gravações manuais em andamento.</summary>
    public static void StopAll()
    {
        ReplayEngine[] all;
        lock (Live) all = Live.ToArray();
        foreach (var e in all) e.Stop();
    }

    public void Start()
    {
        lock (Live) Live.Add(this);
        if (!_mfStarted) { MediaFactory.MFStartup(false).CheckError(); _mfStarted = true; }
        _stop = false;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Hertzo Replay", Priority = ThreadPriority.Highest };
        _thread.Start();
        ready.Wait(8000);
        if (Error != null) throw new InvalidOperationException(Error);
    }

    public void Stop()
    {
        lock (Live) Live.Remove(this);
        _stop = true;
        _thread?.Join(15000); // dá tempo de fechar uma gravação manual em andamento
        lock (_ring) _ring.Clear();
    }

    public void Dispose() => Stop();

    // ---- recebe os fragmentos do encoder contínuo ----
    private void OnInit(byte[] init) { lock (_ring) _init = init; }

    private void OnFragment(byte[] data, int frames, long origin)
    {
        lock (_ring)
        {
            _ring.Add(new Fragment(data, frames, origin));
            _framesCommitted += frames;
            _fragCount++;
            // guarda um segundo a mais para sempre cobrir o tempo pedido
            while (_ring.Count > 1 && (_ring.Sum(f => f.Frames) - _ring[0].Frames) / (double)Fps >= MaxSeconds + 2) _ring.RemoveAt(0);
        }
    }

    // ------------------------------------------------------------------
    // Laço de captura
    // ------------------------------------------------------------------
    private void Run(ManualResetEventSlim ready)
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? ctx = null;
        IDXGIOutputDuplication? dup = null;
        IMFDXGIDeviceManager? manager = null;
        ID3D11Texture2D? latest = null;
        IMFVideoSampleAllocatorEx? alloc = null;
        Writer? live = null;
        Writer? file = null;
        long fileStart = 0;
        TaskCompletionSource<(double, Snapshot?)>? pendingStop = null;
        Scaler? scaler = null;
        WgcSource? wgc = null;

        // prioridade de multimídia (MMCSS) e temporizador de 1 ms, como gravadores profissionais:
        // o jogo pesado não pode atrasar a captura
        uint mmTask = 0;
        var mm = Mm.AvSetMmThreadCharacteristicsW("Capture", ref mmTask);
        if (mm != IntPtr.Zero) Mm.AvSetMmThreadPriority(mm, 1 /* AVRT_PRIORITY_HIGH */);
        Mm.timeBeginPeriod(1);

        try
        {
            (device, ctx, dup) = OpenDuplication();
            (Width, Height) = _s.EncodeSize(CaptureWidth, CaptureHeight);
            using (var mt = device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);

            manager = MediaFactory.MFCreateDXGIDeviceManager();
            manager.ResetDevice(device).CheckError();

            var desc = new Texture2DDescription
            {
                Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            };
            latest = device.CreateTexture2D(desc);
            // texturas de entrada do encoder: o Media Foundation só recicla uma textura depois que o encoder a libera
            // (um conjunto fixo era sobrescrito com o encoder atrasado = quadros repetidos no clipe)
            alloc = new IMFVideoSampleAllocatorEx(MediaFactory.MFCreateVideoSampleAllocatorEx(typeof(IMFVideoSampleAllocatorEx).GUID));
            alloc.SetDirectXManager(manager);
            using (var aa = MediaFactory.MFCreateAttributes(2))
            using (var vt = MediaFactory.MFCreateMediaType())
            {
                aa.Set(TransformAttributeKeys.D3D11Bindflags, (uint)(BindFlags.ShaderResource | BindFlags.RenderTarget));
                vt.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                vt.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
                vt.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)(uint)Width << 32) | (uint)Height);
                vt.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)(uint)Fps << 32) | 1u);
                vt.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
                vt.Set(MediaTypeAttributeKeys.PixelAspectRatio, (1UL << 32) | 1u);
                alloc.InitializeSampleAllocatorEx(PoolSize, 48, aa, vt);
            }

            Bitrate = _s.BitrateFor(Width, Height);
            if (Buffering)
            {
                live = Writer.Create(manager, Width, Height, Fps, Bitrate, stream: new FragmentStream(OnInit, OnFragment), audioTracks: _s.AudioTracks);
                _liveW = live;
                Encoder = live.EncoderName ?? Encoder;
            }
            // fonte de imagem: Windows.Graphics.Capture (todos os quadros do jogo); Desktop Duplication é a reserva
            var frameLock = new object();
            var tex0 = latest;
            if (WgcSource.Supported)
            {
                try
                {
                    wgc = new WgcSource(device, tex =>
                    {
                        lock (frameLock)
                        {
                            var td = tex.Description;
                            if (td.Width == (uint)Width && td.Height == (uint)Height) ctx!.CopyResource(tex0, tex);
                            else
                            {
                                if (scaler == null || scaler.InWidth != (int)td.Width || scaler.InHeight != (int)td.Height)
                                {
                                    scaler?.Dispose();
                                    scaler = new Scaler(device!, ctx!, (int)td.Width, (int)td.Height, tex0, Width, Height, Fps);
                                }
                                scaler.Scale(tex);
                            }
                            _fresh = true;
                        }
                    });
                    CaptureApi = "Windows.Graphics.Capture";
                    dup?.Dispose();
                    dup = null;
                }
                catch (Exception ex) { UiHost.Log(ex); wgc = null; }
            }
            ready.Set();

            long interval = 10_000_000L / Fps;          // 100 ns
            double frameMs = 1000.0 / Fps;
            var startQpc = AudioMixer.NowQpc100ns();
            var clock = Stopwatch.StartNew();
            StartAudio(startQpc);
            long tick = 0;

            while (!_stop)
            {
                long due = (long)(clock.Elapsed.TotalSeconds * Fps);
                if (tick > due && wgc != null)
                {
                    // os quadros chegam sozinhos pela WGC: só espera a hora do próximo quadro do vídeo
                    var ms = (tick / (double)Fps - clock.Elapsed.TotalSeconds) * 1000;
                    if (ms > 1.5) Thread.Sleep((int)(ms - 1)); else Thread.Yield();
                    continue;
                }
                if (tick > due)
                {
                    // espera o próximo quadro da tela até a hora do próximo tique
                    var waitMs = (uint)Math.Max(0, (tick / (double)Fps - clock.Elapsed.TotalSeconds) * 1000);
                    var r = dup!.AcquireNextFrame(waitMs, out var info, out var res);
                    if (r.Success)
                    {
                        if (info.LastPresentTime != 0 || info.AccumulatedFrames > 0)
                        {
                            using (var tex = res!.QueryInterface<ID3D11Texture2D>())
                            {
                                var td = tex.Description;
                                if (td.Width == (uint)Width && td.Height == (uint)Height && td.Format == Format.B8G8R8A8_UNorm)
                                    ctx!.CopyResource(latest, tex);
                                else
                                {
                                    // resolução diferente da do vídeo (jogo esticado, saída 720p…): redimensiona na GPU
                                    if (scaler == null || scaler.InWidth != (int)td.Width || scaler.InHeight != (int)td.Height)
                                    {
                                        scaler?.Dispose();
                                        scaler = new Scaler(device!, ctx!, (int)td.Width, (int)td.Height, latest, Width, Height, Fps);
                                    }
                                    scaler.Scale(tex);
                                }
                            }
                            _fresh = true;
                        }
                        res?.Dispose();
                        dup.ReleaseFrame();
                    }
                    else if (r.Code == Vortice.DXGI.ResultCode.AccessLost.Code)
                    {
                        // troca de resolução, tela de UAC, modo de tela cheia: reabre a duplicação
                        dup.Dispose();
                        dup = null;
                        for (int attempt = 0; attempt < 20 && dup == null && !_stop; attempt++)
                        {
                            dup = Reopen(device!);
                            if (dup == null) Thread.Sleep(150);
                        }
                        if (dup == null) throw new InvalidOperationException("A captura da tela foi perdida. Ligue o replay de novo.");
                    }
                    else if (r.Code != Vortice.DXGI.ResultCode.WaitTimeout.Code) r.CheckError();
                    continue;
                }

                var lateMs = clock.Elapsed.TotalMilliseconds - tick * frameMs;
                if (lateMs > frameMs * 0.5) Interlocked.Increment(ref _late);
                if (lateMs > MaxLateMs) MaxLateMs = lateMs;
                // pula tiques muito atrasados em vez de acumular (o tempo do vídeo continua real)
                if (due - tick > 2) tick = due;

                // pedidos da gravação manual
                string? startPath; TaskCompletionSource? started; TaskCompletionSource<(double, Snapshot?)>? stopReq;
                lock (_fileLock)
                {
                    startPath = _fileStartPath; _fileStartPath = null;
                    started = _fileStarted; _fileStarted = null;
                    stopReq = _fileStop; _fileStop = null;
                }
                if (startPath != null)
                {
                    try
                    {
                        if (file != null) throw new InvalidOperationException("Já existe uma gravação em andamento.");
                        file = Writer.Create(manager!, Width, Height, Fps, Bitrate, path: startPath, audioTracks: _s.AudioTracks);
                        _fileT0 = tick * interval;
                        _fileW = file;
                        if (!Buffering) Encoder = file.EncoderName ?? Encoder;
                        fileStart = tick;
                        FilePath = startPath;
                        _fileSeconds = 0;
                        _fileActive = true;
                        started?.TrySetResult();
                    }
                    catch (Exception ex) { started?.TrySetException(ex); }
                }
                if (stopReq != null)
                {
                    if (file == null) stopReq.TrySetException(new InvalidOperationException("Nenhuma gravação manual em andamento."));
                    else
                    {
                        var f = file; file = null; _fileW = null; _fileActive = false;
                        var secs = (tick - fileStart) / (double)Fps;
                        Snapshot? snap; lock (frameLock) snap = Grab(device!, ctx!, latest);
                        Task.Run(() =>
                        {
                            try { f.Finish(); stopReq.TrySetResult((secs, snap)); }
                            catch (Exception ex) { stopReq.TrySetException(ex); }
                        });
                    }
                }

                // miniatura pedida ao salvar um clipe
                TaskCompletionSource<Snapshot?>? snapReq;
                lock (_snapLock) { snapReq = _snapRequest; _snapRequest = null; }
                if (snapReq != null) { Snapshot? sn; lock (frameLock) sn = Grab(device!, ctx!, latest); snapReq.TrySetResult(sn); }

                if (!_fresh) Interlocked.Increment(ref _repeats);
                _fresh = false;

                lock (frameLock)
                {
                    if (live != null) WriteFrame(alloc, ctx!, latest, live, tick * interval, interval);
                    if (file != null)
                    {
                        WriteFrame(alloc, ctx!, latest, file, (tick - fileStart) * interval, interval);
                        _fileSeconds = (tick + 1 - fileStart) / (float)Fps;
                    }
                }
                tick++;
                Interlocked.Increment(ref _written);
            }
        }
        catch (Exception ex)
        {
            Error = Friendly(ex);
            UiHost.Log(ex);
            ready.Set();
            if (!_stop) { _stop = true; Faulted?.Invoke(); }
        }
        finally
        {
            // uma gravação manual em andamento é fechada e mantida, nunca descartada
            if (file != null)
            {
                _fileActive = false;
                try { file.Finish(); } catch (Exception ex) { UiHost.Log(ex); }
                lock (_fileLock) pendingStop = _fileStop;
                pendingStop?.TrySetResult((_fileSeconds, null));
            }
            wgc?.Dispose();
            _liveW = null; _fileW = null;
            Mixer?.Dispose();
            Mixer = null;
            try { live?.Abandon(); } catch { }
            scaler?.Dispose();
            try { alloc?.UninitializeSampleAllocator(); } catch { }
            alloc?.Dispose();
            latest?.Dispose();
            dup?.Dispose();
            manager?.Dispose();
            ctx?.Dispose();
            device?.Dispose();
            Mm.timeEndPeriod(1);
            if (mm != IntPtr.Zero) Mm.AvRevertMmThreadCharacteristics(mm);
        }
    }

    /// <summary>Copia o quadro atual para uma textura livre do alocador e entrega ao encoder.</summary>
    private void WriteFrame(IMFVideoSampleAllocatorEx alloc, ID3D11DeviceContext ctx, ID3D11Texture2D latest, Writer w, long time, long duration)
    {
        IMFSample sample;
        try { sample = alloc.AllocateSample(); }
        catch { Interlocked.Increment(ref _dropped); return; } // encoder muito atrasado: descarta o quadro em vez de corromper outro
        using (sample)
        {
            using (var buf = sample.GetBufferByIndex(0))
            {
                using (var dx = buf.QueryInterface<IMFDXGIBuffer>())
                using (var tex = new ID3D11Texture2D(dx.GetResource(typeof(ID3D11Texture2D).GUID)))
                    ctx.CopyResource(tex, latest);
                using var b2 = buf.QueryInterface<IMF2DBuffer>();
                buf.CurrentLength = b2.ContiguousLength;
            }
            w.WriteSample(sample, time, duration);
        }
    }

    /// <summary>Quadros descartados porque o encoder não acompanhou.</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    private long _dropped;

    private static string Friendly(Exception ex) => ex switch
    {
        SharpGenException sg when sg.ResultCode.Code == unchecked((int)0x887A0004) =>
            "A duplicação de tela não é suportada neste adaptador (comum em notebooks com duas GPUs).",
        SharpGenException sg when sg.ResultCode.Code == unchecked((int)0xC00D36B4) || sg.ResultCode.Code == unchecked((int)0xC00D5212) =>
            "Não foi encontrado um codificador H.264 compatível. Atualize o driver da placa de vídeo.",
        InvalidOperationException => ex.Message,
        _ => "O replay parou: " + ex.Message,
    };

    private (ID3D11Device, ID3D11DeviceContext, IDXGIOutputDuplication) OpenDuplication()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var od = output.Description;
                        if (!od.AttachedToDesktop || od.DesktopCoordinates.Left != 0 || od.DesktopCoordinates.Top != 0) continue; // monitor principal
                        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown,
                            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device? dev, out ID3D11DeviceContext? c).CheckError();
                        using var o1 = output.QueryInterface<IDXGIOutput1>();
                        var dup = o1.DuplicateOutput(dev);
                        var dd = dup.Description;
                        CaptureWidth = (int)dd.ModeDescription.Width;
                        CaptureHeight = (int)dd.ModeDescription.Height;
                        return (dev!, c!, dup);
                    }
                }
            }
        }
        throw new InvalidOperationException("Nenhum monitor principal encontrado para capturar.");
    }

    private IDXGIOutputDuplication? Reopen(ID3D11Device device)
    {
        try
        {
            using var dxgiDev = device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDev.GetAdapter();
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                using (output)
                {
                    var od = output.Description;
                    if (!od.AttachedToDesktop || od.DesktopCoordinates.Left != 0 || od.DesktopCoordinates.Top != 0) continue;
                    using var o1 = output.QueryInterface<IDXGIOutput1>();
                    // a resolução pode ter mudado (jogo em tela cheia com resolução própria):
                    // o vídeo continua no tamanho original e os quadros passam pelo Scaler
                    return o1.DuplicateOutput(device);
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Diagnóstico: escala uma imagem 1280×960 vermelha para 1920×1080 na GPU e lê os pixels de volta.</summary>
    public static string ScalerSelfTest()
    {
        D3D11.D3D11CreateDevice(null!, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_0], out ID3D11Device? dev, out ID3D11DeviceContext? ctx).CheckError();
        using var d = dev!; using var c = ctx!;
        Texture2DDescription Desc(int w, int h) => new()
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        };
        using var src = d.CreateTexture2D(Desc(1280, 960));
        using (var rtv = d.CreateRenderTargetView(src)) c.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(1, 0, 0, 1));
        using var dst = d.CreateTexture2D(Desc(1920, 1080));
        using var s = new Scaler(d, c, 1280, 960, dst, 1920, 1080, 60);
        s.Scale(src);
        var sd = Desc(1920, 1080); sd.Usage = ResourceUsage.Staging; sd.BindFlags = BindFlags.None; sd.CPUAccessFlags = CpuAccessFlags.Read;
        using var st = d.CreateTexture2D(sd);
        c.CopyResource(st, dst);
        var map = c.Map(st, 0, MapMode.Read);
        string Px(int x, int y) { var o = y * (int)map.RowPitch + x * 4; return $"B{Marshal.ReadByte(map.DataPointer, o)} G{Marshal.ReadByte(map.DataPointer, o + 1)} R{Marshal.ReadByte(map.DataPointer, o + 2)}"; }
        var r = $"gpu={s.UsesGpu} centro=[{Px(960, 540)}] canto=[{Px(1910, 1070)}]";
        c.Unmap(st, 0);
        return r;
    }

    /// <summary>
    /// Redimensiona na GPU (processador de vídeo do D3D11) quando a tela muda de resolução no meio
    /// da gravação — ex.: VALORANT/CS2 em 1280×960 esticado e área de trabalho em 1920×1080.
    /// Assim o replay não cai no alt-tab e o vídeo mantém um tamanho fixo.
    /// </summary>
    private sealed class Scaler : IDisposable
    {
        public int InWidth { get; }
        public int InHeight { get; }
        private readonly ID3D11DeviceContext _ctx;
        private readonly ID3D11VideoContext? _vctx;
        private readonly ID3D11Texture2D _raw;
        private readonly ID3D11VideoProcessorEnumerator? _en;
        private readonly ID3D11VideoProcessor? _proc;
        private readonly ID3D11VideoProcessorInputView? _in;
        private readonly ID3D11VideoProcessorOutputView? _out;
        private readonly ID3D11Texture2D _target;
        private readonly bool _gpu;
        public bool UsesGpu => _gpu;

        public Scaler(ID3D11Device dev, ID3D11DeviceContext ctx, int inW, int inH, ID3D11Texture2D target, int outW, int outH, int fps)
        {
            InWidth = inW; InHeight = inH; _ctx = ctx; _target = target;
            _raw = dev.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)inW, Height = (uint)inH, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            });
            try
            {
                using var vdev = dev.QueryInterface<ID3D11VideoDevice>();
                _vctx = ctx.QueryInterface<ID3D11VideoContext>();
                _en = vdev.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
                {
                    InputFrameFormat = VideoFrameFormat.Progressive,
                    InputWidth = (uint)inW, InputHeight = (uint)inH,
                    OutputWidth = (uint)outW, OutputHeight = (uint)outH,
                    InputFrameRate = new Rational((uint)fps, 1), OutputFrameRate = new Rational((uint)fps, 1),
                    Usage = VideoUsage.PlaybackNormal,
                });
                _proc = vdev.CreateVideoProcessor(_en, 0);
                _in = vdev.CreateVideoProcessorInputView(_raw, _en, new VideoProcessorInputViewDescription
                {
                    FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                });
                _out = vdev.CreateVideoProcessorOutputView(target, _en, new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                });
                _vctx.VideoProcessorSetStreamAutoProcessingMode(_proc, 0, false);
                _gpu = true;
            }
            catch (Exception ex) { UiHost.Log(ex); _gpu = false; } // sem processador de vídeo: recorta em vez de redimensionar
        }

        public void Scale(ID3D11Texture2D src)
        {
            if (_gpu)
            {
                _ctx.CopyResource(_raw, src);
                _vctx!.VideoProcessorBlt(_proc!, _out!, 0, [new VideoProcessorStream { Enable = true, InputSurface = _in! }]).CheckError();
            }
            else
            {
                var d = _target.Description;
                var box = new Vortice.Mathematics.Box(0, 0, 0, (int)Math.Min((uint)InWidth, d.Width), (int)Math.Min((uint)InHeight, d.Height), 1);
                _ctx.CopySubresourceRegion(_target, 0, 0, 0, 0, src, 0, box);
            }
        }

        public void Dispose()
        {
            _out?.Dispose(); _in?.Dispose(); _proc?.Dispose(); _en?.Dispose(); _vctx?.Dispose(); _raw.Dispose();
        }
    }

    /// <summary>Cópia do último quadro para a CPU (miniatura do clipe).</summary>
    private Snapshot? Grab(ID3D11Device device, ID3D11DeviceContext ctx, ID3D11Texture2D src)
    {
        try
        {
            var d = src.Description;
            d.Usage = ResourceUsage.Staging; d.BindFlags = BindFlags.None; d.CPUAccessFlags = CpuAccessFlags.Read; d.MiscFlags = ResourceOptionFlags.None;
            using var staging = device.CreateTexture2D(d);
            ctx.CopyResource(staging, src);
            var map = ctx.Map(staging, 0, MapMode.Read);
            try
            {
                var stride = (int)map.RowPitch;
                var bytes = new byte[stride * Height];
                Marshal.Copy(map.DataPointer, bytes, 0, bytes.Length);
                return new Snapshot(bytes, Width, Height, stride);
            }
            finally { ctx.Unmap(staging, 0); }
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------
    // Salvar
    // ------------------------------------------------------------------
    /// <summary>Junta os últimos <paramref name="seconds"/> segundos do buffer num MP4 (sem recodificar).</summary>
    public async Task<(double Seconds, Snapshot? Snap)> SaveAsync(string path, int seconds)
    {
        if (!Running || !Buffering) throw new InvalidOperationException("O replay não está ligado.");

        var snapTask = new TaskCompletionSource<Snapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_snapLock) _snapRequest = snapTask;

        // espera o encoder entregar tudo até o momento do atalho (normalmente menos de 1 s)
        long target = Interlocked.Read(ref _written);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 6000) { lock (_ring) if (_framesCommitted >= target) break; await Task.Delay(25); }
        var snap = await Task.WhenAny(snapTask.Task, Task.Delay(1000)) == snapTask.Task ? snapTask.Task.Result : null;

        byte[] init; List<Fragment> take = [];
        lock (_ring)
        {
            init = _init;
            int frames = 0;
            // +1 s: o clipe começa no quadro-chave anterior
            for (int i = _ring.Count - 1; i >= 0 && frames < (seconds + 1) * Fps; i--) { take.Insert(0, _ring[i]); frames += _ring[i].Frames; }
        }
        if (init.Length == 0 || take.Count == 0) throw new InvalidOperationException("Ainda não há nada no buffer. Espere alguns segundos.");
        var total = await Task.Run(() => Mux(init, take, path));
        return (total, snap);
    }

    /// <summary>Corrige o base_data_offset (posição absoluta) do tfhd quando o fragmento muda de lugar no clipe.</summary>
    private static byte[] Relocate(byte[] frag, long delta)
    {
        if (delta == 0) return frag;
        var d = (byte[])frag.Clone();
        for (int i = 4; i + 20 <= d.Length; i++)
        {
            if (d[i] != (byte)'t' || d[i + 1] != (byte)'f' || d[i + 2] != (byte)'h' || d[i + 3] != (byte)'d') continue;
            int flags = d[i + 5] << 16 | d[i + 6] << 8 | d[i + 7];
            if ((flags & 1) == 0) continue;        // sem base_data_offset: posições relativas ao moof
            int p = i + 12;
            long v = 0;
            for (int k = 0; k < 8; k++) v = v << 8 | d[p + k];
            v -= delta;
            for (int k = 7; k >= 0; k--) { d[p + k] = (byte)v; v >>= 8; }
        }
        return d;
    }

    private static IMFSample CopySample(IMFSample src, long time)
    {
        using var inBuf = src.ConvertToContiguousBuffer();
        inBuf.Lock(out var p, out _, out var len);
        var outBuf = MediaFactory.MFCreateMemoryBuffer(len);
        try
        {
            outBuf.Lock(out var q, out _, out _);
            unsafe { Buffer.MemoryCopy((void*)p, (void*)q, len, len); }
            outBuf.Unlock();
            outBuf.CurrentLength = len;
        }
        finally { inBuf.Unlock(); }
        var s = MediaFactory.MFCreateSample();
        s.AddBuffer(outBuf);
        outBuf.Dispose();
        s.SampleTime = time;
        s.SampleDuration = src.SampleDuration;
        uint key = 0;
        try { key = src.GetUInt32(new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a")); } catch { }
        if (key != 0) s.Set(new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a"), 1u);
        return s;
    }

    /// <summary>SPS/PPS do H.264 (formato Annex-B) lidos da caixa avcC do início do MP4.</summary>
    private static byte[]? SpsPpsFromAvcC(byte[] init)
    {
        for (int i = 4; i + 8 < init.Length; i++)
        {
            if (init[i] != (byte)'a' || init[i + 1] != (byte)'v' || init[i + 2] != (byte)'c' || init[i + 3] != (byte)'C') continue;
            int p = i + 4 + 5;                       // versão, perfil, compat., nível, tamanho do NAL
            var outp = new List<byte>();
            int nSps = init[p++] & 0x1F;
            for (int k = 0; k < nSps; k++) { int len = init[p] << 8 | init[p + 1]; p += 2; outp.AddRange([0, 0, 0, 1]); outp.AddRange(init.AsSpan(p, len).ToArray()); p += len; }
            int nPps = init[p++];
            for (int k = 0; k < nPps; k++) { int len = init[p] << 8 | init[p + 1]; p += 2; outp.AddRange([0, 0, 0, 1]); outp.AddRange(init.AsSpan(p, len).ToArray()); p += len; }
            return outp.ToArray();
        }
        return null;
    }

    /// <summary>Diagnóstico: converte um MP4 fragmentado bruto em MP4 normal.</summary>
    public static double MuxRaw(byte[] raw, string path) => Mux(raw, [], path);

    private static double Mux(byte[] init, List<Fragment> frags, string path)
    {
        var tmp = path + ".part";
        using var ms = new MemoryStream(init.Length + frags.Sum(f => f.Data.Length));
        ms.Write(init);
        foreach (var f in frags) ms.Write(Relocate(f.Data, f.Origin - ms.Position));
        ms.Position = 0;

        // 1ª passada: instante do primeiro quadro-chave (o clipe começa nele, vídeo e áudio)
        long first = -1;
        using (var ms0 = new MemoryStream(ms.ToArray(), false))
        using (var bs0 = new MFByteStream(ms0, false))
        using (var r0 = MediaFactory.MFCreateSourceReaderFromByteStream(bs0, null))
        {
            r0.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            r0.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            while (first < 0)
            {
                using var s0 = r0.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var f0, out var t0);
                if ((f0 & SourceReaderFlag.EndOfStream) != 0 || s0 == null) break;
                uint k0 = 0;
                try { k0 = s0.GetUInt32(new Guid("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a")); } catch { }
                if (k0 != 0) first = t0;
            }
        }
        if (first < 0) throw new InvalidOperationException("O buffer não tem um quadro-chave completo.");
        ms.Position = 0;

        IMFSinkWriter? writer = null;
        long last = 0;
        try
        {
            using var bs = new MFByteStream(ms, false);
            using var reader = MediaFactory.MFCreateSourceReaderFromByteStream(bs, null);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, true);
            using var native = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0);
            // tipo "limpo" só com o essencial: o tipo do leitor de MP4 fragmentado traz atributos que o gravador de MP4 recusa
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            foreach (var k in new[] { MediaTypeAttributeKeys.FrameSize, MediaTypeAttributeKeys.FrameRate, MediaTypeAttributeKeys.PixelAspectRatio })
                try { type.Set(k, native.GetUInt64(k)); } catch { }
            foreach (var k in new[] { MediaTypeAttributeKeys.InterlaceMode, MediaTypeAttributeKeys.AvgBitrate, MediaTypeAttributeKeys.Mpeg2Profile })
                try { type.Set(k, native.GetUInt32(k)); } catch { }
            byte[]? seq = null;
            try { seq = native.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader); } catch { }
            if (seq is not { Length: > 0 }) seq = SpsPpsFromAvcC(init);
            if (seq is { Length: > 0 }) type.SetBlob(MediaTypeAttributeKeys.MpegSequenceHeader, seq);
            using var attrs = MediaFactory.MFCreateAttributes(1);
            attrs.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
            writer = MediaFactory.MFCreateSinkWriterFromURL(tmp, null!, attrs);
            // mapa: faixa do leitor -> faixa do gravador (vídeo com tipo limpo; áudio AAC copiado)
            var map = new Dictionary<int, int>();
            int videoIndex = -1;
            for (int si = 0; si < 8; si++)
            {
                IMFMediaType nt;
                try { nt = reader.GetNativeMediaType(si, 0); } catch { break; }
                using (nt)
                {
                    var major = nt.GetGUID(MediaTypeAttributeKeys.MajorType);
                    if (major == MediaTypeGuids.Video && videoIndex < 0)
                    {
                        videoIndex = si;
                        map[si] = writer.AddStream(type);
                        writer.SetInputMediaType(map[si], type, null);
                    }
                    else if (major == MediaTypeGuids.Audio)
                    {
                        using var at = MediaFactory.MFCreateMediaType();
                        nt.CopyAllItems(at);
                        map[si] = writer.AddStream(at);
                        writer.SetInputMediaType(map[si], at, null);
                    }
                }
            }
            writer.BeginWriting();
            while (true)
            {
                using var sample = reader.ReadSample(SourceReaderIndex.AnyStream, SourceReaderControlFlag.None, out var sIdx, out var flags, out var ts);
                if (sample == null)
                {
                    if ((flags & SourceReaderFlag.EndOfStream) != 0 && sIdx == videoIndex) break;
                    if ((flags & SourceReaderFlag.EndOfStream) != 0) continue;
                    break;
                }
                if (!map.TryGetValue(sIdx, out var stream) || ts < first) continue; // tudo antes do quadro-chave fica de fora
                // copia para uma amostra nova e limpa (as do leitor de MP4 fragmentado trazem atributos que o gravador recusa)
                using var clean = CopySample(sample, ts - first);
                try { writer.WriteSample(stream, clean); }
                catch (Exception ex) { throw new InvalidOperationException("Não deu para montar o clipe: " + ex.Message, ex); }
                if (sIdx == videoIndex) last = Math.Max(last, ts - first + sample.SampleDuration);
            }
            writer.Finalize();
        }
        finally { writer?.Dispose(); }
        File.Move(tmp, path, true);
        return last / 10_000_000.0;
    }

    /// <summary>
    /// Recebe o MP4 fragmentado do encoder e o separa em caixas: ftyp+moov (início) e moof+mdat (um fragmento por GOP).
    /// Nada é guardado além disso, então a memória fica limitada ao tamanho do buffer.
    /// </summary>
    private sealed class FragmentStream(Action<byte[]> onInit, Action<byte[], int, long> onFragment) : Stream
    {
        // Guarda só o trecho ainda não entregue: bytes de [_base, _base + _len).
        // O gravador de MP4 fragmentado volta para corrigir cada fragmento depois de escrevê-lo,
        // então um fragmento só é entregue quando o seguinte já começou.
        private byte[] _buf = new byte[4 << 20];
        private int _len;
        private long _base, _pos;
        private bool _initSent;

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _base + _len;
        public override long Position { get => _pos; set => Seek(value, SeekOrigin.Begin); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void SetLength(long value)
        {
            lock (this) { if (value < _base) throw new NotSupportedException(); Ensure((int)(value - _base)); _len = (int)(value - _base); }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (this)
            {
                var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _base + _len + offset };
                if (target < _base) throw new NotSupportedException("Volta para um trecho já entregue.");
                _pos = target;
                return _pos;
            }
        }

        private void Ensure(int size)
        {
            if (size > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, size));
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (this)
            {
                int at = (int)(_pos - _base);
                Ensure(at + count);
                Buffer.BlockCopy(buffer, offset, _buf, at, count);
                _pos += count;
                _len = Math.Max(_len, at + count);
                Parse();
                if (_len > 512 << 20) throw new InvalidOperationException("O encoder não está fechando fragmentos.");
            }
        }

        private (long Size, string Type)? BoxAt(int p)
        {
            if (_len - p < 8) return null;
            long size = (uint)(_buf[p] << 24 | _buf[p + 1] << 16 | _buf[p + 2] << 8 | _buf[p + 3]);
            var type = System.Text.Encoding.ASCII.GetString(_buf, p + 4, 4);
            if (size == 1)
            {
                if (_len - p < 16) return null;
                size = 0;
                for (int i = 0; i < 8; i++) size = size << 8 | _buf[p + 8 + i];
            }
            if (size < 8) return null;
            return (size, type);
        }

        private void Parse()
        {
            int pos = 0;
            int cut = 0;              // até onde já foi entregue
            int? moofStart = null;
            while (true)
            {
                var box = BoxAt(pos);
                if (box == null || _len - pos < box.Value.Size) break;
                var (size, type) = box.Value;
                if (type == "moof")
                {
                    // um moof completo depois do moov = o início já não muda mais
                    if (!_initSent && pos > 0) { onInit(_buf.AsSpan(0, pos).ToArray()); _initSent = true; cut = pos; }
                    // um moof completo depois de outro = o fragmento anterior está pronto
                    else if (moofStart is int prev && _initSent)
                    {
                        var frag = _buf.AsSpan(prev, pos - prev).ToArray();
                        onFragment(frag, SampleCount(frag), _base + prev);
                        cut = pos;
                    }
                    moofStart = pos;
                }
                pos += (int)size;
            }
            // descarta o que já foi entregue (nunca é reescrito)
            if (cut > 0 && _pos - _base >= cut)
            {
                Buffer.BlockCopy(_buf, cut, _buf, 0, _len - cut);
                _len -= cut;
                _base += cut;
            }
        }

        /// <summary>Quadros de VÍDEO do fragmento: sample_count do trun da faixa 1 (as de áudio vêm depois).</summary>
        private static int SampleCount(byte[] moof)
        {
            int track = -1;
            for (int i = 4; i + 12 <= moof.Length; i++)
            {
                if (moof[i] == 't' && moof[i + 1] == 'f' && moof[i + 2] == 'h' && moof[i + 3] == 'd')
                    track = moof[i + 8] << 24 | moof[i + 9] << 16 | moof[i + 10] << 8 | moof[i + 11];
                else if (moof[i] == 't' && moof[i + 1] == 'r' && moof[i + 2] == 'u' && moof[i + 3] == 'n' && track is 1 or -1)
                    return moof[i + 8] << 24 | moof[i + 9] << 16 | moof[i + 10] << 8 | moof[i + 11];
            }
            return 0;
        }
    }

    private static class Mm
    {
        [DllImport("avrt.dll", CharSet = CharSet.Unicode)] public static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
        [DllImport("avrt.dll")] public static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);
        [DllImport("avrt.dll")] public static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    }

    // ------------------------------------------------------------------
    // Codificador: MP4 na memória (fragmentado) ou direto no arquivo
    // ------------------------------------------------------------------
    private sealed class Writer
    {
        private readonly MemoryStream? _ms;
        private readonly MFByteStream? _bs;
        private readonly IMFSinkWriter _w;
        private readonly int _stream;
        public string? EncoderName { get; private set; }

        private Writer(MemoryStream? ms, MFByteStream? bs, IMFSinkWriter w, int stream) { _ms = ms; _bs = bs; _w = w; _stream = stream; }

        /// <param name="path">Arquivo de destino (gravação manual) ou null para gravar na memória (trecho do replay).</param>
        public static Writer Create(IMFDXGIDeviceManager manager, int width, int height, int fps, int bitrate, string? path = null, Stream? stream = null, int audioTracks = 0)
        {
            MemoryStream? ms = null;
            var bs = stream != null ? new MFByteStream(stream, false) : null;
            using var attrs = MediaFactory.MFCreateAttributes(4);
            attrs.Set(SinkWriterAttributeKeys.D3DManager, manager);
            attrs.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            // sem o modo de baixa latência: nele o encoder AMF não passa de ~38 quadros/s em 1080p60
            attrs.Set(SinkWriterAttributeKeys.DisableThrottling, 1u); // não bloqueia o laço de captura esperando o encoder
            // buffer do replay: MP4 fragmentado (um fragmento por GOP); gravação manual: MP4 normal no arquivo
            attrs.Set(TranscodeAttributeKeys.TranscodeContainertype, stream != null ? TranscodeContainerTypeGuids.Fmpeg4 : TranscodeContainerTypeGuids.Mpeg4);
            var w = MediaFactory.MFCreateSinkWriterFromURL(path!, bs!, attrs);

            ulong size = ((ulong)(uint)width << 32) | (uint)height;
            ulong rate = ((ulong)(uint)fps << 32) | 1u;
            ulong par = (1UL << 32) | 1u;

            using var outType = MediaFactory.MFCreateMediaType();
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            outType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressivo
            outType.Set(MediaTypeAttributeKeys.FrameSize, size);
            outType.Set(MediaTypeAttributeKeys.FrameRate, rate);
            outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, par);
            outType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u); // High
            var streamIdx = w.AddStream(outType);

            using var inType = MediaFactory.MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
            inType.Set(MediaTypeAttributeKeys.FrameSize, size);
            inType.Set(MediaTypeAttributeKeys.FrameRate, rate);
            inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, par);
            // Controle de taxa (ICodecAPI via atributos): VBR com pico limitado, GOP de 1 s.
            using var enc = MediaFactory.MFCreateAttributes(4);
            enc.Set(new Guid("1c0608e9-370c-4710-8a58-cb6181c42423"), 1u);                 // RateControlMode = PeakConstrainedVBR
            enc.Set(new Guid("f7222374-2144-4815-b550-a37f8e12ee52"), (uint)bitrate);      // MeanBitRate
            enc.Set(new Guid("9651eae4-39b9-4ebf-85ef-d7f444ec7465"), (uint)(bitrate * 3 / 2)); // MaxBitRate
            enc.Set(new Guid("95f31b26-95a4-41aa-9303-246a7fc6eef1"), (uint)fps);          // GOP
            try { w.SetInputMediaType(streamIdx, inType, enc); }
            catch { w.SetInputMediaType(streamIdx, inType, null); }

            // faixas de áudio AAC 48 kHz estéreo (1ª = mix jogo+microfone; 2ª, opcional = só microfone)
            var audio = new List<int>();
            for (int a = 0; a < audioTracks; a++)
            {
                using var aOut = MediaFactory.MFCreateMediaType();
                aOut.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                aOut.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                aOut.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                aOut.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioMixer.Rate);
                aOut.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
                aOut.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, a == 0 ? 24000u : 16000u); // 192 / 128 kbps
                aOut.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
                var ai = w.AddStream(aOut);
                using var aIn = MediaFactory.MFCreateMediaType();
                aIn.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                aIn.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
                aIn.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
                aIn.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioMixer.Rate);
                aIn.Set(MediaTypeAttributeKeys.AudioNumChannels, 2u);
                aIn.Set(MediaTypeAttributeKeys.AudioBlockAlignment, 4u);
                aIn.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(AudioMixer.Rate * 4));
                w.SetInputMediaType(ai, aIn, null);
                audio.Add(ai);
            }
            w.BeginWriting();

            var wr = new Writer(ms, bs, w, streamIdx) { _audio = audio.ToArray() };
            wr.EncoderName = TryEncoderName(w, streamIdx);
            return wr;
        }

        public static readonly List<string> Debug = [];

        private static string? TryEncoderName(IMFSinkWriter w, int stream)
        {
            try
            {
                using var ex = w.QueryInterface<IMFSinkWriterEx>();
                for (int i = 0; i < 6; i++)
                {
                    Guid cat; IMFTransform? mft;
                    try { ex.GetTransformForStream(stream, i, out cat, out mft); } catch { break; }
                    if (mft == null) break;
                    using (mft)
                    {
                        string? name = null, vendor = null;
                        try { using var a = mft.Attributes; name = a.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { }
                        try { using var a = mft.Attributes; vendor = a.GetString(TransformAttributeKeys.MftEnumHardwareVendorIdAttribute); } catch { }
                        lock (Debug) Debug.Add($"#{i} cat={cat} name={name} vendor={vendor}");
                        if (cat != TransformCategoryGuids.VideoEncoder) continue;
                        var v = vendor?.ToUpperInvariant() ?? "";
                        var brand = v.Contains("1002") ? "AMD" : v.Contains("10DE") ? "NVIDIA" : v.Contains("8086") ? "Intel" : null;
                        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
                        return brand != null ? $"H.264 · {brand} (hardware)" : "H.264";
                    }
                }
            }
            catch { }
            return null;
        }

        private int[] _audio = [];
        private volatile bool _closed;
        public int AudioTracks => _audio.Length;

        public void WriteSample(IMFSample sample, long time, long duration)
        {
            sample.SampleTime = time;
            sample.SampleDuration = duration;
            if (_closed) return;
            lock (_w) _w.WriteSample(_stream, sample);
        }

        /// <summary>PCM 16 bits estéreo para a faixa de áudio <paramref name="track"/>.</summary>
        public unsafe void WriteAudio(int track, short[] pcm, int frames, long time)
        {
            if (_closed || track >= _audio.Length) return;
            int bytes = frames * 4;
            using var buf = MediaFactory.MFCreateMemoryBuffer(bytes);
            buf.Lock(out var p, out _, out _);
            fixed (short* src = pcm) Buffer.MemoryCopy(src, (void*)p, bytes, bytes);
            buf.Unlock();
            buf.CurrentLength = bytes;
            using var s = MediaFactory.MFCreateSample();
            s.AddBuffer(buf);
            s.SampleTime = time;
            s.SampleDuration = frames * 10_000_000L / AudioMixer.Rate;
            lock (_w) _w.WriteSample(_audio[track], s);
        }

        public void Write(ID3D11Texture2D tex, long time, long duration)
        {
            using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, tex, 0, false);
            using (var b2 = buffer.QueryInterface<IMF2DBuffer>()) buffer.CurrentLength = b2.ContiguousLength;
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = time;
            sample.SampleDuration = duration;
            _w.WriteSample(_stream, sample);
        }

        public byte[] Finish()
        {
            _closed = true;
            try { lock (_w) _w.Finalize(); }
            finally { _w.Dispose(); _bs?.Dispose(); }
            return _ms?.ToArray() ?? [];
        }

        public void Abandon()
        {
            try { _w.Dispose(); _bs?.Dispose(); _ms?.Dispose(); } catch { }
        }
    }
}
