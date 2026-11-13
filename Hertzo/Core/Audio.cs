using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hertzo.Core;

// =====================================================================
// ÁUDIO DOS CLIPES
// WASAPI (a API de áudio do próprio Windows): loopback do dispositivo de
// saída padrão = som do jogo (tudo o que você ouve) e captura do microfone.
// Os dois são convertidos pelo Windows para 48 kHz estéreo e alinhados pelo
// mesmo relógio (QPC) do vídeo, então o som não "escorrega" da imagem.
// =====================================================================

public sealed record AudioDevice(string Id, string Name);

public static class AudioDevices
{
    /// <summary>Microfones ativos (nome amigável do Windows).</summary>
    public static List<AudioDevice> Microphones()
    {
        var list = new List<AudioDevice>();
        try
        {
            var en = (Wasapi.IMMDeviceEnumerator)new Wasapi.MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(en.EnumAudioEndpoints(1 /* eCapture */, 1 /* ACTIVE */, out var col));
            col.GetCount(out var n);
            for (int i = 0; i < n; i++)
            {
                col.Item(i, out var dev);
                dev.GetId(out var id);
                list.Add(new AudioDevice(id, Wasapi.FriendlyName(dev) ?? "Microfone"));
                Marshal.ReleaseComObject(dev);
            }
            Marshal.ReleaseComObject(col);
            Marshal.ReleaseComObject(en);
        }
        catch (Exception ex) { UiHost.Log(ex); }
        return list;
    }

    public static string? DefaultName(bool microphone)
    {
        try
        {
            var en = (Wasapi.IMMDeviceEnumerator)new Wasapi.MMDeviceEnumerator();
            if (en.GetDefaultAudioEndpoint(microphone ? 1 : 0, microphone ? 2 /* eCommunications */ : 0 /* eConsole */, out var dev) != 0) return null;
            var name = Wasapi.FriendlyName(dev);
            Marshal.ReleaseComObject(dev);
            Marshal.ReleaseComObject(en);
            return name;
        }
        catch { return null; }
    }
}

/// <summary>Uma fonte de áudio (loopback da saída ou microfone), entregando float estéreo 48 kHz.</summary>
internal sealed class WasapiSource : IDisposable
{
    public const int Rate = 48000;
    private readonly bool _loopback;
    private readonly string? _deviceId;
    private readonly Action<float[], int, long> _onPacket; // amostras intercaladas, quadros, QPC em 100 ns
    private readonly Thread _thread;
    private volatile bool _stop;
    public string DeviceName { get; private set; } = "";
    public string? Error { get; private set; }

