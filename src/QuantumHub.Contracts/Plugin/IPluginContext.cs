using System.Text.Json.Nodes;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Proto;

namespace QuantumHub.Contracts.Plugin;

/// <summary>Всё, что хаб предлагает плагину.</summary>
public interface IPluginContext
{
    /// <summary>Манифест плагина.</summary>
    PluginManifest Manifest { get; }

    /// <summary>Имя экземпляра хаба. Нужно внешним частям плагина, чтобы подключиться к нему же.</summary>
    string HubInstance { get; }
    /// <summary>Вызовы, события и собственные методы.</summary>
    IHubBus Bus { get; }
    /// <summary>Настройки плагина.</summary>
    IPluginConfig Config { get; }
    /// <summary>Журнал, виден в GUI.</summary>
    IPluginLogger Logger { get; }
    /// <summary>Папки плагина.</summary>
    IPluginPaths Paths { get; }
    /// <summary>Состояние, к которому привязан интерфейс.</summary>
    IPluginUiState Ui { get; }
}

/// <summary>Сохранённые настройки плагина. Файлом владеет хаб, значения по умолчанию берутся из манифеста.</summary>
public interface IPluginConfig
{
    /// <summary>Читает настройку.</summary>
    /// <typeparam name="T">Тип значения.</typeparam>
    /// <param name="key">Ключ.</param>
    /// <param name="fallback">Что вернуть, если значения нет.</param>
    T? Get<T>(string key, T? fallback = default);

    /// <summary>Записывает настройку. На диск она попадёт чуть позже, вместе с другими изменениями.</summary>
    /// <typeparam name="T">Тип значения.</typeparam>
    /// <param name="key">Ключ.</param>
    /// <param name="value">Значение.</param>
    void Set<T>(string key, T value);

    /// <summary>Копия всех настроек.</summary>
    JsonObject Snapshot();

    /// <summary>Срабатывает с ключом, кто бы ни внёс изменение: плагин, GUI или клиент.</summary>
    event Action<string>? Changed;
}

/// <summary>Журнал плагина.</summary>
public interface IPluginLogger
{
    /// <summary>Подробности для отладки.</summary>
    /// <param name="message">Сообщение.</param>
    void Debug(string message);
    /// <summary>Обычное событие.</summary>
    /// <param name="message">Сообщение.</param>
    void Info(string message);
    /// <summary>Что-то пошло не так, но работа продолжается.</summary>
    /// <param name="message">Сообщение.</param>
    void Warning(string message);
    /// <summary>Ошибка.</summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="exception">Исключение, если есть.</param>
    void Error(string message, Exception? exception = null);
}

/// <summary>Папки плагина.</summary>
public interface IPluginPaths
{
    /// <summary>Только для чтения: папка, куда установлен плагин.</summary>
    string InstallDir { get; }

    /// <summary>Личные данные пользователя, переживают обновление плагина.</summary>
    string DataDir { get; }

    /// <summary>Кеш, который можно стереть в любой момент.</summary>
    string CacheDir { get; }
}

/// <summary>Состояние интерфейса плагина.</summary>
public interface IPluginUiState
{
    /// <summary>Меняет значение ключа, и GUI обновляет всё, что к нему привязано.</summary>
    /// <param name="key">Ключ состояния.</param>
    /// <param name="value">Значение.</param>
    void Set(string key, string value);
}
