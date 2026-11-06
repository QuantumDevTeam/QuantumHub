using System.Globalization;

namespace QuantumHub.Contracts.Ui;

/// <summary>
/// Язык интерфейса. Строки лежат прямо в коде парами, поэтому плагину не нужны файлы ресурсов.
/// Плагины работают в процессе хаба и видят его язык. Текст манифеста переводится в блоке "locales".
/// </summary>
public static class Loc
{
    /// <summary>Значение настройки: брать язык системы.</summary>
    public const string Auto = "auto";
    /// <summary>Русский.</summary>
    public const string Russian = "ru";
    /// <summary>Английский. Им же показывается всё, если у системы другой язык.</summary>
    public const string English = "en";

    /// <summary>Текущий язык.</summary>
    public static string Language { get; set; } = FromCulture(CultureInfo.CurrentUICulture);

    /// <summary>Выбирает текст на текущем языке.</summary>
    /// <param name="ru">Русский текст.</param>
    /// <param name="en">Английский текст.</param>
    public static string T(string ru, string en) => Language == Russian ? ru : en;

    /// <summary>Язык для настройки.</summary>
    /// <param name="setting">Настройка: "auto", "ru" или "en". Что-то другое считается "auto".</param>
    /// <param name="system">Язык системы.</param>
    public static string Resolve(string? setting, CultureInfo system) =>
        setting is Russian or English ? setting : FromCulture(system);

    /// <summary>Язык, ближайший к языку системы.</summary>
    /// <param name="culture">Язык системы.</param>
    public static string FromCulture(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == Russian ? Russian : English;
}
