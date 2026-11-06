using System.Globalization;

namespace QuantumHub.Core.Plugins;

/// <summary>Версия вида major.minor.patch с необязательной пометкой pre-release. Pre-release считается раньше релиза.</summary>
/// <param name="Major">Несовместимые изменения.</param>
/// <param name="Minor">Новые возможности.</param>
/// <param name="Patch">Исправления.</param>
/// <param name="PreRelease">Пометка, например "beta".</param>
public sealed record SemVersion(int Major, int Minor, int Patch, string PreRelease = "") : IComparable<SemVersion>
{
    /// <summary>Разбирает версию. Минорная часть и патч необязательны.</summary>
    /// <param name="text">Текст версии.</param>
    /// <param name="version">Результат.</param>
    /// <returns><see langword="true"/>, если разобрать удалось.</returns>
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = new SemVersion(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var main = text.Trim();
        var preRelease = string.Empty;
        var plus = main.IndexOf('+');
        if (plus >= 0) main = main[..plus];
        var dash = main.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = main[(dash + 1)..];
            main = main[..dash];
        }

        var parts = main.Split('.');
        if (parts.Length is 0 or > 3) return false;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }

        version = new SemVersion(numbers[0], numbers[1], numbers[2], preRelease);
        return true;
    }

    /// <summary>Разбирает версию или бросает исключение.</summary>
    /// <param name="text">Текст версии.</param>
    /// <exception cref="FormatException">Это не версия.</exception>
    public static SemVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a version.");

    /// <summary>Сравнивает версии по правилам SemVer.</summary>
    /// <param name="other">С чем сравнить.</param>
    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (PreRelease.Length == 0) return other.PreRelease.Length == 0 ? 0 : 1;
        if (other.PreRelease.Length == 0) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    /// <summary>Версия в виде текста.</summary>
    public override string ToString() => PreRelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

/// <summary>
/// Требование к версии: "*", "1.2.3", "&gt;=1.0.0 &lt;2.0.0", "^1.2.0", "~1.2.0". Все части через пробел
/// должны выполняться одновременно.
/// </summary>
public sealed class VersionRange
{
    /// <summary>Проверки, по одной на часть требования.</summary>
    private readonly List<Func<SemVersion, bool>> _checks = [];

    private VersionRange()
    {
    }

    /// <summary>Разбирает требование. Пустая строка значит «любая версия».</summary>
    /// <param name="text">Текст требования.</param>
    /// <param name="range">Результат.</param>
    /// <returns><see langword="true"/>, если разобрать удалось.</returns>
    public static bool TryParse(string? text, out VersionRange range)
    {
        range = new VersionRange();
        if (string.IsNullOrWhiteSpace(text)) return true;

        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "*") continue;
            var check = ParsePart(part);
            if (check is null) return false;
            range._checks.Add(check);
        }

        return true;
    }

    /// <summary>Подходит ли версия.</summary>
    /// <param name="version">Версия.</param>
    public bool Includes(SemVersion version)
    {
        foreach (var check in _checks)
        {
            if (!check(version)) return false;
        }

        return true;
    }

    /// <summary>Разбирает одну часть требования.</summary>
    private static Func<SemVersion, bool>? ParsePart(string part)
    {
        foreach (var op in new[] { ">=", "<=", ">", "<", "=", "^", "~" })
        {
            if (!part.StartsWith(op, StringComparison.Ordinal)) continue;
            if (!SemVersion.TryParse(part[op.Length..], out var bound)) return null;
            return op switch
            {
                ">=" => v => v.CompareTo(bound) >= 0,
                "<=" => v => v.CompareTo(bound) <= 0,
                ">" => v => v.CompareTo(bound) > 0,
                "<" => v => v.CompareTo(bound) < 0,
                "=" => v => v.CompareTo(bound) == 0,
                "^" => v => v.CompareTo(bound) >= 0 && v.CompareTo(CaretLimit(bound)) < 0,
                _ => v => v.CompareTo(bound) >= 0 && v.CompareTo(new SemVersion(bound.Major, bound.Minor + 1, 0)) < 0,
            };
        }

        return SemVersion.TryParse(part, out var exact) ? v => v.CompareTo(exact) == 0 : null;
    }

    /// <summary>Верхняя граница для "^", как в npm: ниже 1.0 ломающим считается первый ненулевой компонент.</summary>
    private static SemVersion CaretLimit(SemVersion v) =>
        v.Major > 0 ? new SemVersion(v.Major + 1, 0, 0)
        : v.Minor > 0 ? new SemVersion(0, v.Minor + 1, 0)
        : new SemVersion(0, 0, v.Patch + 1);
}
