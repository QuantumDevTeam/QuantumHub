using QuantumHub.Contracts.Ui;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QuantumHub.Contracts.Proto;
using QuantumHub.Gui.Services;

namespace QuantumHub.Gui.Rendering;

/// <summary>
/// Превращает дерево <see cref="UiNode"/> в настоящие контролы WinUI и держит их в согласии с состоянием.
/// GUI знает этот небольшой словарь и ничего не знает о конкретных плагинах.
/// </summary>
/// <remarks>Каждая привязка — это колбэк в таблице ключ состояния → установщики значения.</remarks>
/// <param name="hub">Связь с хабом.</param>
/// <param name="pluginId">Id плагина.</param>
/// <param name="pageId">Id страницы.</param>
public sealed class UiRenderer(HubConnection hub, string pluginId, string pageId)
{
    /// <summary>Пауза, после которой значение ползунка уходит в хаб.</summary>
    private static readonly TimeSpan SliderDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>Наибольшая ширина контрола справа в карточке.</summary>
    private const double MaxControlWidth = 420;

    /// <summary>Чаще этого страница не тикает, сколько бы ни попросил плагин.</summary>
    private const int MinLiveInterval = 250;

    /// <summary>Кого обновлять при изменении ключа.</summary>
    private readonly Dictionary<string, List<Action<string>>> _bindings = new();
    /// <summary>Текущие значения: контрол, созданный позже, сразу получает актуальное.</summary>
    private readonly Dictionary<string, string> _state = new();

    /// <summary>Применяет новые значения к контролам.</summary>
    /// <param name="state">Ключи и значения.</param>
    public void ApplyState(IEnumerable<KeyValuePair<string, string>> state)
    {
        foreach (var (key, value) in state)
        {
            _state[key] = value;
            if (!_bindings.TryGetValue(key, out var setters)) continue;
            foreach (var setter in setters) setter(value);
        }
    }

