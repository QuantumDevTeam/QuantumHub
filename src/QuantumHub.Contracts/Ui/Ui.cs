using QuantumHub.Contracts.Proto;

namespace QuantumHub.Contracts.Ui;

/// <summary>Короткая запись для построения дерева <see cref="UiNode"/>.</summary>
/// <example>
/// <code>
/// yield return Ui.Page("main", "Звук", "E767",
///     Ui.KeyValue("Сейчас", "current"),
///     Ui.Button("vr", "На VR-гарнитуру", accent: true));
/// </code>
/// </example>
public static class Ui
{
    /// <summary>Создаёт страницу.</summary>
    /// <param name="id">Id страницы, приходит обратно в событиях.</param>
    /// <param name="title">Заголовок вкладки.</param>
    /// <param name="icon">Код символа Segoe Fluent Icons, например "E713".</param>
    /// <param name="content">Содержимое сверху вниз.</param>
    public static UiPage Page(string id, string title, string icon, params UiNode[] content) =>
        new() { Id = id, Title = title, Icon = icon, Root = Stack(content) };

    /// <summary>Вертикальный контейнер.</summary>
    /// <param name="children">Содержимое.</param>
    public static UiNode Stack(params UiNode[] children) => Node("stack").With(children);

    /// <summary>Горизонтальный контейнер.</summary>
    /// <param name="children">Содержимое.</param>
    public static UiNode Row(params UiNode[] children) =>
        Node("stack").Prop("orientation", "horizontal").With(children);

    /// <summary>Карточка в стиле параметров Windows.</summary>
    /// <param name="title">Заголовок.</param>
    /// <param name="description">Пояснение.</param>
    /// <param name="control">Контрол: встанет справа, а текст — под заголовком.</param>
    public static UiNode Card(string title, string? description = null, UiNode? control = null)
    {
        var card = Node("card").Prop("title", title);
        if (description is not null) card.Prop("description", description);
        if (control is not null) card.Children.Add(control);
        return card;
    }

    /// <summary>Постоянный текст.</summary>
    /// <param name="text">Текст.</param>
    /// <param name="style">"title", "subtitle", "body", "caption" или "mono".</param>
    public static UiNode Text(string text, string style = "body") =>
        Node("text").Prop("text", text).Prop("style", style);

    /// <summary>Текст, привязанный к ключу состояния.</summary>
    /// <param name="stateKey">Ключ состояния.</param>
    /// <param name="style">"title", "subtitle", "body", "caption" или "mono".</param>
    public static UiNode BoundText(string stateKey, string style = "body") =>
        Node("text").Prop("style", style).Bind("text", stateKey);

    /// <summary>Строка «название: значение».</summary>
    /// <param name="key">Название.</param>
    /// <param name="stateKey">Ключ состояния со значением.</param>
    public static UiNode KeyValue(string key, string stateKey) =>
        Node("keyvalue").Prop("key", key).Bind("value", stateKey);

    /// <summary>Кнопка. Нажатие приходит плагину с NodeId, равным <paramref name="id"/>.</summary>
    /// <param name="id">Id узла.</param>
    /// <param name="label">Надпись.</param>
    /// <param name="accent">Выделить цветом.</param>
    public static UiNode Button(string id, string label, bool accent = false) =>
        Node("button", id).Prop("label", label).Prop("style", accent ? "accent" : "default");

    /// <summary>Поле ввода.</summary>
    /// <param name="id">Id узла.</param>
    /// <param name="placeholder">Подсказка в пустом поле.</param>
    public static UiNode TextBox(string id, string placeholder = "") =>
        Node("textbox", id).Prop("placeholder", placeholder);

    /// <summary>Переключатель.</summary>
    /// <param name="id">Id узла.</param>
    public static UiNode Toggle(string id) => Node("toggle", id);

    /// <summary>Информационная полоса.</summary>
    /// <param name="title">Заголовок.</param>
    /// <param name="message">Текст.</param>
    /// <param name="severity">"informational", "success", "warning" или "error".</param>
    public static UiNode Info(string title, string message, string severity = "informational") =>
        Node("infobar").Prop("title", title).Prop("message", message).Prop("severity", severity);

    /// <summary>
    /// Невидимый узел «страница сейчас открыта». Пока её видно, плагину приходят события: "opened" при открытии,
    /// "tick" через каждый интервал и "closed" при уходе. Так данные обновляются сами и только когда их смотрят.
    /// </summary>
    /// <param name="id">Id узла.</param>
    /// <param name="intervalMs">Интервал в миллисекундах.</param>
    public static UiNode Live(string id, int intervalMs = 2000) =>
        Node("live", id).Prop("interval", intervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Узел произвольного типа.</summary>
    /// <param name="type">Тип узла.</param>
    /// <param name="id">Id узла.</param>
    public static UiNode Node(string type, string id = "") => new() { Type = type, Id = id };

    /// <summary>Задаёт свойство. Возвращает тот же узел, чтобы вызовы шли цепочкой.</summary>
    /// <param name="node">Узел.</param>
    /// <param name="name">Имя свойства.</param>
    /// <param name="value">Значение.</param>
    public static UiNode Prop(this UiNode node, string name, string value)
    {
        node.Props[name] = value;
        return node;
    }

    /// <summary>Привязывает свойство к ключу состояния. Ключ "config:имя" привязывает прямо к настройке плагина.</summary>
    /// <param name="node">Узел.</param>
    /// <param name="prop">Имя свойства.</param>
    /// <param name="stateKey">Ключ состояния.</param>
    public static UiNode Bind(this UiNode node, string prop, string stateKey)
    {
        node.Bindings[prop] = stateKey;
        return node;
    }

    /// <summary>Добавляет дочерние узлы.</summary>
    /// <param name="node">Узел.</param>
    /// <param name="children">Дочерние узлы.</param>
    public static UiNode With(this UiNode node, params UiNode[] children)
    {
        node.Children.Add(children);
        return node;
    }
}
