using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.VrSwitcher;

/// <summary>
/// Логика плагина: когда переключать звук и куда возвращать. С внешним миром работает только через
/// интерфейсы, поэтому полностью проверяется тестами.
/// </summary>
/// <remarks>
/// <para>
/// Пока идёт сессия и переключение ещё не удалось, движок пробует снова на каждой проверке: аудиоустройства
/// гарнитуры часто появляются позже SteamVR. После удачного переключения он больше не вмешивается, и если
/// пользователь сам выбрал другое устройство, спорить с ним никто не будет.
/// </para>
/// <para>
/// Прежние устройства запоминаются один раз на сессию и только если это не сама гарнитура: иначе после сбоя
/// можно запомнить её как «обычный» звук и никогда не вернуться.
/// </para>
/// </remarks>
/// <param name="audio">Звуковые устройства.</param>
/// <param name="probe">Определение SteamVR.</param>
/// <param name="memory">Хранилище прежних устройств.</param>
/// <param name="options">Текущие настройки.</param>
/// <param name="log">Куда писать смену статуса.</param>
public sealed class VrAudioEngine(
    IAudioDevices audio,
    IVrProbe probe,
    IVrAudioMemory memory,
    Func<VrAudioOptions> options,
    Action<string> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _polledOnce;
    private volatile bool _applyPending;
    private volatile bool _applied;
    private int _missedPolls;

    /// <summary>Идёт ли VR-сессия.</summary>
    public bool SessionActive { get; private set; }

    /// <summary>Переведён ли звук на гарнитуру.</summary>
    public bool OnHeadset => _applied || _playbackOnHeadset;

    /// <summary>Сообщает, какое устройство сейчас стоит по умолчанию.</summary>
    /// <param name="flow">Направление.</param>
    /// <param name="device">Устройство.</param>
    public void NoteDefault(AudioFlow flow, AudioDevice? device)
    {
        if (device is null) return;

        var preset = options().Preset;
        var onHeadset = VrAudioPreset.Find([device], flow == AudioFlow.Playback ? preset.Playback : preset.Recording) is not null;
        if (flow == AudioFlow.Playback) _playbackOnHeadset = onHeadset;
        if (onHeadset) return;

        if (flow == AudioFlow.Playback) memory.DesktopPlayback = device.Id;
        else memory.DesktopRecording = device.Id;
    }

    /// <summary>Что сейчас происходит, для показа пользователю.</summary>
    public string Status { get; private set; } = Loc.T("Ждём SteamVR", "Waiting for SteamVR");

    /// <summary>Сессия началась или закончилась.</summary>
    public event Action<bool>? SessionChanged;

    /// <summary>Сменился статус.</summary>
    public event Action? StatusChanged;

    /// <summary>Одна проверка. Вызывается по таймеру.</summary>
    public async Task PollAsync()
    {
        bool? sessionChange = null;
        await _gate.WaitAsync();
        try
        {
            var settings = options();
            var running = probe.IsRunning();

            if (!_polledOnce)
            {
                _polledOnce = true;
                // Прошлый запуск мог упасть посреди VR и не вернуть звук.
                if (!running && HasMemory) await RestoreAsync(settings);
            }

            if (running)
            {
                _missedPolls = 0;
                if (!SessionActive)
                {
                    SessionActive = true;
                    sessionChange = true;
                    _applyPending = settings.AutoSwitch;
                    if (!settings.AutoSwitch) SetStatus(Loc.T("SteamVR запущен, автопереключение выключено", "SteamVR is running, auto switching is off"));
                }
            }
            else if (SessionActive && ++_missedPolls >= settings.ExitGracePolls)
            {
                SessionActive = false;
                sessionChange = false;
                _applyPending = false;
                if (_applied) await RestoreAsync(settings);
                else SetStatus(Loc.T("Ждём SteamVR", "Waiting for SteamVR"));
            }

            if (_applyPending) await ApplyAsync(settings);
        }
        finally
        {
            _gate.Release();
        }

        if (sessionChange is { } active) SessionChanged?.Invoke(active);
    }

    /// <summary>Переключает на гарнитуру по команде пользователя.</summary>
    public async Task ApplyNowAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await ApplyAsync(options());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Возвращает обычный звук по команде пользователя.</summary>
    public async Task RestoreNowAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _applyPending = false;
            await RestoreAsync(options());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Пресет сменился: если звук уже на гарнитуре, его нужно перевести на новую.</summary>
    public void OnPresetChanged()
    {
        if (_applied) _applyPending = true;
    }

    /// <summary>Плагин останавливается: всё, что он поменял, возвращается обратно.</summary>
    public async Task ShutdownAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _applyPending = false;
            if (_applied) await RestoreAsync(options());
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Устройство вывода по умолчанию сейчас принадлежит гарнитуре.</summary>
    private bool _playbackOnHeadset;

    private bool HasMemory => memory.Playback is not null || memory.Recording is not null;

    private async Task ApplyAsync(VrAudioOptions settings)
    {
        try
        {
            var playback = VrAudioPreset.Find(await audio.ListAsync(AudioFlow.Playback), settings.Preset.Playback);
            var recording = VrAudioPreset.Find(await audio.ListAsync(AudioFlow.Recording), settings.Preset.Recording);
            if (playback is null || recording is null)
            {
                var missing = playback is null ? recording is null ? Loc.T("наушники и микрофон", "headphones and microphone") : Loc.T("наушники", "headphones") : Loc.T("микрофон", "microphone");
                SetStatus(Loc.T($"{settings.Preset.Name}: не найдены {missing}, ждём", $"{settings.Preset.Name}: {missing} not found, waiting"));
                _applyPending = true;
                return;
            }

            await RememberAsync(AudioFlow.Playback, playback.Id);
            await RememberAsync(AudioFlow.Recording, recording.Id);

            await audio.SetDefaultAsync(playback.Id);
            await audio.SetDefaultAsync(recording.Id);

            _applied = true;
            _playbackOnHeadset = true;
            _applyPending = false;
            SetStatus(Loc.T($"Звук на гарнитуре: {playback.Name}, {recording.Name}", $"Sound on the headset: {playback.Name}, {recording.Name}"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Устройство могло пропасть прямо сейчас. Следующая проверка попробует снова.
            _applyPending = true;
            SetStatus(Loc.T($"Не удалось переключить звук: {ex.Message}", $"Could not switch sound: {ex.Message}"));
        }
    }

    private async Task RememberAsync(AudioFlow flow, string vrDeviceId)
    {
        var current = await audio.GetDefaultAsync(flow);
        if (current is null || current == vrDeviceId) return;

        if (flow == AudioFlow.Playback) memory.Playback ??= current;
        else memory.Recording ??= current;
    }

    private async Task RestoreAsync(VrAudioOptions settings)
    {
        try
        {
            var playback = await TargetAsync(AudioFlow.Playback, settings.PcPlayback, memory.Playback, memory.DesktopPlayback, settings.Preset.Playback);
            var recording = await TargetAsync(AudioFlow.Recording, settings.PcRecording, memory.Recording, memory.DesktopRecording, settings.Preset.Recording);

            if (playback is not null)
            {
                await audio.SetDefaultAsync(playback.Id);
                _playbackOnHeadset = false;
            }

            if (recording is not null) await audio.SetDefaultAsync(recording.Id);

            SetStatus(playback is null && recording is null
                ? Loc.T("Некуда возвращать звук: укажи устройства в настройках", "Nowhere to return sound to: set devices in the settings")
                : Loc.T("Звук возвращён", "Sound returned"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus(Loc.T($"Не удалось вернуть звук: {ex.Message}", $"Could not return sound: {ex.Message}"));
        }
        finally
        {
            _applied = false;
            memory.Playback = null;
            memory.Recording = null;
        }
    }

    /// <summary>
    /// Куда вернуть звук. Настройка важнее запомненного: она явно выражает желание пользователя. Если звук уже был на
    /// гарнитуре и запоминать было нечего, берём последнее обычное устройство, а потом любое не из гарнитуры.
    /// </summary>
    private async Task<AudioDevice?> TargetAsync(AudioFlow flow, string configured, string? remembered, string? desktop, IReadOnlyList<string> headset)
    {
        var devices = await audio.ListAsync(flow);
        return VrAudioPreset.Find(devices, [configured])
               ?? devices.FirstOrDefault(device => device.Id == remembered)
               ?? devices.FirstOrDefault(device => device.Id == desktop)
               ?? devices.FirstOrDefault(device => VrAudioPreset.Find([device], headset) is null);
    }

    private void SetStatus(string status)
    {
        if (status == Status) return;
        Status = status;
        log(status);
        StatusChanged?.Invoke();
    }
}