    public WasapiSource(bool loopback, string? deviceId, Action<float[], int, long> onPacket)
    {
        _loopback = loopback;
        _deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        _onPacket = onPacket;
        _thread = new Thread(Run) { IsBackground = true, Name = loopback ? "Hertzo Áudio Jogo" : "Hertzo Áudio Mic", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        uint mmTask = 0;
        var mm = Wasapi.AvSetMmThreadCharacteristicsW("Audio", ref mmTask);
        try
        {
            while (!_stop)
            {
                try { CaptureSession(); }
                catch (Exception ex)
                {
                    // dispositivo trocado ou desconectado: tenta de novo com o padrão atual
                    Error = ex.Message;
                    UiHost.Log(ex);
                    for (int i = 0; i < 10 && !_stop; i++) Thread.Sleep(100);
                }
            }
        }
        finally { if (mm != IntPtr.Zero) Wasapi.AvRevertMmThreadCharacteristics(mm); }
    }

    private void CaptureSession()
    {
        var en = (Wasapi.IMMDeviceEnumerator)new Wasapi.MMDeviceEnumerator();
        Wasapi.IMMDevice? dev = null;
        Wasapi.IAudioClient? client = null;
        Wasapi.IAudioCaptureClient? cap = null;
        var fmt = IntPtr.Zero;
        try
        {
            int hr = _deviceId != null
                ? en.GetDevice(_deviceId, out dev)
                : en.GetDefaultAudioEndpoint(_loopback ? 0 : 1, _loopback ? 0 : 2, out dev);
            Marshal.ThrowExceptionForHR(hr);
            DeviceName = Wasapi.FriendlyName(dev) ?? "";

            var iid = typeof(Wasapi.IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(dev!.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out var obj));
            client = (Wasapi.IAudioClient)obj;

            // pede float 32 bits, 48 kHz, estéreo: o Windows converte taxa e canais sozinho
            fmt = Marshal.AllocHGlobal(18);
            Marshal.WriteInt16(fmt, 0, 3);            // WAVE_FORMAT_IEEE_FLOAT
            Marshal.WriteInt16(fmt, 2, 2);            // canais
            Marshal.WriteInt32(fmt, 4, Rate);
            Marshal.WriteInt32(fmt, 8, Rate * 8);     // bytes/s
            Marshal.WriteInt16(fmt, 12, 8);           // block align
            Marshal.WriteInt16(fmt, 14, 32);          // bits
            Marshal.WriteInt16(fmt, 16, 0);
            int flags = unchecked((int)0x80000000) /* AUTOCONVERTPCM */ | 0x08000000 /* SRC_DEFAULT_QUALITY */ | (_loopback ? 0x00020000 /* LOOPBACK */ : 0);
            Marshal.ThrowExceptionForHR(client.Initialize(0 /* SHARED */, flags, 2_000_000 /* 200 ms */, 0, fmt, IntPtr.Zero));

            var ciid = typeof(Wasapi.IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref ciid, out var co));
            cap = (Wasapi.IAudioCaptureClient)co;
            Marshal.ThrowExceptionForHR(client.Start());
            Error = null;

            var buf = new float[Rate * 2];
            while (!_stop)
            {
                Thread.Sleep(5);
                while (true)
                {
                    Marshal.ThrowExceptionForHR(cap.GetNextPacketSize(out var next));
                    if (next == 0) break;
                    Marshal.ThrowExceptionForHR(cap.GetBuffer(out var data, out var frames, out var bflags, out _, out var qpc));
                    int n = (int)frames * 2;
                    if (n > buf.Length) buf = new float[n];
                    if ((bflags & 0x2) != 0 /* SILENT */ || data == IntPtr.Zero) Array.Clear(buf, 0, n);
                    else Marshal.Copy(data, buf, 0, n);
                    cap.ReleaseBuffer(frames);
                    if (frames > 0) _onPacket(buf, (int)frames, (long)qpc);
                }
            }
            client.Stop();
        }
        finally
        {
            if (fmt != IntPtr.Zero) Marshal.FreeHGlobal(fmt);
            if (cap != null) Marshal.ReleaseComObject(cap);
            if (client != null) Marshal.ReleaseComObject(client);
            if (dev != null) Marshal.ReleaseComObject(dev);
            Marshal.ReleaseComObject(en);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
    }
}

/// <summary>
/// Mistura jogo + microfone numa linha do tempo única (índice de amostra a 48 kHz contado do início do vídeo).
/// Pacotes atrasados ou faltando viram silêncio, nunca deslocam o resto do áudio.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public const int Rate = WasapiSource.Rate;
    private const int Chunk = 960;          // 20 ms
    private const int Latency = Rate / 6;   // ~170 ms de folga para os pacotes chegarem

    /// <summary>(mix PCM 16 bits, só microfone ou null, índice da 1ª amostra, quadros)</summary>
    public event Action<short[], short[]?, long, int>? Output;

    public double GameLevel => _game?.Level ?? 0;
    public double MicLevel => _mic?.Level ?? 0;
    public string GameDevice => _gameSrc?.DeviceName ?? "";
    public string MicDevice => _micSrc?.DeviceName ?? "";
    public bool HasMic => _mic != null;
    public bool HasGame => _game != null;

    private readonly Track? _game, _mic;
    private readonly WasapiSource? _gameSrc, _micSrc;
    private volatile float _gameGain, _micGain;

    /// <summary>Volumes ao vivo (1 = 100%), sem reiniciar a captura.</summary>
    public void SetGains(double game, double mic) { _gameGain = (float)game; _micGain = (float)mic; }
    private readonly bool _micTrack;
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _base;     // QPC (100 ns) do instante zero
    private long _mixPos;

