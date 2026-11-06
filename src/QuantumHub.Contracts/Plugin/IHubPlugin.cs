using QuantumHub.Contracts.Proto;

namespace QuantumHub.Contracts.Plugin;

/// <summary>
/// Сервисная часть плагина, загружаемая в хаб. Что делает плагин, решает он сам: хаб только вызывает
/// эти два метода и передаёт контекст.
/// </summary>
public interface IHubPlugin
{
    /// <summary>Запускает плагин. Здесь регистрируются методы и подписки.</summary>
    /// <param name="context">Всё, что хаб даёт плагину. Сохрани его в поле.</param>
    /// <param name="token">Отменяется по таймауту запуска, 30 секунд.</param>
    Task StartAsync(IPluginContext context, CancellationToken token);

    /// <summary>Останавливает плагин. После этого его код будет выгружен, поэтому потоки и хуки надо завершить здесь.</summary>
    /// <param name="token">Отменяется по таймауту остановки, 10 секунд.</param>
    Task StopAsync(CancellationToken token);
}

/// <summary>
/// Необязательный интерфейс: описывает страницы плагина для GUI. Внешние клиенты делают то же самое,
/// обрабатывая методы "ui.get" и "ui.event".
/// </summary>
public interface IUiProvider
{
    /// <summary>Отдаёт страницы плагина. Вызывается при каждом открытии плагина в GUI.</summary>
    /// <returns>Страницы в порядке вкладок.</returns>
    IEnumerable<UiPage> GetPages();

    /// <summary>Реакция на действие пользователя: нажатие кнопки или изменение значения.</summary>
    /// <param name="uiEvent">Что произошло.</param>
    void OnUiEvent(UiEvent uiEvent);
}

/// <summary>Отмечает класс плагина, чтобы хаб нашёл его без поля "service.type" в манифесте.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class HubPluginAttribute : Attribute;
