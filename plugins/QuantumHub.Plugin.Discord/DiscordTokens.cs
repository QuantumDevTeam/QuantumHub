using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using QuantumHub.Contracts.Plugin;

namespace QuantumHub.Plugin.Discord;

/// <summary>Токены OAuth.</summary>
/// <param name="Access">Токен доступа.</param>
/// <param name="Refresh">Токен обновления.</param>
public sealed record DiscordTokens(string Access, string? Refresh);

/// <summary>Обмен кода на токены. Отделён, чтобы тесты не ходили в сеть.</summary>
public interface ITokenService
{
    /// <summary>Меняет код из AUTHORIZE на токены.</summary>
    /// <param name="code">Код.</param>
    /// <param name="options">Настройки приложения.</param>
    /// <param name="token">Отмена.</param>
    Task<DiscordTokens> ExchangeAsync(string code, DiscordOptions options, CancellationToken token);

    /// <summary>Обновляет истёкший токен.</summary>
    /// <param name="refreshToken">Токен обновления.</param>
    /// <param name="options">Настройки приложения.</param>
    /// <param name="token">Отмена.</param>
    Task<DiscordTokens> RefreshAsync(string refreshToken, DiscordOptions options, CancellationToken token);
}

/// <summary>Где хранятся токены между запусками.</summary>
public interface ITokenStore
{
    /// <summary>Сохранённые токены.</summary>
    DiscordTokens? Load();

    /// <summary>Сохраняет токены. <see langword="null"/> стирает их.</summary>
    /// <param name="tokens">Токены.</param>
    void Save(DiscordTokens? tokens);
}

/// <summary>Настоящий обмен через OAuth2 Discord. Это единственный сетевой запрос плагина.</summary>
public sealed class HttpTokenService : ITokenService
{
    private const string Endpoint = "https://discord.com/api/oauth2/token";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <inheritdoc/>
    public Task<DiscordTokens> ExchangeAsync(string code, DiscordOptions options, CancellationToken token) =>
        PostAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = options.RedirectUri,
        }, options, token);

    /// <inheritdoc/>
    public Task<DiscordTokens> RefreshAsync(string refreshToken, DiscordOptions options, CancellationToken token) =>
        PostAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, options, token);

    private static async Task<DiscordTokens> PostAsync(Dictionary<string, string> form, DiscordOptions options, CancellationToken token)
    {
        form["client_id"] = options.ClientId;
        form["client_secret"] = options.ClientSecret;

        using var response = await Client.PostAsync(Endpoint, new FormUrlEncodedContent(form), token);
        var text = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new DiscordRpcException((int)response.StatusCode, $"Discord не выдал токен, код {(int)response.StatusCode}. Проверь Client Secret и Redirect URI.");

        var json = JsonNode.Parse(text);
        var access = json?["access_token"]?.GetValue<string>() ?? throw new DiscordRpcException(0, "В ответе Discord нет токена.");
        return new DiscordTokens(access, json["refresh_token"]?.GetValue<string>());
    }
}

/// <summary>
/// Хранит токены в конфиге плагина, зашифрованными средствами Windows (DPAPI): прочитать их может только
/// этот пользователь на этом компьютере.
/// </summary>
/// <param name="config">Настройки плагина.</param>
public sealed class ProtectedTokenStore(IPluginConfig config) : ITokenStore
{
    private const string Key = "tokens";

    /// <inheritdoc/>
    public DiscordTokens? Load()
    {
        if (config.Get<string>(Key) is not { Length: > 0 } stored) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser);
            var parts = Encoding.UTF8.GetString(bytes).Split('\n');
            return new DiscordTokens(parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // Конфиг перенесён с другого компьютера: авторизуемся заново.
        }
    }

    /// <inheritdoc/>
    public void Save(DiscordTokens? tokens)
    {
        if (tokens is null)
        {
            config.Set(Key, string.Empty);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes($"{tokens.Access}\n{tokens.Refresh}");
        config.Set(Key, Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)));
    }
}
