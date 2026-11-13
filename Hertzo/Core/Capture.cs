using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Hertzo.Core;

/// <summary>
/// Captura do monitor pela Windows.Graphics.Capture (a API moderna da Microsoft, usada pelo OBS no modo
/// "Captura do Windows 10+"). Diferente do Desktop Duplication, ela recebe todos os quadros de jogos em
/// tela cheia — no teste, 141 quadros/s com o jogo a ~140 FPS, contra ~20/s do método antigo.
/// Nada é injetado no jogo.
/// </summary>
internal sealed class WgcSource : IDisposable
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(long pt, uint flags);

    private static readonly Guid IidCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidDxgiAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    public static bool Supported
    {
        get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
    }

    public int Width { get; }
    public int Height { get; }
    public long Frames => Interlocked.Read(ref _frames);

    private readonly IDirect3DDevice _wdev;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly Action<ID3D11Texture2D> _onFrame;
    private SizeInt32 _size;
    private long _frames;
    private volatile bool _disposed;

    public WgcSource(ID3D11Device device, Action<ID3D11Texture2D> onFrame)
    {
        _onFrame = onFrame;
        using (var dxgi = device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var gd));
            _wdev = MarshalInterface<IDirect3DDevice>.FromAbi(gd);
            Marshal.Release(gd);
        }

        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory.ThisPtr);
        var iid = IidCaptureItem;
        var hmon = MonitorFromPoint(0, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var itemPtr = interop.CreateForMonitor(hmon, ref iid);
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
        Marshal.Release(itemPtr);

        _size = item.Size;
        Width = _size.Width;
        Height = _size.Height;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_wdev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        // Windows 11: sem a borda amarela e sem o limite padrão de quadros por segundo
        try { _session.IsBorderRequired = false; } catch { }
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        try { _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1); } catch { }
        _session.StartCapture();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        if (_disposed) return;
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame == null) return;
            var cs = frame.ContentSize;
            if (cs.Width != _size.Width || cs.Height != _size.Height)
            {
                // a resolução da tela mudou (jogo em tela cheia com resolução própria)
                _size = cs;
                pool.Recreate(_wdev, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                return;
            }
            using var tex = TextureOf(frame.Surface);
            _onFrame(tex);
            Interlocked.Increment(ref _frames);
        }
        catch (Exception ex) { UiHost.Log(ex); }
    }

    private static unsafe ID3D11Texture2D TextureOf(IDirect3DSurface surface)
    {
        var abi = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var iid = IidDxgiAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(abi, ref iid, out var access));
            try
            {
                // IDirect3DDxgiInterfaceAccess::GetInterface (posição 3 da vtable)
                var texIid = typeof(ID3D11Texture2D).GUID;
                IntPtr tex;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(IntPtr**)access)[3];
                Marshal.ThrowExceptionForHR(fn(access, &texIid, &tex));
                return new ID3D11Texture2D(tex);
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(abi); }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
        try { _wdev.Dispose(); } catch { }
    }
}
