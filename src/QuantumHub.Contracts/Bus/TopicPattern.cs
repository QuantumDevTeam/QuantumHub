namespace QuantumHub.Contracts.Bus;

/// <summary>Сопоставление топиков с шаблонами подписки.</summary>
public static class TopicPattern
{
    /// <summary>Подходит ли топик под шаблон.</summary>
    /// <param name="pattern">Точный топик или префикс вида "hub.*".</param>
    /// <param name="topic">Топик события.</param>
    /// <returns><see langword="true"/>, если подходит.</returns>
    public static bool Matches(string pattern, string topic) =>
        pattern.EndsWith(".*", StringComparison.Ordinal)
            ? topic.Length >= pattern.Length - 1 &&
              topic.AsSpan().StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.Ordinal)
            : string.Equals(pattern, topic, StringComparison.Ordinal);
}
