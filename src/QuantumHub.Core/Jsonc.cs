using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuantumHub.Core;

/// <summary>Чтение JSON с комментариями и лишними запятыми. Формат конфигов и манифестов.</summary>
public static class Jsonc
{
    /// <summary>Что разрешено при разборе.</summary>
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Настройки записи: с отступами, чтобы файлы мог читать человек.</summary>
    public static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Разбирает JSONC.</summary>
    /// <param name="text">Текст файла.</param>
    /// <returns>Дерево значений.</returns>
    /// <exception cref="System.Text.Json.JsonException">Текст не является корректным JSONC.</exception>
    public static JsonNode? Parse(string text) => JsonNode.Parse(text, documentOptions: DocumentOptions);
}
