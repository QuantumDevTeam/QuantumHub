using System.IO;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.VrDashboard;

/// <summary>Что произошло в SteamVR.</summary>
public enum OverlayInputKind
{
    /// <summary>Луч контроллера сдвинулся.</summary>
    Move,
    /// <summary>Кнопку контроллера отпустили.</summary>
    Press,
    /// <summary>Пользователь закончил ввод на клавиатуре.</summary>
    KeyboardDone,
    /// <summary>Клавиатуру закрыли.</summary>
    KeyboardClosed,
    /// <summary>SteamVR закрывается.</summary>
    Quit,
}

/// <summary>Событие SteamVR.</summary>
/// <param name="Kind">Что произошло.</param>
/// <param name="X">Координата X в пикселях панели.</param>
/// <param name="Y">Координата Y в пикселях панели.</param>
public readonly record struct OverlayInput(OverlayInputKind Kind, double X = 0, double Y = 0);

/// <summary>Всё, что панели нужно от SteamVR. Отдельно, чтобы логику панели можно было проверить без шлема.</summary>
public interface IOverlayRuntime
{
    /// <summary>Подключается к SteamVR и создаёт вкладку.</summary>
    /// <param name="icon">Значок вкладки.</param>
    /// <returns>Текст ошибки или <see langword="null"/>.</returns>
    string? Start(string icon);

    /// <summary>Отключается от SteamVR.</summary>
    void Stop();

    /// <summary>Открыта ли вкладка.</summary>
    bool IsVisible { get; }

    /// <summary>Забирает следующее событие.</summary>
    /// <param name="input">Событие.</param>
    /// <returns>Было ли событие.</returns>
    bool TryPoll(out OverlayInput input);

    /// <summary>Показывает картинку из пикселей RGBA.</summary>
    /// <param name="rgba">Пиксели.</param>
    /// <returns>Текст ошибки или <see langword="null"/>.</returns>
    string? ShowPixels(byte[] rgba);

    /// <summary>Показывает картинку из файла.</summary>
    /// <param name="path">Путь к PNG.</param>
    /// <returns>Текст ошибки или <see langword="null"/>.</returns>
    string? ShowFile(string path);

    /// <summary>Открывает клавиатуру SteamVR.</summary>
    /// <param name="description">Подпись.</param>
    /// <param name="text">Начальный текст.</param>
    /// <returns>Текст ошибки или <see langword="null"/>.</returns>
    string? OpenKeyboard(string description, string text);

    /// <summary>Что ввёл пользователь.</summary>
    string KeyboardText();
}

/// <summary>Рисует панель.</summary>
public interface IPanelPainter
{
    /// <summary>Рисует в пиксели RGBA.</summary>
    /// <param name="view">Раскладка.</param>
    /// <param name="state">Данные.</param>
    byte[] Rgba(PanelView view, PanelState state);

    /// <summary>Рисует в файл PNG.</summary>
    /// <param name="view">Раскладка.</param>
    /// <param name="state">Данные.</param>
    /// <param name="path">Куда.</param>
    void Png(PanelView view, PanelState state, string path);
}

/// <summary>Логика вкладки в меню SteamVR: что показать и что сделать по нажатию.</summary>
/// <param name="runtime">SteamVR.</param>
/// <param name="painter">Рисование.</param>
/// <param name="client">Связь с плагинами.</param>
/// <param name="log">Журнал.</param>
public sealed class OverlayHost(IOverlayRuntime runtime, IPanelPainter painter, PanelClient client, Action<string> log)
{
    private const string IconFile = "logo.png";
    /// <summary>Шаг цикла: в нём читаются нажатия.</summary>
    private static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(20);
    /// <summary>Как часто обновлять данные, пока панель открыта.</summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(1.5);

    private readonly Lock _gate = new();
    private PanelOptions _options = new();
    private PanelState _state = new();
    private PanelView _view = new([], null);
    private bool _dirty = true;
    /// <summary>Что сейчас показано. Замена картинки в SteamVR заметна глазу, поэтому такая же картинка не отправляется.</summary>
    private byte[]? _shown;
    /// <summary>SteamVR отказался принимать пиксели, и картинка идёт через файл.</summary>
    private bool _useFile;
    /// <summary>Файлов два: SteamVR читает файл с задержкой и не должен застать его наполовину записанным.</summary>
    private int _fileIndex;
    /// <summary>Для чего сейчас открыта клавиатура.</summary>
    private string? _keyboardFor;
    private int _refreshing;
    private DateTime _nextRefresh = DateTime.MinValue;

    /// <summary>Подключается к SteamVR и работает, пока тот не закроется.</summary>
    /// <returns>0 при обычном завершении.</returns>
    public int Run()
    {
        if (runtime.Start(Path.Combine(AppContext.BaseDirectory, IconFile)) is { } error)
        {
            log(error);
            return 1;
        }

        log(Loc.T("Панель добавлена в меню SteamVR.", "Panel is in the SteamVR menu."));
        try
        {
            // Данные нужны сразу: иначе при первом открытии вкладка будет пустой.
            RefreshAsync().GetAwaiter().GetResult();
            while (Step()) Thread.Sleep(StepDelay);
            return 0;
        }
        finally
        {
            runtime.Stop();
        }
    }

