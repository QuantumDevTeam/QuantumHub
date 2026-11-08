using QuantumHub.Contracts.Ui;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace QuantumHub.Gui.Services;

/// <summary>
/// Размер и место окна. Размер хранится в единицах, не зависящих от масштаба экрана: AppWindow считает в настоящих
/// пикселях, и без этого на 4K-мониторе окно получается крошечным.
/// </summary>
/// <param name="X">Левый край в пикселях экрана.</param>
/// <param name="Y">Верхний край в пикселях экрана.</param>
/// <param name="Width">Ширина при масштабе 100%.</param>
/// <param name="Height">Высота при масштабе 100%.</param>
/// <param name="Maximized">Развёрнуто ли окно.</param>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized)
{
    /// <summary>Наименьшая ширина при масштабе 100%.</summary>
    public const double MinWidth = 720;
    /// <summary>Наименьшая высота при масштабе 100%.</summary>
    public const double MinHeight = 500;
    /// <summary>Ширина окна при первом запуске, если экран небольшой.</summary>
    private const double DefaultWidth = 1180;
    /// <summary>Высота окна при первом запуске, если экран небольшой.</summary>
    private const double DefaultHeight = 780;
    /// <summary>Какую долю большого экрана занять при первом запуске.</summary>
    private const double DefaultScreenShare = 0.6;
    /// <summary>Какая часть окна должна быть видна, чтобы его не пришлось искать за краем экрана.</summary>
    private const int VisibleMargin = 80;

    /// <summary>Файл с размером окна.</summary>
    /// <param name="instance">Экземпляр хаба.</param>
    private static string FilePath(string instance) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuantumHub", $"window.{instance}.json");

    /// <summary>Читает сохранённое положение.</summary>
    /// <param name="instance">Экземпляр хаба.</param>
    /// <returns>Положение или <see langword="null"/>, если его нет или файл испорчен.</returns>
    public static WindowPlacement? Load(string instance)
    {
        try
        {
            return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath(instance)));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Сохраняет положение.</summary>
    /// <param name="instance">Экземпляр хаба.</param>
    public void Save(string instance)
    {
        try
        {
            var path = FilePath(instance);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(Loc.T($"Не удалось сохранить размер окна: {ex.Message}", $"Could not save the window size: {ex.Message}"));
        }
    }

    /// <summary>Считает прямоугольник окна. Отдельно от WinUI, поэтому логика понятна и без монитора.</summary>
    /// <param name="saved">Сохранённое положение или <see langword="null"/> при первом запуске.</param>
    /// <param name="area">Рабочая область экрана.</param>
    /// <param name="scale">Масштаб экрана: 1 это 100%.</param>
    /// <returns>Прямоугольник в пикселях экрана.</returns>
    public static RectInt32 Fit(WindowPlacement? saved, RectInt32 area, double scale)
    {
        var width = saved is null ? Math.Max(DefaultWidth * scale, area.Width * DefaultScreenShare) : saved.Width * scale;
        var height = saved is null ? Math.Max(DefaultHeight * scale, area.Height * DefaultScreenShare) : saved.Height * scale;

        var w = (int)Math.Min(Math.Max(width, MinWidth * scale), area.Width);
        var h = (int)Math.Min(Math.Max(height, MinHeight * scale), area.Height);

        var x = saved?.X ?? area.X + (area.Width - w) / 2;
        var y = saved?.Y ?? area.Y + (area.Height - h) / 2;

        // Монитор могли отключить: возвращаем окно на видимое место.
        x = Math.Clamp(x, area.X - w + VisibleMargin, area.X + area.Width - VisibleMargin);
        y = Math.Clamp(y, area.Y, area.Y + area.Height - VisibleMargin);
        return new RectInt32(x, y, w, h);
    }

    /// <summary>Ставит окно на место.</summary>
    /// <param name="window">Окно.</param>
    /// <param name="scale">Масштаб экрана.</param>
    /// <param name="instance">Экземпляр хаба.</param>
    /// <param name="remember">Использовать ли сохранённое положение.</param>
    public static void Apply(AppWindow window, double scale, string instance, bool remember)
    {
        var saved = remember ? Load(instance) : null;
        var area = saved is null
            ? DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary)
            : DisplayArea.GetFromPoint(new PointInt32(saved.X, saved.Y), DisplayAreaFallback.Nearest);

        window.MoveAndResize(Fit(saved, area.WorkArea, scale));
        if (window.Presenter is not OverlappedPresenter presenter) return;

        presenter.PreferredMinimumWidth = (int)(MinWidth * scale);
        presenter.PreferredMinimumHeight = (int)(MinHeight * scale);
        if (saved?.Maximized == true) presenter.Maximize();
    }

    /// <summary>Запоминает текущее положение окна.</summary>
    /// <param name="window">Окно.</param>
    /// <param name="scale">Масштаб экрана.</param>
    /// <param name="instance">Экземпляр хаба.</param>
    public static void Capture(AppWindow window, double scale, string instance)
    {
        var state = (window.Presenter as OverlappedPresenter)?.State;
        if (state == OverlappedPresenterState.Minimized) return;

        // Размер развёрнутого окна запоминать незачем: иначе обычный размер потеряется.
        if (state == OverlappedPresenterState.Maximized)
        {
            (Load(instance) ?? new WindowPlacement(window.Position.X, window.Position.Y, DefaultWidth, DefaultHeight, true))
                .WithMaximized().Save(instance);
            return;
        }

        new WindowPlacement(window.Position.X, window.Position.Y, window.Size.Width / scale, window.Size.Height / scale, false).Save(instance);
    }

    /// <summary>Забывает положение окна.</summary>
    /// <param name="instance">Экземпляр хаба.</param>
    public static void Forget(string instance)
    {
        try
        {
            File.Delete(FilePath(instance));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log(Loc.T($"Не удалось забыть размер окна: {ex.Message}", $"Could not forget the window size: {ex.Message}"));
        }
    }

    /// <summary>То же положение, но развёрнутое.</summary>
    private WindowPlacement WithMaximized() => this with { Maximized = true };
}
