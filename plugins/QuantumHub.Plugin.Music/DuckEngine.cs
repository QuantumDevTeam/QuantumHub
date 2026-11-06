namespace QuantumHub.Plugin.Music;

/// <summary>Приложение со звуком.</summary>
/// <param name="Process">Имя процесса.</param>
/// <param name="Volume">Громкость от 0 до 1.</param>
/// <param name="Peak">Текущий уровень звука от 0 до 1.</param>
public sealed record AudioSession(string Process, double Volume, double Peak);

/// <summary>Что движку нужно от звука. В работе за этим стоит возможность audio.devices.</summary>
public interface IAudioSessions
{
    /// <summary>Приложения, в которых сейчас идёт звук.</summary>
    Task<IReadOnlyList<AudioSession>> ListAsync();

    /// <summary>Меняет громкость приложения.</summary>
    /// <param name="process">Имя процесса.</param>
    /// <param name="volume">Громкость от 0 до 1.</param>
    /// <param name="fadeMs">За сколько плавно к ней прийти.</param>
    Task SetVolumeAsync(string process, double volume, int fadeMs);
}

/// <summary>
/// Что было до приглушения. Хранится на диске: если хаб упадёт, музыка не останется тихой навсегда.
/// </summary>
public interface IDuckMemory
{
    /// <summary>Какое приложение приглушено.</summary>
    string? Process { get; set; }

    /// <summary>Какая была громкость.</summary>
    double? Volume { get; set; }
}

/// <summary>Настройки приглушения.</summary>
/// <param name="Enabled">Включено ли оно.</param>
/// <param name="UseBlacklist">Приглушают все, кроме <paramref name="Ignored"/>. Иначе только <paramref name="Apps"/>.</param>
/// <param name="Apps">Белый список.</param>
/// <param name="Ignored">Чёрный список.</param>
/// <param name="MusicApps">Музыкальные приложения. Сами себя они не приглушают.</param>
/// <param name="Volume">Громкость при приглушении.</param>
/// <param name="FadeMs">Плавность.</param>
/// <param name="PeakThreshold">Какой уровень считать звуком. Открытая сессия без сигнала не считается.</param>
/// <param name="AttackTicks">Сколько проверок подряд должен идти звук. Отсекает короткие системные щелчки.</param>
/// <param name="ReleaseTicks">Сколько проверок подряд должно быть тихо. Паузы в речи не дёргают громкость.</param>
public sealed record DuckOptions(
    bool Enabled,
    bool UseBlacklist,
    IReadOnlyList<string> Apps,
    IReadOnlyList<string> Ignored,
    IReadOnlyList<string> MusicApps,
    double Volume,
    int FadeMs,
    double PeakThreshold = 0.01,
    int AttackTicks = 2,
    int ReleaseTicks = 6);

/// <summary>
/// Делает музыку тише, пока звучит другое приложение. Смотрит на настоящий уровень сигнала, а не на то,
/// что сессия открыта: Discord или браузер держат её всегда.
/// </summary>
/// <param name="audio">Приложения со звуком.</param>
/// <param name="memory">Хранилище прежней громкости.</param>
/// <param name="options">Текущие настройки.</param>
public sealed class DuckEngine(IAudioSessions audio, IDuckMemory memory, Func<DuckOptions> options)
{
    private int _loudTicks;
    private int _quietTicks;

    /// <summary>Приглушена ли музыка сейчас.</summary>
    public bool IsDucked => memory.Process is not null;

    /// <summary>Кто сейчас приглушает, для показа.</summary>
    public string? Trigger { get; private set; }

    /// <summary>Одна проверка.</summary>
    /// <param name="musicPlaying">Играет ли музыка. Приглушать тишину незачем.</param>
    public async Task TickAsync(bool musicPlaying)
    {
        var settings = options();
        var sessions = await audio.ListAsync();

        Trigger = settings.Enabled && musicPlaying
            ? sessions.Where(session => IsTrigger(session, settings)).OrderByDescending(session => session.Peak).FirstOrDefault()?.Process
            : null;

        if (Trigger is not null)
        {
            _quietTicks = 0;
            _loudTicks++;
        }
        else
        {
            _loudTicks = 0;
            _quietTicks++;
        }

        if (IsDucked)
        {
            if (!settings.Enabled || _quietTicks >= settings.ReleaseTicks) await ReleaseAsync();
            return;
        }

        if (_loudTicks < settings.AttackTicks) return;

        var music = sessions.FirstOrDefault(session => AppNames.MatchesAny(session.Process, settings.MusicApps));
        if (music is null || music.Volume <= settings.Volume) return;

        memory.Volume = music.Volume;
        memory.Process = music.Process;
        await audio.SetVolumeAsync(music.Process, settings.Volume, settings.FadeMs);
    }

    /// <summary>Возвращает прежнюю громкость, если музыка приглушена.</summary>
    public async Task ReleaseAsync()
    {
        if (memory.Process is not { } process) return;

        var volume = memory.Volume ?? 1;
        memory.Process = null;
        memory.Volume = null;
        await audio.SetVolumeAsync(process, volume, options().FadeMs);
    }

    private static bool IsTrigger(AudioSession session, DuckOptions settings)
    {
        if (session.Peak < settings.PeakThreshold || AppNames.MatchesAny(session.Process, settings.MusicApps)) return false;

        return settings.UseBlacklist
            ? !AppNames.MatchesAny(session.Process, settings.Ignored)
            : AppNames.MatchesAny(session.Process, settings.Apps);
    }
}
