namespace QuantumHub.Plugin.MicSwitcher;

/// <summary>
/// Отличает голос от случайного звука на одном микрофоне. Сигнал считается устойчивым, если он держится нужное время и
/// занимает хотя бы половину этого времени: паузы в речи ему не мешают, а редкие щелчки ничего не накопят. Считается только
/// сигнал, которому предшествовала тишина, поэтому постоянный шум вроде гула никогда не сработает.
/// </summary>
public sealed class SignalTracker
{
    private DateTimeOffset? _start;
    private DateTimeOffset? _lastLoud;
    private DateTimeOffset? _previous;
    private TimeSpan _loudTime;
    private bool _heardSilence;
    private bool _afterSilence;

    /// <summary>Учитывает новое показание.</summary>
    /// <param name="now">Когда оно снято.</param>
    /// <param name="loud">Выше ли порога.</param>
    /// <param name="gap">Какая пауза ещё не обрывает сигнал.</param>
    public void Sample(DateTimeOffset now, bool loud, TimeSpan gap)
    {
        var step = _previous is { } previous ? now - previous : TimeSpan.Zero;
        _previous = now;

        if (loud)
        {
            _lastLoud = now;
            if (_start is null)
            {
                _start = now;
                _loudTime = TimeSpan.Zero;
                _afterSilence = _heardSilence;
            }
            else
            {
                _loudTime += step;
            }

            return;
        }

        if (_lastLoud is { } last && now - last <= gap) return;

        _start = null;
        _heardSilence = true;
    }

    /// <summary>Появился ли устойчивый сигнал.</summary>
    /// <param name="now">Текущее время.</param>
    /// <param name="hold">Сколько он должен держаться.</param>
    public bool IsSustained(DateTimeOffset now, TimeSpan hold) =>
        _afterSilence && _start is { } start && now - start >= hold && _loudTime >= hold / 2;

    /// <summary>Тихо ли на микрофоне всё это время.</summary>
    /// <param name="now">Текущее время.</param>
    /// <param name="span">Сколько должно быть тихо.</param>
    public bool IsQuiet(DateTimeOffset now, TimeSpan span) => _lastLoud is not { } last || now - last >= span;
}
