using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace QuantumHub.Plugin.SystemSpecs;

/// <summary>Текущая загрузка компьютера. Ответ stats.get.</summary>
/// <param name="CpuPercent">Загрузка процессора.</param>
/// <param name="RamUsedGb">Занятая оперативная память.</param>
/// <param name="RamTotalGb">Всего оперативной памяти.</param>
/// <param name="GpuPercent">Загрузка видеокарты, 3D-движок. <see langword="null"/>, если счётчиков нет.</param>
/// <param name="VramUsedGb">Занятая видеопамять. <see langword="null"/>, если счётчиков нет.</param>
/// <param name="VramTotalGb">Всего видеопамяти. <see langword="null"/>, если неизвестно.</param>
public sealed record SystemStats(
    double CpuPercent,
    double RamUsedGb,
    double RamTotalGb,
    double? GpuPercent,
    double? VramUsedGb,
    double? VramTotalGb);

/// <summary>Вычисляет проценты по двум замерам счётчиков. Выделено, потому что ошибиться здесь легко.</summary>
public static class CpuLoad
{
    /// <summary>Загрузка процессора между замерами.</summary>
    /// <param name="idleDelta">Прирост времени простоя.</param>
    /// <param name="totalDelta">Прирост всего времени: ядро и пользователь. Время ядра включает простой.</param>
    public static double Percent(ulong idleDelta, ulong totalDelta) =>
        totalDelta == 0 ? 0 : Math.Clamp(100d * (totalDelta - Math.Min(idleDelta, totalDelta)) / totalDelta, 0, 100);
}

/// <summary>
/// Читает загрузку через WinAPI и счётчики PDH. WMI здесь намеренно нет: он в разы тяжелее. Замеры
/// кешируются на секунду, поэтому частые запросы ничего не стоят.
/// </summary>
public sealed class SystemStatsReader : IDisposable
{
    private const string GpuEngineCounter = @"\GPU Engine(*engtype_3D)\Utilization Percentage";
    private const string GpuMemoryCounter = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const uint FormatDouble = 0x200;
    private const uint MoreData = 0x800007D2;
    private const double Gb = 1024d * 1024 * 1024;

    private static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly double? _vramTotalGb = ReadVramTotalGb();
    private nint _query;
    private nint _gpuCounter;
    private nint _memoryCounter;
    private ulong _lastIdle;
    private ulong _lastTotal;
    private SystemStats? _cached;
    private long _cachedAt;

    /// <summary>Открывает счётчики и делает первый замер: проценты считаются по разнице между замерами.</summary>
    public SystemStatsReader()
    {
        if (PdhOpenQuery(null, 0, out _query) == 0)
        {
            PdhAddEnglishCounter(_query, GpuEngineCounter, 0, out _gpuCounter);
            PdhAddEnglishCounter(_query, GpuMemoryCounter, 0, out _memoryCounter);
            PdhCollectQueryData(_query);
        }

        ReadCpu();
    }

    /// <summary>Текущая загрузка.</summary>
    public SystemStats Read()
    {
        lock (_gate)
        {
            if (_cached is not null && Environment.TickCount64 - _cachedAt < CacheTime.TotalMilliseconds) return _cached;

            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            GlobalMemoryStatusEx(ref memory);

            double? gpu = null;
            double? vram = null;
            if (_query != 0 && PdhCollectQueryData(_query) == 0)
            {
                gpu = _gpuCounter == 0 ? null : Math.Min(Sum(_gpuCounter), 100);
                vram = _memoryCounter == 0 ? null : Sum(_memoryCounter) / Gb;
            }

            _cached = new SystemStats(ReadCpu(), (memory.TotalPhysical - memory.AvailablePhysical) / Gb, memory.TotalPhysical / Gb,
                gpu, vram, _vramTotalGb);
            _cachedAt = Environment.TickCount64;
            return _cached;
        }
    }

    private double ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        var total = kernel + user;
        var percent = CpuLoad.Percent(idle - _lastIdle, total - _lastTotal);
        _lastIdle = idle;
        _lastTotal = total;
        return percent;
    }

    /// <summary>Складывает значения всех экземпляров счётчика: по одному на каждый процесс и движок.</summary>
    private static double Sum(nint counter)
    {
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, FormatDouble, ref size, out _, 0) != MoreData || size == 0) return 0;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, FormatDouble, ref size, out var count, buffer) != 0) return 0;

            double sum = 0;
            var itemSize = Marshal.SizeOf<CounterItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * itemSize);
                if (item.Status == 0) sum += item.Value;
            }

            return sum;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static double? ReadVramTotalGb()
    {
        using var root = Registry.LocalMachine.OpenSubKey(DisplayClass);
        if (root is null) return null;

        long best = 0;
        foreach (var name in root.GetSubKeyNames())
        {
            if (!int.TryParse(name, out _)) continue;
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("HardwareInformation.qwMemorySize") is long size) best = Math.Max(best, size);
        }

        return best > 0 ? best / Gb : null;
    }

    /// <summary>Закрывает счётчики.</summary>
    public void Dispose()
    {
        if (_query != 0) PdhCloseQuery(_query);
        _query = 0;
    }

    /// <summary>Структура PDH_FMT_COUNTERVALUE_ITEM_W для значения double.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public nint Name;
        public uint Status;
        public double Value;
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

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")] private static extern uint PdhOpenQuery(string? source, nint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern uint PdhAddEnglishCounter(nint query, string path, nint user, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint size, out uint count, nint buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}
