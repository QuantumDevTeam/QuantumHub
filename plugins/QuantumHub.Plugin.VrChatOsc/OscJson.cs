using System.Text.Json;

namespace QuantumHub.Plugin.VrChatOsc;

/// <summary>Перевод аргументов между JSON из API и типами OSC.</summary>
public static class OscJson
{
    /// <summary>
    /// Превращает значение JSON в аргумент OSC. Целое число становится int, дробное — float.
    /// Чтобы отправить целое как float, запиши его объектом: { "float": 1 }.
    /// </summary>
    /// <param name="value">Значение.</param>
    /// <exception cref="ArgumentException">Значение нельзя отправить.</exception>
    public static object ToArgument(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt32(out var number) => number,
        JsonValueKind.Number => value.GetSingle(),
        JsonValueKind.Object when value.TryGetProperty("float", out var number) => number.GetSingle(),
        _ => throw new ArgumentException($"Нельзя отправить значение {value}."),
    };
}
