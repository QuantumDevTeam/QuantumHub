namespace QuantumHub.Plugin.Music;

/// <summary>Состояние воспроизведения.</summary>
public enum PlaybackState
{
    /// <summary>Остановлено или неизвестно.</summary>
    Stopped,

    /// <summary>На паузе.</summary>
    Paused,

    /// <summary>Играет.</summary>
    Playing,
}

/// <summary>Что приложение сообщает о себе системе.</summary>
/// <param name="AppId">Id приложения в системе, например "com.github.th-ch.youtube-music".</param>
/// <param name="Title">Название трека.</param>
/// <param name="Artist">Исполнитель.</param>
/// <param name="Album">Альбом.</param>
/// <param name="State">Состояние.</param>
/// <param name="PositionSec">Позиция на момент <paramref name="UpdatedAt"/>.</param>
/// <param name="DurationSec">Длительность.</param>
/// <param name="UpdatedAt">Когда приложение сообщило позицию.</param>
public sealed record MediaSession(
    string AppId,
    string Title,
    string Artist,
    string Album,
    PlaybackState State,
    double PositionSec,
    double DurationSec,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Позиция сейчас. Приложения сообщают её редко, поэтому пока играет, она досчитывается.</summary>
    /// <param name="now">Текущее время.</param>
    public double PositionAt(DateTimeOffset now)
    {
        var position = State == PlaybackState.Playing ? PositionSec + (now - UpdatedAt).TotalSeconds : PositionSec;
        return DurationSec > 0 ? Math.Clamp(position, 0, DurationSec) : Math.Max(position, 0);
    }
}

/// <summary>Что произошло с музыкой. Для всего, кроме None и Stopped, показывается уведомление.</summary>
public enum MusicChange
{
    /// <summary>Ничего важного.</summary>
    None,

    /// <summary>Музыка заиграла.</summary>
    Started,

    /// <summary>Сменился трек.</summary>
    TrackChanged,

    /// <summary>Пауза.</summary>
    Paused,

    /// <summary>Продолжение после паузы.</summary>
    Resumed,

    /// <summary>Плеер пропал.</summary>
    Stopped,
}

/// <summary>Сравнение имён приложений. Разделители в разных источниках разные, поэтому они не учитываются.</summary>
public static class AppNames
{
    /// <summary>
    /// Подходит ли имя под шаблон. "YouTube Music" находит и процесс "YouTube Music",
    /// и приложение "com.github.th-ch.youtube-music".
    /// </summary>
    /// <param name="name">Имя процесса или приложения.</param>
    /// <param name="pattern">Шаблон из настроек.</param>
    public static bool Matches(string name, string pattern)
    {
        var wanted = Normalize(pattern);
        return wanted.Length > 0 && Normalize(name).Contains(wanted, StringComparison.Ordinal);
    }

    /// <summary>Подходит ли имя под любой из шаблонов.</summary>
    /// <param name="name">Имя.</param>
    /// <param name="patterns">Шаблоны.</param>
    public static bool MatchesAny(string name, IEnumerable<string> patterns) => patterns.Any(pattern => Matches(name, pattern));

    private static string Normalize(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}

/// <summary>
/// Определяет, что считается событием для пользователя. Смотрит только на приложения из настроек, поэтому
/// браузер или видео в другой программе не могут перехватить трек.
/// </summary>
public sealed class MusicTracker
{
    /// <summary>Пауза в самом конце трека: это смена трека, а не пользователь нажал на паузу.</summary>
    private const double EndOfTrackSec = 1.5;

    /// <summary>Текущий трек или <see langword="null"/>, если плеера нет.</summary>
    public MediaSession? Current { get; private set; }

    /// <summary>Обновляет состояние.</summary>
    /// <param name="sessions">Все приложения, сообщившие о себе системе.</param>
    /// <param name="apps">Музыкальные приложения, важнее первые.</param>
    /// <param name="now">Текущее время.</param>
    /// <returns>Что произошло.</returns>
    public MusicChange Update(IReadOnlyList<MediaSession> sessions, IReadOnlyList<string> apps, DateTimeOffset now)
    {
        var next = Pick(sessions, apps);
        var previous = Current;

        if (next is null)
        {
            Current = null;
            return previous is null ? MusicChange.None : MusicChange.Stopped;
        }

        // Между треками плеер ненадолго сообщает пустое название. Это не событие.
        if (next.Title.Length == 0) return MusicChange.None;

        Current = next;
        if (previous is null) return next.State == PlaybackState.Playing ? MusicChange.Started : MusicChange.None;

        if (next.Title != previous.Title || next.Artist != previous.Artist)
            return next.State == PlaybackState.Playing ? MusicChange.TrackChanged : MusicChange.None;

        if (previous.State == PlaybackState.Playing && next.State == PlaybackState.Paused)
        {
            var left = next.DurationSec - next.PositionAt(now);
            return next.DurationSec > 0 && left < EndOfTrackSec ? MusicChange.None : MusicChange.Paused;
        }

        return previous.State != PlaybackState.Playing && next.State == PlaybackState.Playing
            ? MusicChange.Resumed
            : MusicChange.None;
    }

    /// <summary>Играющее приложение важнее приостановленного, потом решает порядок в настройке.</summary>
    private static MediaSession? Pick(IReadOnlyList<MediaSession> sessions, IReadOnlyList<string> apps)
    {
        MediaSession? best = null;
        foreach (var app in apps)
        {
            foreach (var session in sessions)
            {
                if (!AppNames.Matches(session.AppId, app)) continue;
                if (session.State == PlaybackState.Playing) return session;
                best ??= session;
            }
        }

        return best;
    }
}
