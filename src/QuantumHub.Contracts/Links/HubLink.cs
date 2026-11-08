using System.Text;
using System.Text.RegularExpressions;

namespace QuantumHub.Contracts.Links;

/// <summary>Что делает ссылка quantumhub://.</summary>
public enum HubLinkKind
{
    /// <summary>Показать окно: <c>quantumhub://open</c>.</summary>
    Open,
    /// <summary>Открыть страницу: <c>quantumhub://settings</c>.</summary>
    Page,
    /// <summary>Открыть страницу плагина: <c>quantumhub://plugin/qh.discord</c>.</summary>
    Plugin,
    /// <summary>Запустить действие плагина: <c>quantumhub://run/qh.discord/mute?state=on</c>.</summary>
    Run,
}

/// <summary>Разобранная ссылка quantumhub://. Текст приходит откуда угодно, поэтому разбор строгий: всё незнакомое отвергается.</summary>
/// <param name="Kind">Что делает ссылка.</param>
/// <param name="Page">Страница для <see cref="HubLinkKind.Page"/>.</param>
/// <param name="PluginId">Плагин.</param>
/// <param name="Action">Действие для <see cref="HubLinkKind.Run"/>.</param>
/// <param name="Params">Параметры для <see cref="HubLinkKind.Run"/>.</param>
public sealed partial record HubLink(HubLinkKind Kind, string? Page, string? PluginId, string? Action, IReadOnlyDictionary<string, string> Params)
{
    /// <summary>Схема ссылок.</summary>
    public const string Scheme = "quantumhub";
    /// <summary>Метод, в котором плагин получает <c>LinkRunRequest</c>.</summary>
    public const string RunMethod = "link.run";

    /// <summary>Самая длинная ссылка. Настоящие ссылки короткие.</summary>
    private const int MaxLength = 2048;
    /// <summary>Наибольшее число параметров.</summary>
    private const int MaxParams = 32;

    /// <summary>Страницы, которые можно открыть ссылкой.</summary>
    private static readonly string[] Pages = ["overview", "plugins", "sessions", "logs", "settings"];

    private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();

    /// <summary>То же правило, что у id плагинов в манифесте.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex Name();

    /// <summary>Похож ли текст на ссылку хаба.</summary>
    /// <param name="text">Текст.</param>
    public static bool HasScheme(string text) => text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>Разбирает ссылку.</summary>
    /// <param name="text">Текст ссылки.</param>
    /// <param name="link">Результат.</param>
    /// <returns><see langword="true"/>, если это правильная ссылка.</returns>
    public static bool TryParse(string? text, out HubLink link)
    {
        link = new HubLink(HubLinkKind.Open, null, null, null, NoParams);
        if (text is null || text.Length > MaxLength || !HasScheme(text)) return false;

        var rest = text[(Scheme.Length + 1)..];
        if (rest.StartsWith("//", StringComparison.Ordinal)) rest = rest[2..];

        var fragment = rest.IndexOf('#');
        if (fragment >= 0) rest = rest[..fragment];

        var query = string.Empty;
        var mark = rest.IndexOf('?');
        if (mark >= 0)
        {
            query = rest[(mark + 1)..];
            rest = rest[..mark];
        }

        // Браузеры дописывают к адресу косую черту.
        var path = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path.Length == 0) return true;

        var head = path[0].ToLowerInvariant();
        if (head == "open") return path.Length == 1;

        if (path.Length == 1 && Pages.Contains(head))
        {
            link = link with { Kind = HubLinkKind.Page, Page = head };
            return true;
        }

        if (head == "plugin" && path.Length == 2 && Name().IsMatch(path[1]))
        {
            link = link with { Kind = HubLinkKind.Plugin, PluginId = path[1] };
            return true;
        }

        if (head == "run" && path.Length == 3 && Name().IsMatch(path[1]) && Name().IsMatch(path[2]) && TryParseQuery(query, out var parameters))
        {
            link = link with { Kind = HubLinkKind.Run, PluginId = path[1], Action = path[2], Params = parameters };
            return true;
        }

        return false;
    }

    /// <summary>Собирает ссылку на действие плагина.</summary>
    /// <param name="pluginId">Плагин.</param>
    /// <param name="action">Действие.</param>
    /// <param name="parameters">Параметры.</param>
    /// <returns>Текст ссылки.</returns>
    public static string Run(string pluginId, string action, IReadOnlyDictionary<string, string>? parameters = null)
    {
        var text = new StringBuilder($"{Scheme}://run/{pluginId}/{action}");
        var separator = '?';
        foreach (var (key, value) in parameters ?? NoParams)
        {
            text.Append(separator).Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return text.ToString();
    }

    /// <summary>Разбирает параметры.</summary>
    private static bool TryParseQuery(string query, out IReadOnlyDictionary<string, string> parameters)
    {
        parameters = NoParams;
        if (query.Length == 0) return true;

        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (pairs.Length > MaxParams) return false;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var equals = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(equals >= 0 ? pair[..equals] : pair);
            if (key.Length == 0) return false;
            result[key] = equals >= 0 ? Uri.UnescapeDataString(pair[(equals + 1)..]) : string.Empty;
        }

        parameters = result;
        return true;
    }
}
