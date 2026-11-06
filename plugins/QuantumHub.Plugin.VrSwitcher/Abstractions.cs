namespace QuantumHub.Plugin.VrSwitcher;

/// <summary>Направление звука.</summary>
public enum AudioFlow
{
    /// <summary>Вывод: наушники и динамики.</summary>
    Playback,

    /// <summary>Ввод: микрофоны.</summary>
    Recording,
}

/// <summary>Звуковое устройство.</summary>
/// <param name="Id">Постоянный id устройства.</param>
/// <param name="Name">Имя для показа, например "Speakers (Pico Streaming Speaker)".</param>
public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// Что движку нужно от звука. В работе за этим стоит возможность audio.devices другого плагина,
/// а в тестах подделка, поэтому настоящий звук они не трогают.
/// </summary>
public interface IAudioDevices
{
    /// <summary>Активные устройства.</summary>
    /// <param name="flow">Направление.</param>
    Task<IReadOnlyList<AudioDevice>> ListAsync(AudioFlow flow);

    /// <summary>Id устройства по умолчанию или <see langword="null"/>, если такого нет.</summary>
    /// <param name="flow">Направление.</param>
    Task<string?> GetDefaultAsync(AudioFlow flow);

    /// <summary>Делает устройство устройством по умолчанию.</summary>
    /// <param name="deviceId">Id устройства.</param>
    Task SetDefaultAsync(string deviceId);
}

/// <summary>Определяет, запущен ли SteamVR.</summary>
public interface IVrProbe
{
    /// <summary>Запущен ли SteamVR прямо сейчас.</summary>
    bool IsRunning();
}

/// <summary>
/// Что было устройством по умолчанию до VR. Хранится на диске: если хаб упадёт посреди
/// VR-сессии, после запуска звук всё равно вернётся.
/// </summary>
public interface IVrAudioMemory
{
    /// <summary>Устройство вывода до VR.</summary>
    string? Playback { get; set; }

    /// <summary>Устройство ввода до VR.</summary>
    string? Recording { get; set; }

    /// <summary>Последнее устройство вывода не из гарнитуры. Нужно, если на момент переключения звук уже был на гарнитуре.</summary>
    string? DesktopPlayback { get; set; }

    /// <summary>Последнее устройство ввода не из гарнитуры.</summary>
    string? DesktopRecording { get; set; }
}

/// <summary>Настройки, которые движок читает на каждом шаге.</summary>
/// <param name="Preset">Какая гарнитура.</param>
/// <param name="AutoSwitch">Переключать ли автоматически.</param>
/// <param name="PcPlayback">Часть имени устройства вывода после VR. Пусто значит вернуть прежнее.</param>
/// <param name="PcRecording">Часть имени устройства ввода после VR. Пусто значит вернуть прежнее.</param>
/// <param name="ExitGracePolls">
/// Сколько проверок подряд SteamVR должен отсутствовать, чтобы считаться закрытым.
/// При перезапуске SteamVR звук не должен дёргаться туда и обратно.
/// </param>
public sealed record VrAudioOptions(
    VrAudioPreset Preset,
    bool AutoSwitch,
    string PcPlayback = "",
    string PcRecording = "",
    int ExitGracePolls = 3);
