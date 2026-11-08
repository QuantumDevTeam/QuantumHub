using System.Net.Http;
using System.Text.Json;

namespace QuantumHub.Gui.Services;

/// <summary>
/// Спрашивает у GitHub, вышла ли новая версия. Отправляется только обычный запрос к странице релизов проекта, никаких данных о компьютере.
/// Файл не зависит от WinUI, поэтому проверяется тестами.
/// </summary>
public static class UpdateChecker
{
    /// <summary>Страница проекта. Открываем только ссылки отсюда: ответ приходит по сети.</summary>
    public const string ProjectUrl = "https://github.com/QuantumDevTeam/QuantumHub";
    /// <summary>Адрес последнего релиза.</summary>
    private const string LatestUrl = "https://api.github.com/repos/QuantumDevTeam/QuantumHub/releases/latest";

    /// <summary>Сколько ждать ответ.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Вышедшая версия.</summary>
    /// <param name="Version">Номер версии.</param>
    /// <param name="Url">Страница релиза.</param>
    public sealed record Release(string Version, string Url);

    /// <summary>Новая версия, если проверка её нашла.</summary>
    public static Release? Available { get; private set; }

    /// <summary>Спрашивает GitHub. Без сети просто ничего не находит.</summary>
    /// <param name="current">Текущая версия.</param>
    /// <returns>Новая версия или <see langword="null"/>.</returns>
    public static async Task<Release?> CheckAsync(string current)
    {
        try
        {
            using var http = new HttpClient { Timeout = RequestTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("QuantumHub");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            Available = Parse(await http.GetStringAsync(LatestUrl), current);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Проверка обновлений не повод показывать ошибку.
        }

        return Available;
    }

    /// <summary>Разбирает ответ GitHub.</summary>
    /// <param name="json">Ответ.</param>
    /// <param name="current">Текущая версия.</param>
    /// <returns>Новая версия или <see langword="null"/>.</returns>
    public static Release? Parse(string json, string current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var preview) && preview.GetBoolean()) return null;
        if (!root.TryGetProperty("tag_name", out var tag) || !root.TryGetProperty("html_url", out var page)) return null;

        var version = tag.GetString()?.TrimStart('v') ?? string.Empty;
        var url = page.GetString() ?? string.Empty;
        if (!url.StartsWith(ProjectUrl + "/", StringComparison.Ordinal)) return null;
        return IsNewer(version, current) ? new Release(version, url) : null;
    }

    /// <summary>Новее ли версия.</summary>
    /// <param name="candidate">Что вышло.</param>
    /// <param name="current">Что стоит.</param>
    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var next) && Version.TryParse(current, out var now) && next > now;
}
