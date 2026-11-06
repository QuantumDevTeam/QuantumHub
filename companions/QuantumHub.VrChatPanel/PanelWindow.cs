using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuantumHub.Client;
using QuantumHub.Contracts.Bus;

namespace QuantumHub.VrChatPanel;

/// <summary>Мини-окно: предпросмотр чатбокса, личный статус и сообщения.</summary>
public sealed class PanelWindow : Window
{
    private const string Plugin = "cap:vrchat.chatbox";

    private static readonly Brush Back = new SolidColorBrush(Color.FromRgb(0x24, 0x1B, 0x3F));
    private static readonly Brush Card = new SolidColorBrush(Color.FromRgb(0x35, 0x2A, 0x5C));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x6D, 0x4A, 0xE0));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xB9, 0xB0, 0xD6));

    private readonly TextBlock _preview = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _error = new() { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _status = Input();
    private readonly TextBox _chat = Input();
    private readonly CheckBox _sending = new() { Content = "Отправлять в VRChat", Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ListBox _statuses = new()
    {
        Background = Card,
        Foreground = Brushes.White,
        BorderThickness = new Thickness(0),
        Height = 150,
        Margin = new Thickness(0, 6, 0, 0),
    };
    private HubClient? _client;
    private bool _applying;

    /// <summary>Собирает окно.</summary>
    public PanelWindow()
    {
        Title = "VRChat";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        Topmost = true;
        ShowActivated = false;
        Background = Back;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Top + 140;

        var setStatus = Action("Поставить");
        setStatus.Click += async (_, _) => await SetStatusAsync();
        _status.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await SetStatusAsync();
        };

        var send = Action("Отправить");
        send.Click += async (_, _) => await SendAsync();
        _chat.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await SendAsync();
        };

        _statuses.MouseDoubleClick += async (_, _) =>
        {
            if (_statuses.SelectedItem is string status) await CallAsync("chatbox.status.set", status);
        };
        _statuses.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Delete && _statuses.SelectedItem is string status) await CallAsync("chatbox.status.remove", status);
        };

        _sending.Click += async (_, _) => await CallAsync("chatbox.sending.set", _sending.IsChecked == true ? "true" : "false");

        var column = new StackPanel { Margin = new Thickness(12) };
        column.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(8), Padding = new Thickness(10), MinHeight = 90, Child = _preview });
        column.Children.Add(Label("Личный статус"));
        column.Children.Add(Row(_status, setStatus));
        column.Children.Add(_statuses);
        column.Children.Add(new TextBlock { Text = "Два щелчка ставят, Delete убирает", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
        column.Children.Add(Label("Сообщение в чат"));
        column.Children.Add(Row(_chat, send));
        column.Children.Add(_sending);
        column.Children.Add(_error);
        Content = column;
    }

    /// <summary>Подключает окно к хабу.</summary>
    /// <param name="client">Клиент.</param>
    public void Attach(HubClient client) => _client = client;

    /// <summary>Показывает новое состояние плагина.</summary>
    /// <param name="state">Состояние.</param>
    public void Apply(ChatboxState state)
    {
        _applying = true;
        _preview.Text = state.Preview.Length > 0 ? state.Preview : "Пока пусто";
        _sending.IsChecked = state.Sending;
        if (!_status.IsKeyboardFocused) _status.Text = state.Status;

        var selected = _statuses.SelectedItem;
        _statuses.ItemsSource = state.Statuses;
        _statuses.SelectedItem = selected;
        _applying = false;
    }

    private async Task SetStatusAsync()
    {
        if (!_applying) await CallAsync("chatbox.status.set", _status.Text);
    }

    private async Task SendAsync()
    {
        if (_chat.Text.Trim().Length == 0) return;
        if (await CallAsync("chatbox.chat.send", _chat.Text)) _chat.Clear();
    }

    private async Task<bool> CallAsync(string method, string text)
    {
        if (_client is null) return false;
        try
        {
            await _client.CallAsync(Plugin, method, HubPayload.Text(text));
            _error.Text = string.Empty;
            return true;
        }
        catch (HubCallException ex)
        {
            _error.Text = ex.Message;
            return false;
        }
    }

    private static TextBox Input() => new()
    {
        Background = Card,
        Foreground = Brushes.White,
        CaretBrush = Brushes.White,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(8, 6, 8, 6),
        MaxLength = 140,
    };

    private static Button Action(string text) => new()
    {
        Content = text,
        Background = Accent,
        Foreground = Brushes.White,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(12, 6, 12, 6),
        Margin = new Thickness(6, 0, 0, 0),
    };

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Muted, Margin = new Thickness(0, 12, 0, 4) };

    private static Grid Row(UIElement input, UIElement action)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(action, 1);
        grid.Children.Add(input);
        grid.Children.Add(action);
        return grid;
    }
}
