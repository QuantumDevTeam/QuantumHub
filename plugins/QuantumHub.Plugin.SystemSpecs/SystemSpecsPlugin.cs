using System.Runtime.InteropServices;
using Microsoft.Win32;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.SystemSpecs;

/// <summary>Характеристики компьютера.</summary>
/// <param name="Machine">Имя компьютера.</param>
/// <param name="Os">Операционная система.</param>
/// <param name="Cpu">Процессор.</param>
/// <param name="Threads">Логических потоков.</param>
/// <param name="RamGb">Оперативная память в гигабайтах.</param>
/// <param name="Gpus">Видеокарты.</param>
public sealed record SystemSpecs(string Machine, string Os, string Cpu, int Threads, double RamGb, string[] Gpus);

/// <summary>
/// Характеристики компьютера и его текущая загрузка, возможности system.specs и system.stats. Это моно-DLL:
/// манифест встроен в сборку. Запускается только по требованию.
/// </summary>
[HubPlugin]
public sealed class SystemSpecsPlugin : IHubPlugin, IUiProvider
{
    /// <summary>Как часто обновлять загрузку, пока страница открыта.</summary>
    private const int LoadRefreshMs = 2000;

    private SystemSpecs _specs = null!;

    private IPluginContext _context = null!;
    private SystemStatsReader _stats = null!;

    /// <inheritdoc/>
    public Task StartAsync(IPluginContext context, CancellationToken token)
    {
        var specs = _specs = Read();
        _stats = new SystemStatsReader();
        _context = context;
        context.Bus.Handle("specs.get", (_, _) => ValueTask.FromResult(HubPayload.Json(specs)));
        context.Bus.Handle("stats.get", (_, _) => ValueTask.FromResult(HubPayload.Json(_stats.Read())));

        PushSpecs();
        return Task.CompletedTask;
    }

    /// <summary>Показывает характеристики на текущем языке.</summary>
    private void PushSpecs()
    {
        _context.Ui.Set("machine", _specs.Machine);
        _context.Ui.Set("os", _specs.Os);
        _context.Ui.Set("cpu", Loc.T($"{_specs.Cpu} ({_specs.Threads} потоков)", $"{_specs.Cpu} ({_specs.Threads} threads)"));
        _context.Ui.Set("ram", Loc.T($"{_specs.RamGb:F1} ГБ", $"{_specs.RamGb:F1} GB"));
        _context.Ui.Set("gpu", string.Join(", ", _specs.Gpus));
    }

    /// <inheritdoc/>
    public void OnLanguageChanged() => PushSpecs();

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken token)
    {
        _stats.Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        yield return Ui.Page("main", Loc.T("Система", "System"), "E7F4",
            Ui.KeyValue(Loc.T("Компьютер", "Computer"), "machine"),
            Ui.KeyValue(Loc.T("ОС", "OS"), "os"),
            Ui.KeyValue(Loc.T("Процессор", "Processor"), "cpu"),
            Ui.KeyValue(Loc.T("Память", "Memory"), "ram"),
            Ui.KeyValue(Loc.T("Видеокарта", "Graphics card"), "gpu"),
            Ui.Text(Loc.T("Загрузка", "Load"), "subtitle"),
            Ui.KeyValue(Loc.T("Сейчас", "Now"), "load"),
            Ui.Live("live", LoadRefreshMs));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
        if (uiEvent.NodeId != "live" || uiEvent.Event == "closed") return;
        var stats = _stats.Read();
        _context.Ui.Set("load", Loc.T($"CPU {stats.CpuPercent:0}%, GPU {stats.GpuPercent:0}%, RAM {stats.RamUsedGb:0.0}/{stats.RamTotalGb:0.0} ГБ, ", $"CPU {stats.CpuPercent:0}%, GPU {stats.GpuPercent:0}%, RAM {stats.RamUsedGb:0.0}/{stats.RamTotalGb:0.0} GB, ") +
                                Loc.T($"VRAM {stats.VramUsedGb:0.0}/{stats.VramTotalGb:0.0} ГБ", $"VRAM {stats.VramUsedGb:0.0}/{stats.VramTotalGb:0.0} GB"));
    }

    /// <summary>Читает характеристики через реестр и WinAPI, без тяжёлого WMI.</summary>
    private static SystemSpecs Read()
    {
        var cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString", null) as string;
        return new SystemSpecs(Environment.MachineName, RuntimeInformation.OSDescription, cpu?.Trim() ?? Loc.T("неизвестно", "unknown"),
            Environment.ProcessorCount, Math.Round(TotalMemoryGb(), 1), Gpus());
    }

    /// <summary>Объём оперативной памяти.</summary>
    private static double TotalMemoryGb()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? status.TotalPhysical / 1024d / 1024d / 1024d : 0;
    }

    /// <summary>Имена видеокарт без повторов.</summary>
    private static string[] Gpus()
    {
        var names = new List<string>();
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            if (!names.Contains(device.DeviceString)) names.Add(device.DeviceString);
        }

        return names.ToArray();
    }

    /// <summary>Структура MEMORYSTATUSEX из WinAPI.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    /// <summary>Структура DISPLAY_DEVICE из WinAPI.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);
}