    public AudioMixer(bool game, double gameVolume, bool mic, double micVolume, string? micDevice, bool micTrack, long startQpc100ns)
    {
        _base = startQpc100ns;
        _gameGain = (float)gameVolume;
        _micGain = (float)micVolume;
        _micTrack = micTrack && mic;
        if (game) { _game = new Track(); _gameSrc = new WasapiSource(true, null, (b, f, q) => _game.Write(b, f, Index(q))); }
        if (mic) { _mic = new Track(); _micSrc = new WasapiSource(false, micDevice, (b, f, q) => _mic.Write(b, f, Index(q))); }
        _thread = new Thread(Loop) { IsBackground = true, Name = "Hertzo Mixer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public static long NowQpc100ns() => (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));

    private long Index(long qpc100ns) => (qpc100ns - _base) * Rate / 10_000_000;

    private void Loop()
    {
        var gameBuf = new float[Chunk * 2];
        var micBuf = new float[Chunk * 2];
        while (!_stop)
        {
            Thread.Sleep(10);
            long target = Index(NowQpc100ns()) - Latency;
            while (!_stop && _mixPos + Chunk <= target)
            {
                if (_mixPos < 0) { _mixPos = Math.Min(0, target - Chunk); if (_mixPos < 0) { _mixPos = 0; continue; } }
                Array.Clear(gameBuf); Array.Clear(micBuf);
                _game?.Read(_mixPos, Chunk, gameBuf, _gameGain);
                _mic?.Read(_mixPos, Chunk, micBuf, _micGain);
                var mix = new short[Chunk * 2];
                short[]? micOnly = _micTrack ? new short[Chunk * 2] : null;
                for (int i = 0; i < mix.Length; i++)
                {
                    mix[i] = ToShort(gameBuf[i] + micBuf[i]);
                    if (micOnly != null) micOnly[i] = ToShort(micBuf[i]);
                }
                try { Output?.Invoke(mix, micOnly, _mixPos, Chunk); } catch (Exception ex) { UiHost.Log(ex); }
                _mixPos += Chunk;
            }
        }
    }

    /// <summary>Limitador suave: evita estalo quando jogo + microfone passam do máximo.</summary>
    private static short ToShort(float v)
    {
        if (v > 0.95f) v = 0.95f + (float)Math.Tanh((v - 0.95f) * 8) * 0.05f;
        else if (v < -0.95f) v = -0.95f - (float)Math.Tanh((-v - 0.95f) * 8) * 0.05f;
        return (short)Math.Clamp(v * 32767f, -32768f, 32767f);
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
        _gameSrc?.Dispose();
        _micSrc?.Dispose();
    }

    /// <summary>Buffer circular de 4 s indexado pela posição absoluta na linha do tempo.</summary>
    private sealed class Track
    {
        private readonly float[] _buf = new float[Rate * 4 * 2];
        private long _end = -1, _expected = -1;
        private float _peak;
        private long _peakAt;

        public double Level
        {
            get
            {
                // decai em ~300 ms se não chegar som
                var age = Environment.TickCount64 - _peakAt;
                return Math.Max(0, _peak * (1 - age / 300.0));
            }
        }

        private int Cap => _buf.Length / 2;

        public void Write(float[] data, int frames, long index)
        {
            lock (this)
            {
                if (_expected < 0 || Math.Abs(index - _expected) > Chunk) _expected = index; // reancora se escorregar mais de 20 ms
                long start = _expected;
                if (_end >= 0 && start > _end)
                    for (long i = _end; i < start && i - _end < Cap; i++) { var p = (int)(Mod(i) * 2); _buf[p] = 0; _buf[p + 1] = 0; }
                float pk = 0;
                for (int f = 0; f < frames; f++)
                {
                    var p = (int)(Mod(start + f) * 2);
                    _buf[p] = data[f * 2];
                    _buf[p + 1] = data[f * 2 + 1];
                    pk = Math.Max(pk, Math.Max(Math.Abs(data[f * 2]), Math.Abs(data[f * 2 + 1])));
                }
                _end = Math.Max(_end, start + frames);
                _expected = start + frames;
                if (pk >= Level) { _peak = Math.Min(1, pk); _peakAt = Environment.TickCount64; }
            }
        }

        public void Read(long from, int frames, float[] dst, float gain)
        {
            lock (this)
            {
                for (int f = 0; f < frames; f++)
                {
                    long i = from + f;
                    if (i < 0 || i >= _end || i < _end - Cap) continue; // fora do que chegou = silêncio
                    var p = (int)(Mod(i) * 2);
                    dst[f * 2] += _buf[p] * gain;
                    dst[f * 2 + 1] += _buf[p + 1] * gain;
                }
            }
        }

        private long Mod(long i) => ((i % Cap) + Cap) % Cap;
    }
}

internal static class Wasapi
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey { public Guid Fmtid; public int Pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr Ptr;
    }

    [DllImport("ole32.dll")] internal static extern int PropVariantClear(ref PropVariant pv);
    [DllImport("avrt.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint index);
    [DllImport("avrt.dll")] internal static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    internal static string? FriendlyName(IMMDevice? dev)
    {
        if (dev == null) return null;
        if (dev.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return null;
        try
        {
            var key = new PropertyKey { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.Vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.Ptr) : null; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }
}