    /// <summary>Один шаг цикла.</summary>
    /// <returns><see langword="false"/>, если SteamVR закрывается.</returns>
    public bool Step()
    {
        while (runtime.TryPoll(out var input))
        {
            if (input.Kind == OverlayInputKind.Quit) return false;
            Handle(input);
        }

        if (runtime.IsVisible && DateTime.UtcNow >= _nextRefresh)
        {
            _nextRefresh = DateTime.UtcNow + RefreshEvery;
            RefreshInBackground();
        }

        if (Volatile.Read(ref _dirty)) Present();
        return true;
    }

    /// <summary>Читает данные плагинов.</summary>
    public async Task RefreshAsync()
    {
        var options = await client.ReadOptionsAsync();
        var state = await client.ReadAsync(options);
        lock (_gate)
        {
            if (Same(state, _state) && options == _options) return;
            _options = options;
            _state = state;
        }

        Volatile.Write(ref _dirty, true);
    }

    /// <summary>Одинаковы ли данные. Список статусов записи сравнивают по ссылке, поэтому он сравнивается отдельно.</summary>
    private static bool Same(PanelState a, PanelState b) =>
        (a with { Statuses = null }) == (b with { Statuses = null }) && (a.Statuses ?? []).SequenceEqual(b.Statuses ?? []);

    /// <summary>Обновляет данные, не задерживая цикл: иначе панель перестанет отзываться на контроллер.</summary>
    private void RefreshInBackground()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync();
            }
            catch (Exception ex) when (ex is IOException or HubCallException or InvalidOperationException or OperationCanceledException)
            {
                log(Loc.T($"Обновление не удалось: {ex.Message}", $"Refresh failed: {ex.Message}"));
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }

    /// <summary>Отдаёт картинку в SteamVR.</summary>
    private void Present()
    {
        Volatile.Write(ref _dirty, false);

        PanelState state;
        PanelOptions options;
        lock (_gate)
        {
            state = _state;
            options = _options;
        }

        _view = PanelLayout.Build(state, options);

        var pixels = painter.Rgba(_view, state);
        if (_shown is not null && pixels.AsSpan().SequenceEqual(_shown)) return;
        _shown = pixels;

        if (!_useFile)
        {
            var error = runtime.ShowPixels(pixels);
            if (error is null) return;

            // Размер данных для этого вызова ограничен, а файл принимается всегда.
            log(Loc.T($"SetOverlayRaw не сработал: {error}. Перехожу на файл.", $"SetOverlayRaw failed: {error}. Falling back to a file."));
            _useFile = true;
        }

        _fileIndex ^= 1;
        var path = Path.Combine(Path.GetTempPath(), $"QuantumHub.VrDashboard.{Environment.ProcessId}.{_fileIndex}.png");
        painter.Png(_view, state, path);
        if (runtime.ShowFile(path) is { } fileError) log(Loc.T($"SetOverlayFromFile не сработал: {fileError}", $"SetOverlayFromFile failed: {fileError}"));
    }

    /// <summary>Разбирает событие панели.</summary>
    private void Handle(OverlayInput input)
    {
        switch (input.Kind)
        {
            case OverlayInputKind.Press:
                var tile = TileAt(input);
                log($"Press at {input.X:0},{input.Y:0}: {tile?.Id ?? "nothing"}");
                if (tile is not null) Press(tile);
                break;

            case OverlayInputKind.KeyboardDone:
                OnKeyboardDone();
                break;

            case OverlayInputKind.KeyboardClosed:
                _keyboardFor = null;
                break;
        }
    }

    /// <summary>Кнопка под лучом. SteamVR считает координаты от нижнего края панели, а раскладка от верхнего.</summary>
    private PanelTile? TileAt(OverlayInput input) =>
        PanelLayout.HitTest(_view, input.X, _options.FlipPointer ? PanelLayout.Height - input.Y : input.Y);

    /// <summary>Нажатие на кнопку.</summary>
    private void Press(PanelTile tile)
    {
        PanelState state;
        lock (_gate) state = _state;

        if (tile.Id == PanelLayout.EditStatus)
        {
            OpenKeyboard(tile.Id, Loc.T("Личный статус", "Personal status"), state.Status);
            return;
        }

        if (tile.Id == PanelLayout.SendChat)
        {
            OpenKeyboard(tile.Id, Loc.T("Сообщение", "Message"), string.Empty);
            return;
        }

        Run(() => client.PressAsync(tile.Id, state));
    }

    /// <summary>Открывает клавиатуру SteamVR.</summary>
    private void OpenKeyboard(string purpose, string description, string text)
    {
        if (runtime.OpenKeyboard(description, text) is { } error) log(Loc.T($"Не удалось открыть клавиатуру: {error}", $"Could not open the keyboard: {error}"));
        else _keyboardFor = purpose;
    }

    /// <summary>Пользователь закончил ввод.</summary>
    private void OnKeyboardDone()
    {
        var text = runtime.KeyboardText();
        var purpose = _keyboardFor;
        _keyboardFor = null;

        if (purpose == PanelLayout.EditStatus) Run(() => client.SetStatusAsync(text));
        else if (purpose == PanelLayout.SendChat && text.Length > 0) Run(() => client.SendChatAsync(text));
    }

    /// <summary>Выполняет действие в фоне и сразу обновляет панель.</summary>
    private void Run(Func<Task> action) => _ = Task.Run(async () =>
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or HubCallException or InvalidOperationException or OperationCanceledException)
        {
            log(Loc.T($"Действие не удалось: {ex.Message}", $"Action failed: {ex.Message}"));
        }

        RefreshInBackground();
    });
}
