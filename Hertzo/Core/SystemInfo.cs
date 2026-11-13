using System.Management;
using Microsoft.Win32;

namespace Hertzo.Core;

public sealed class GpuInfo
{
    public string Name { get; init; } = "";
    public ulong VramBytes { get; init; }
    public string DriverVersion { get; init; } = "";
    public DateTime? DriverDate { get; init; }
    public string Vendor => Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
        : Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD"
        : Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "Outro";
    public bool IsIntegrated => Vendor == "Intel" && !Name.Contains("Arc", StringComparison.OrdinalIgnoreCase)
        || Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
        || Name.Equals("AMD Radeon Graphics", StringComparison.OrdinalIgnoreCase);
}

public sealed class RamModule
{
    public ulong Capacity { get; init; }
    public int ConfiguredMHz { get; init; }
    public int RatedMHz { get; init; }
    public string Type { get; init; } = "";
    public string Locator { get; init; } = "";
}

public sealed class DisplayInfo
{
    public string DeviceName { get; init; } = "";
    public string MonitorName { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public int CurrentHz { get; init; }
    public int MaxHz { get; init; }
    public bool IsPrimary { get; init; }
    public bool BelowMax => MaxHz - CurrentHz >= 5;
}

public sealed class HardwareSnapshot
{
    public string CpuName { get; set; } = "CPU";
    public int Cores { get; set; }
    public int Threads { get; set; }
    public int MaxClockMHz { get; set; }
    public List<GpuInfo> Gpus { get; } = [];
    public List<RamModule> Modules { get; } = [];
    public ulong RamTotal { get; set; }
    public string OsName { get; set; } = "Windows";
    public string OsVersion { get; set; } = "";
    public int OsBuild { get; set; }
    public string Board { get; set; } = "";
    public bool IsLaptop { get; set; }
    public bool? VbsRunning { get; set; }
    public bool? HvciRunning { get; set; }

    public GpuInfo? MainGpu => Gpus.FirstOrDefault(g => !g.IsIntegrated) ?? Gpus.FirstOrDefault();
    public string RamType => Modules.FirstOrDefault()?.Type ?? "";
    public int RamMHz => Modules.Count == 0 ? 0 : Modules.Min(m => m.ConfiguredMHz > 0 ? m.ConfiguredMHz : m.RatedMHz);
    public bool IsWin11 => OsBuild >= 22000;
}

public static class SystemInfo
{
    public static HardwareSnapshot Read()
    {
        var hw = new HardwareSnapshot();

        Try(() =>
        {
            foreach (var o in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
            {
                hw.CpuName = Clean(o["Name"]?.ToString() ?? "CPU");
                hw.Cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
                hw.Threads += Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
                hw.MaxClockMHz = Convert.ToInt32(o["MaxClockSpeed"] ?? 0);
            }
        });

        Try(() =>
        {
            foreach (var o in Query("SELECT Name, AdapterRAM, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController"))
            {
                var name = o["Name"]?.ToString() ?? "";
                var pnp = o["PNPDeviceID"]?.ToString() ?? "";
                if (name.Length == 0 || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;
                if (!pnp.StartsWith("PCI", StringComparison.OrdinalIgnoreCase)) continue; // ignora adaptadores virtuais
                DateTime? date = null;
                try { if (o["DriverDate"] is string ds) date = ManagementDateTimeConverter.ToDateTime(ds); } catch { }
                var vram = VramFromRegistry(name);
                if (vram == 0) vram = Convert.ToUInt64(o["AdapterRAM"] ?? 0UL);
                hw.Gpus.Add(new GpuInfo { Name = name, VramBytes = vram, DriverVersion = o["DriverVersion"]?.ToString() ?? "", DriverDate = date });
            }
        });

        Try(() =>
        {
            foreach (var o in Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, DeviceLocator FROM Win32_PhysicalMemory"))
            {
                var t = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0);
                hw.Modules.Add(new RamModule
                {
                    Capacity = Convert.ToUInt64(o["Capacity"] ?? 0UL),
                    RatedMHz = Convert.ToInt32(o["Speed"] ?? 0),
                    ConfiguredMHz = Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0),
                    Type = t switch { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 30 => "LPDDR4", 35 => "LPDDR5", _ => "" },
                    Locator = o["DeviceLocator"]?.ToString() ?? "",
                });
            }
        });

        var mem = Native.MEMORYSTATUSEX.Default();
        if (Native.GlobalMemoryStatusEx(ref mem)) hw.RamTotal = mem.ullTotalPhys;

        Try(() =>
        {
            foreach (var o in Query("SELECT Caption, BuildNumber FROM Win32_OperatingSystem"))
            {
                hw.OsName = (o["Caption"]?.ToString() ?? "Windows").Replace("Microsoft ", "");
                hw.OsBuild = int.TryParse(o["BuildNumber"]?.ToString(), out var b) ? b : Environment.OSVersion.Version.Build;
            }
        });
        if (hw.OsBuild == 0) hw.OsBuild = Environment.OSVersion.Version.Build;
        // A Caption do WMI às vezes diz "Windows 10" no 11; o build é a fonte confiável.
        if (hw.OsBuild >= 22000) hw.OsName = hw.OsName.Replace("Windows 10", "Windows 11");
        var dv = Reg.GetString(Hive.HKLM, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion") ?? "";
        var ubr = Reg.GetDword(Hive.HKLM, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");
        hw.OsVersion = $"{dv} · build {hw.OsBuild}{(ubr is int u ? "." + u : "")}".Trim(' ', '·');

        Try(() =>
        {
            foreach (var o in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                hw.Board = Clean($"{o["Manufacturer"]} {o["Product"]}");
        });

        if (Native.GetSystemPowerStatus(out var ps)) hw.IsLaptop = ps.BatteryFlag != 128 && ps.BatteryFlag != 255;

        Try(() =>
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\DeviceGuard");
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_DeviceGuard"));
            foreach (ManagementObject o in s.Get())
            {
                hw.VbsRunning = Convert.ToInt32(o["VirtualizationBasedSecurityStatus"] ?? 0) == 2;
                hw.HvciRunning = o["SecurityServicesRunning"] is uint[] arr && arr.Contains(2u);
            }
        });

        return hw;
    }

    public static bool OnBattery() => Native.GetSystemPowerStatus(out var ps) && ps.ACLineStatus == 0 && ps.BatteryFlag != 128;

    public static (long Free, long Total) SystemDrive()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            return (d.AvailableFreeSpace, d.TotalSize);
        }
        catch { return (0, 0); }
    }

    public static List<DisplayInfo> Displays()
    {
        var list = new List<DisplayInfo>();
        var dd = Native.DISPLAY_DEVICE.Create();
        for (uint i = 0; Native.EnumDisplayDevices(null, i, ref dd, 0); i++, dd = Native.DISPLAY_DEVICE.Create())
        {
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
            var cur = Native.DEVMODE.Create();
            if (!Native.EnumDisplaySettings(dd.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref cur)) continue;

            int max = cur.dmDisplayFrequency;
            var mode = Native.DEVMODE.Create();
            for (int m = 0; Native.EnumDisplaySettings(dd.DeviceName, m, ref mode); m++, mode = Native.DEVMODE.Create())
            {
                if (mode.dmPelsWidth == cur.dmPelsWidth && mode.dmPelsHeight == cur.dmPelsHeight
                    && (mode.dmDisplayFlags & Native.DM_INTERLACED) == 0 && mode.dmDisplayFrequency > max)
                    max = mode.dmDisplayFrequency;
            }

            var mon = Native.DISPLAY_DEVICE.Create();
            var monName = Native.EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) ? mon.DeviceString : "Monitor";
            if (monName.Contains("Generic", StringComparison.OrdinalIgnoreCase) || monName.Contains("Genérico", StringComparison.OrdinalIgnoreCase) || monName.Contains("PnP"))
                monName = "Monitor";

            list.Add(new DisplayInfo
            {
                DeviceName = dd.DeviceName,
                MonitorName = monName,
                Width = cur.dmPelsWidth,
                Height = cur.dmPelsHeight,
                CurrentHz = cur.dmDisplayFrequency,
                MaxHz = max,
                IsPrimary = (dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
            });
        }
        return list.OrderByDescending(d => d.IsPrimary).ToList();
    }

    /// <summary>Muda a taxa de atualização mantendo a resolução. Retorna o código do Windows (0 = sucesso).</summary>
    public static int SetRefreshRate(string device, int hz)
    {
        var dm = Native.DEVMODE.Create();
        if (!Native.EnumDisplaySettings(device, Native.ENUM_CURRENT_SETTINGS, ref dm)) return -1;
        dm.dmDisplayFrequency = hz;
        dm.dmFields = Native.DM_DISPLAYFREQUENCY | Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT;
        var test = Native.ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, Native.CDS_TEST, IntPtr.Zero);
        if (test != Native.DISP_CHANGE_SUCCESSFUL) return test;
        return Native.ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
    }