    /// <summary>Создаёт контрол для узла. Незнакомый тип превращается в предупреждение, а не в падение страницы.</summary>
    /// <param name="node">Узел.</param>
    public FrameworkElement Render(UiNode node) => node.Type switch
    {
        "stack" => RenderStack(node),
        "card" => RenderCard(node),
        "text" => RenderText(node),
        "keyvalue" => RenderKeyValue(node),
        "button" => RenderButton(node),
        "toggle" => RenderToggle(node),
        "textbox" => RenderTextBox(node),
        "password" => RenderPassword(node),
        "number" => RenderNumber(node),
        "slider" => RenderSlider(node),
        "choice" => RenderChoice(node),
        "infobar" => RenderInfoBar(node),
        "progress" => RenderProgress(node),
        "live" => RenderLive(node),
        "separator" => new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8), Background = Fluent.Resource("DividerStrokeColorDefaultBrush") },
        // Плагин новее этого GUI: говорим об этом, а не роняем всю страницу.
        _ => new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = Loc.T($"Неизвестный элемент: {node.Type}", $"Unknown element: {node.Type}") },
    };

    /// <summary>Значение свойства узла.</summary>
    private static string Prop(UiNode node, string name, string fallback = "") =>
        node.Props.TryGetValue(name, out var value) ? value : fallback;

    /// <summary>Привязывает свойство контрола к постоянному значению и к ключу состояния, если он указан.</summary>
    private void Bind(UiNode node, string prop, Action<string> setter)
    {
        if (node.Props.TryGetValue(prop, out var literal)) setter(literal);
        if (!node.Bindings.TryGetValue(prop, out var key)) return;

        if (!_bindings.TryGetValue(key, out var setters)) _bindings[key] = setters = [];
        setters.Add(setter);
        if (_state.TryGetValue(key, out var value)) setter(value);
    }

    /// <summary>Отправляет действие пользователя в хаб.</summary>
    private void Send(UiNode node, string eventName, string value = "")
    {
        node.Bindings.TryGetValue("value", out var bind);
        _ = hub.TryRunAsync("hub.ui.event", new UiEvent
        {
            PluginId = pluginId,
            PageId = pageId,
            NodeId = node.Id,
            Event = eventName,
            Value = value,
            Bind = bind ?? string.Empty,
        });
    }

    /// <summary>Контейнер.</summary>
    private StackPanel RenderStack(UiNode node)
    {
        var horizontal = Prop(node, "orientation") == "horizontal";
        var panel = new StackPanel
        {
            Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical,
            Spacing = double.TryParse(Prop(node, "spacing"), CultureInfo.InvariantCulture, out var spacing) ? spacing : 8,
        };
        foreach (var child in node.Children) panel.Children.Add(Render(child));
        return panel;
    }

    /// <summary>Карточка в стиле параметров Windows.</summary>
    private Border RenderCard(UiNode node)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Fluent.Text(Prop(node, "title")));
        if (Prop(node, "description").Length > 0) text.Children.Add(Fluent.Caption(Prop(node, "description")));

        // Текст лучше читается под заголовком, контролы стоят справа, как в параметрах Windows.
        var control = node.Children.FirstOrDefault();
        if (control is null) return Fluent.Card(text);
        if (control.Type is "text" or "stack" || Prop(control, "multiline") == "true")
        {
            var stacked = new StackPanel { Spacing = 10 };
            stacked.Children.Add(text);
            stacked.Children.Add(Render(control));
            return Fluent.Card(stacked);
        }

        var rendered = Render(control);
        rendered.VerticalAlignment = VerticalAlignment.Center;

        // Колонка контрола подстраивается под содержимое: длинное значение иначе вытолкнет текст за край карточки.
        rendered.MaxWidth = MaxControlWidth;
        return Fluent.Card(Fluent.Columns((text, Fluent.Star), (rendered, Fluent.Auto)));
    }

    /// <summary>Текст.</summary>
    private TextBlock RenderText(UiNode node)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        switch (Prop(node, "style"))
        {
            case "title":
                block.Style = Fluent.TextStyle("TitleTextBlockStyle");
                break;
            case "subtitle":
                block.Style = Fluent.TextStyle("BodyStrongTextBlockStyle");
                block.Margin = new Thickness(0, 12, 0, 0);
                break;
            case "caption":
                block.Style = Fluent.TextStyle("CaptionTextBlockStyle");
                break;
            case "mono":
                block.FontFamily = new FontFamily("Cascadia Mono, Consolas");
                break;
        }

        Bind(node, "text", value => block.Text = value);
        return block;
    }

    /// <summary>Строка «название: значение».</summary>
    private Border RenderKeyValue(UiNode node)
    {
        var value = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, TextAlignment = TextAlignment.Right };
        Bind(node, "value", text => value.Text = text);
        return Fluent.Card(Fluent.Columns((Fluent.Text(Prop(node, "key"), secondary: true), Fluent.Auto), (value, Fluent.Star)), 12);
    }

    /// <summary>Кнопка.</summary>
    private Button RenderButton(UiNode node)
    {
        var button = new Button { Content = Prop(node, "label") };
        if (Prop(node, "style") == "accent") button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => Send(node, "click");
        return button;
    }

    /// <summary>Переключатель. Флаг applying отличает изменение из кода от действия пользователя, иначе вышла бы петля.</summary>
    private ToggleSwitch RenderToggle(UiNode node)
    {
        var toggle = new ToggleSwitch { OnContent = Loc.T("Вкл", "On"), OffContent = Loc.T("Выкл", "Off") };
        var applying = false;
        Bind(node, "value", value =>
        {
            applying = true;
            toggle.IsOn = bool.TryParse(value, out var on) && on;
            applying = false;
        });
        toggle.Toggled += (_, _) =>
        {
            if (!applying) Send(node, "changed", toggle.IsOn ? "true" : "false");
        };
        return toggle;
    }

    /// <summary>Поле ввода. Значение уходит при потере фокуса или по Enter.</summary>
    private TextBox RenderTextBox(UiNode node)
    {
        var box = new TextBox { PlaceholderText = Prop(node, "placeholder"), MinWidth = 240 };
        var multiline = Prop(node, "multiline") == "true";
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.MinHeight = 96;
        }

        var focused = false;
        var sent = string.Empty;

        // Никогда не перезаписываем то, что пользователь сейчас печатает.
        Bind(node, "value", value =>
        {
            if (focused) return;
            box.Text = value;

            // Поле заменяет \n на \r, и сравнивать надо с тем, что в нём оказалось. Иначе простой щелчок по полю считался бы правкой.
            sent = box.Text;
        });

        void Commit()
        {
            if (box.Text == sent) return;
            sent = box.Text;
            Send(node, "changed", box.Text.Replace("\r\n", "\n").Replace('\r', '\n'));
        }

        box.GotFocus += (_, _) => focused = true;
        box.LostFocus += (_, _) =>
        {
            focused = false;
            Commit();
        };
        box.KeyDown += (_, e) =>
        {
            if (multiline || e.Key != Windows.System.VirtualKey.Enter) return;
            Commit();
            Send(node, "submit", box.Text);
        };
        return box;
    }

    /// <summary>Поле со скрытым вводом. Значение уходит при потере фокуса.</summary>
    private PasswordBox RenderPassword(UiNode node)
    {
        var box = new PasswordBox { MinWidth = 240 };
        var sent = string.Empty;
        Bind(node, "value", value =>
        {
            box.Password = value;
            sent = value;
        });
        box.LostFocus += (_, _) =>
        {
            if (box.Password == sent) return;
            sent = box.Password;
            Send(node, "changed", box.Password);
        };
        return box;
    }

    /// <summary>Поле для числа.</summary>
    private NumberBox RenderNumber(UiNode node)
    {
        var box = new NumberBox { SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 120 };
        var applying = false;
        Bind(node, "value", value =>
        {
            if (!double.TryParse(value, CultureInfo.InvariantCulture, out var number)) return;
            applying = true;
            box.Value = number;
            applying = false;
        });
        box.ValueChanged += (_, args) =>
        {
            if (!applying && !double.IsNaN(args.NewValue))
                Send(node, "changed", args.NewValue.ToString(CultureInfo.InvariantCulture));
        };
        return box;
    }

    /// <summary>Ползунок с текущим значением рядом.</summary>
    private StackPanel RenderSlider(UiNode node)
    {
        var slider = new Slider
        {
            Minimum = double.Parse(Prop(node, "min", "0"), CultureInfo.InvariantCulture),
            Maximum = double.Parse(Prop(node, "max", "100"), CultureInfo.InvariantCulture),
            MinWidth = 220,
        };

        var applying = false;
        Bind(node, "value", value =>
        {
            if (!double.TryParse(value, CultureInfo.InvariantCulture, out var number)) return;
            applying = true;
            slider.Value = number;
            applying = false;
        });

        // Перетаскивание порождает десятки изменений. Хабу важно только, где ползунок остановился.
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = SliderDebounce;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Send(node, "changed", slider.Value.ToString(CultureInfo.InvariantCulture));
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 36, TextAlignment = TextAlignment.Right };
        label.Text = slider.Value.ToString("0.##", CultureInfo.CurrentCulture);
        slider.ValueChanged += (_, _) =>
        {
            label.Text = slider.Value.ToString("0.##", CultureInfo.CurrentCulture);
            if (!applying) timer.Start();
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(label);
        row.Children.Add(slider);
        return row;
    }

    /// <summary>Выпадающий список. Показывает подписи, а в хаб отправляет значения.</summary>
    private ComboBox RenderChoice(UiNode node)
    {
        var box = new ComboBox { MinWidth = 180 };
        var options = Prop(node, "options").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var labels = Prop(node, "labels").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < options.Length; i++) box.Items.Add(labels.Length == options.Length ? labels[i] : options[i]);

        var applying = false;
        Bind(node, "value", value =>
        {
            applying = true;
            box.SelectedIndex = Array.IndexOf(options, value);
            applying = false;
        });
        box.SelectionChanged += (_, _) =>
        {
            if (!applying && box.SelectedIndex >= 0) Send(node, "changed", options[box.SelectedIndex]);
        };
        return box;
    }

    /// <summary>Невидимый узел: сообщает плагину, что страница открыта, и тикает, пока её видно.</summary>
    private Border RenderLive(UiNode node)
    {
        var interval = int.TryParse(Prop(node, "interval"), CultureInfo.InvariantCulture, out var ms) ? ms : 2000;
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(interval, MinLiveInterval));

        var marker = new Border();
        timer.Tick += (_, _) =>
        {
            // Окно могло уйти в трей: страница «открыта», но её никто не видит.
            if (marker.XamlRoot?.IsHostVisible == true) Send(node, "tick");
        };
        marker.Loaded += (_, _) =>
        {
            Send(node, "opened");
            timer.Start();
        };
        marker.Unloaded += (_, _) =>
        {
            timer.Stop();
            Send(node, "closed");
        };
        return marker;
    }

    /// <summary>Информационная полоса.</summary>
    private InfoBar RenderInfoBar(UiNode node)
    {
        var bar = new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = Prop(node, "severity") switch
            {
                "success" => InfoBarSeverity.Success,
                "warning" => InfoBarSeverity.Warning,
                "error" => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational,
            },
        };
        Bind(node, "title", value => bar.Title = value);
        Bind(node, "message", value => bar.Message = value);
        return bar;
    }

    /// <summary>Индикатор прогресса.</summary>
    private ProgressBar RenderProgress(UiNode node)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = !node.Bindings.ContainsKey("value") && !node.Props.ContainsKey("value") };
        Bind(node, "value", value =>
        {
            if (double.TryParse(value, CultureInfo.InvariantCulture, out var number)) bar.Value = number;
        });
        return bar;
    }
}
