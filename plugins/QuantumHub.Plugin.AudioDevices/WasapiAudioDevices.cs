using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace QuantumHub.Plugin.AudioDevices;

/// <summary>Настоящие звуковые устройства Windows.</summary>
public sealed class WasapiAudioDevices
{
    private readonly ConcurrentDictionary<uint, string> _names = new();

    /// <summary>Все активные устройства.</summary>
    public AudioList List() => new(ListFlow(DataFlow.Render), ListFlow(DataFlow.Capture));

    /// <summary>Устройства по умолчанию.</summary>
    public AudioCurrent Current() => new(Default(DataFlow.Render), Default(DataFlow.Capture));

    private static List<AudioDevice> ListFlow(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDevice>();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (endpoint) devices.Add(new AudioDevice(endpoint.ID, endpoint.FriendlyName));
        }

        return devices;
    }

    private static AudioDevice? Default(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)) return null;

        using var endpoint = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        return new AudioDevice(endpoint.ID, endpoint.FriendlyName);
    }

    /// <summary>
    /// Приложения, в которых сейчас идёт звук, по всем устройствам вывода. Одно приложение может выводить
    /// на несколько устройств и из нескольких процессов, поэтому всё сводится по имени с самым громким пиком.
    /// </summary>
    public IReadOnlyList<AudioSession> Sessions()
    {
        var result = new Dictionary<string, AudioSession>(StringComparer.OrdinalIgnoreCase);
        ForEachSession((name, session) =>
        {
            if (session.State != AudioSessionState.AudioSessionStateActive) return;

            var found = new AudioSession(name, session.SimpleAudioVolume.Volume, session.AudioMeterInformation.MasterPeakValue, session.SimpleAudioVolume.Mute);
            if (!result.TryGetValue(name, out var existing) || existing.Peak < found.Peak) result[name] = found;
        });
        return result.Values.ToList();
    }

    /// <summary>Меняет громкость приложения во всех его сессиях.</summary>
    /// <param name="process">Имя процесса.</param>
    /// <param name="volume">Громкость от 0 до 1.</param>
    public void SetSessionVolume(string process, double volume) => ForEachSession((name, session) =>
    {
        if (name.Equals(process, StringComparison.OrdinalIgnoreCase)) session.SimpleAudioVolume.Volume = (float)Math.Clamp(volume, 0, 1);
    });

    private void ForEachSession(Action<string, AudioSessionControl> action)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (endpoint)
            {
                var sessions = endpoint.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.IsSystemSoundsSession) continue;
                    if (ProcessName(session.GetProcessID) is { } name) action(name, session);
                }
            }
        }
    }

    /// <summary>Имена кешируются: это вызывается часто, а процессы живут долго.</summary>
    private string? ProcessName(uint pid)
    {
        if (_names.TryGetValue(pid, out var name)) return name;
        if (_names.Count > 512) _names.Clear();
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return _names[pid] = process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null; // Уже завершился.
        }
    }

    /// <summary>Делает устройство устройством по умолчанию для всех ролей.</summary>
    /// <param name="deviceId">Id устройства.</param>
    public void SetDefault(string deviceId)
    {
        // Публичного API для этого в Windows нет. IPolicyConfig недокументирован, но им
        // пользуется сама панель управления, поэтому он стабилен уже много лет.
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, (uint)role));
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    /// <summary>Недокументированный интерфейс настроек звука. Важен порядок методов, а не их использование.</summary>
    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceName, nint format);
        [PreserveSig] int GetDeviceFormat(string deviceName, bool isDefault, nint format);
        [PreserveSig] int ResetDeviceFormat(string deviceName);
        [PreserveSig] int SetDeviceFormat(string deviceName, nint endpointFormat, nint defaultFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceName, bool isDefault, nint defaultPeriod, nint minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceName, nint period);
        [PreserveSig] int GetShareMode(string deviceName, nint mode);
        [PreserveSig] int SetShareMode(string deviceName, nint mode);
        [PreserveSig] int GetPropertyValue(string deviceName, bool fxStore, nint key, nint value);
        [PreserveSig] int SetPropertyValue(string deviceName, bool fxStore, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceName, uint role);
        [PreserveSig] int SetEndpointVisibility(string deviceName, bool visible);
    }

    /// <summary>COM-класс, реализующий <see cref="IPolicyConfig"/>.</summary>
    [ComImport]
    [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;
}