    private static ulong VramFromRegistry(string gpuName)
    {
        const string cls = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var b = Reg.Base(Hive.HKLM);
            using var k = b.OpenSubKey(cls);
            if (k == null) return 0;
            foreach (var sub in k.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var s = k.OpenSubKey(sub);
                if (s?.GetValue("DriverDesc") is not string desc || !desc.Equals(gpuName, StringComparison.OrdinalIgnoreCase)) continue;
                var v = s.GetValue("HardwareInformation.qwMemorySize");
                if (v is long l) return (ulong)l;
                if (v is byte[] bytes && bytes.Length >= 8) return BitConverter.ToUInt64(bytes, 0);
                if (s.GetValue("HardwareInformation.MemorySize") is int i) return (uint)i;
                if (s.GetValue("HardwareInformation.MemorySize") is byte[] b4 && b4.Length >= 4) return BitConverter.ToUInt32(b4, 0);
            }
        }
        catch { }
        return 0;
    }

    private static IEnumerable<ManagementBaseObject> Query(string wql)
    {
        using var s = new ManagementObjectSearcher(wql);
        foreach (var o in s.Get()) yield return o;
    }

    private static void Try(Action a) { try { a(); } catch { } }

    private static string Clean(string s) => string.Join(' ', s.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

    public static string Bytes(double b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:0} {u[i]}" : $"{b:0.#} {u[i]}";
    }
}

/// <summary>Leitura leve de uso de CPU/RAM em tempo real (sem contadores de desempenho pesados).</summary>
public sealed class LiveMonitor
{
    private ulong _idle, _kernel, _user;

    public double CpuPercent()
    {
        if (!Native.GetSystemTimes(out var i, out var k, out var u)) return 0;
        ulong di = i.Value - _idle, dk = k.Value - _kernel, du = u.Value - _user;
        bool first = _idle == 0;
        _idle = i.Value; _kernel = k.Value; _user = u.Value;
        if (first) return 0;
        var total = dk + du;
        return total == 0 ? 0 : Math.Clamp((1.0 - (double)di / total) * 100.0, 0, 100);
    }

    public static (double Percent, ulong Used, ulong Total) Ram()
    {
        var m = Native.MEMORYSTATUSEX.Default();
        if (!Native.GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        return (m.dwMemoryLoad, m.ullTotalPhys - m.ullAvailPhys, m.ullTotalPhys);
    }
}
