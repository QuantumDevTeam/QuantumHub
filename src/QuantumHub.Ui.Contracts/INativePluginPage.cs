using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using QuantumHub.Contracts.Bus;

namespace QuantumHub.Ui.Contracts;

/// <summary>
/// GUI-часть плагина на случай, когда декларативного интерфейса мало. Класс живёт в сборке, указанной в
/// поле "ui" манифеста, и получает настоящее дерево элементов WinUI. Загружается только в процесс GUI,
/// поэтому хабу WinUI не нужен.
/// </summary>
public interface INativePluginPage
{
    /// <summary>Заголовок вкладки.</summary>
    string Title { get; }

    /// <summary>Код символа Segoe Fluent Icons, например "E7C4".</summary>
    string Icon { get; }

    /// <summary>Создаёт содержимое вкладки. Вызывается на потоке интерфейса при каждом её открытии.</summary>
    /// <param name="host">Связь с GUI и хабом.</param>
    /// <returns>Корневой элемент.</returns>
    FrameworkElement CreateView(IPluginUiHost host);
}

/// <summary>Что GUI даёт нативной странице.</summary>
public interface IPluginUiHost
{
    /// <summary>Id плагина.</summary>
    string PluginId { get; }

    /// <summary>Связь с хабом, а через него и с сервисной частью плагина.</summary>
    IHubBus Bus { get; }

    /// <summary>Очередь потока интерфейса. События приходят на других потоках, и менять элементы надо через неё.</summary>
    DispatcherQueue Dispatcher { get; }
}
