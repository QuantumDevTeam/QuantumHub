using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using SmtcSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SmtcStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace QuantumHub.Plugin.Music;

/// <summary>
/// Читает системные сведения о медиа (SMTC): то, что Windows показывает в регуляторе громкости. Читаются
/// все сессии, а не только «текущая»: текущей система назначает кого угодно, например браузер.
/// </summary>
public sealed class SmtcMediaSource : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<SmtcSession> _hooked = [];
    private SessionManager? _manager;

    /// <summary>Что-то изменилось: стоит прочитать заново.</summary>
    public event Action? Changed;

    /// <summary>Подключается к системе.</summary>
    public async Task StartAsync()
    {
        _manager = await SessionManager.RequestAsync();
        _manager.SessionsChanged += OnSessionsChanged;
        Rehook();
    }

    private void OnSessionsChanged(SessionManager sender, SessionsChangedEventArgs args)
    {
        Rehook();
        Changed?.Invoke();
    }

    private void Rehook()
    {
        lock (_gate)
        {
            Unhook();
            if (_manager is null) return;

            foreach (var session in _manager.GetSessions())
            {
                session.PlaybackInfoChanged += OnPlaybackChanged;
                session.MediaPropertiesChanged += OnPropertiesChanged;
                _hooked.Add(session);
            }
        }
    }

    private void Unhook()
    {
        foreach (var session in _hooked)
        {
            session.PlaybackInfoChanged -= OnPlaybackChanged;
            session.MediaPropertiesChanged -= OnPropertiesChanged;
        }

        _hooked.Clear();
    }

    private void OnPlaybackChanged(SmtcSession sender, PlaybackInfoChangedEventArgs args) => Changed?.Invoke();

    private void OnPropertiesChanged(SmtcSession sender, MediaPropertiesChangedEventArgs args) => Changed?.Invoke();

    /// <summary>Читает все сессии.</summary>
    public async Task<IReadOnlyList<MediaSession>> ReadAsync()
    {
        var result = new List<MediaSession>();
        if (_manager is null) return result;

        foreach (var session in _manager.GetSessions())
        {
            try
            {
                var properties = await session.TryGetMediaPropertiesAsync();
                var timeline = session.GetTimelineProperties();
                result.Add(new MediaSession(
                    session.SourceAppUserModelId,
                    properties?.Title ?? string.Empty,
                    properties?.Artist ?? string.Empty,
                    properties?.AlbumTitle ?? string.Empty,
                    ToState(session.GetPlaybackInfo()?.PlaybackStatus),
                    timeline.Position.TotalSeconds,
                    timeline.EndTime.TotalSeconds,
                    timeline.LastUpdatedTime));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // Приложение могло закрыться прямо сейчас. Следующая проверка его просто не увидит.
            }
        }

        return result;
    }

    private static PlaybackState ToState(SmtcStatus? status) => status switch
    {
        SmtcStatus.Playing => PlaybackState.Playing,
        SmtcStatus.Paused => PlaybackState.Paused,
        _ => PlaybackState.Stopped,
    };

    /// <summary>Читает обложку текущего трека приложения.</summary>
    /// <param name="appId">Id приложения.</param>
    /// <returns>Файл картинки или <see langword="null"/>.</returns>
    public async Task<byte[]?> ReadThumbnailAsync(string appId)
    {
        var session = _manager?.GetSessions().FirstOrDefault(candidate => candidate.SourceAppUserModelId == appId);
        if (session is null) return null;

        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties?.Thumbnail is not { } thumbnail) return null;

            using var stream = await thumbnail.OpenReadAsync();
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Отключается от системы.</summary>
    public void Dispose()
    {
        lock (_gate) Unhook();
        if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
        _manager = null;
    }
}
