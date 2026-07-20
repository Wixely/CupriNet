using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace CupriChat;

public partial class MainWindow : Window
{
    private readonly ChatService _chat = new();
    private readonly ObservableCollection<string> _messages = [];

    private readonly TextBox _usernameBox;
    private readonly TextBlock _statusText;
    private readonly Button _generateButton;
    private readonly TextBox _linkBox;
    private readonly Image _qrImage;
    private readonly TextBox _connectBox;
    private readonly Button _connectButton;
    private readonly ListBox _messagesList;
    private readonly TextBox _messageBox;
    private readonly Button _sendButton;

    public MainWindow()
    {
        InitializeComponent();

        _usernameBox = this.FindControl<TextBox>("UsernameBox")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _generateButton = this.FindControl<Button>("GenerateButton")!;
        _linkBox = this.FindControl<TextBox>("LinkBox")!;
        _qrImage = this.FindControl<Image>("QrImage")!;
        _connectBox = this.FindControl<TextBox>("ConnectBox")!;
        _connectButton = this.FindControl<Button>("ConnectButton")!;
        _messagesList = this.FindControl<ListBox>("MessagesList")!;
        _messageBox = this.FindControl<TextBox>("MessageBox")!;
        _sendButton = this.FindControl<Button>("SendButton")!;

        _messagesList.ItemsSource = _messages;

        _chat.MessageArrived += OnMessage;
        _chat.Status += OnStatus;

        _generateButton.Click += OnGenerate;
        _connectButton.Click += OnConnect;
        _sendButton.Click += OnSend;
        _messageBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OnSend(this, e);
                e.Handled = true;
            }
        };

        Opened += async (_, _) =>
        {
            try
            {
                await _chat.StartAsync();
            }
            catch (Exception ex)
            {
                OnStatus($"Start failed: {ex.Message}");
            }
        };
    }

    private void OnMessage(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        var who = message.IsLocal ? "you" : message.User;
        _messages.Add($"[{message.At:HH:mm}] {who}: {message.Text}");
        if (_messages.Count > 0)
            _messagesList.ScrollIntoView(_messages.Count - 1);
    });

    private void OnStatus(string status) => Dispatcher.UIThread.Post(() => _statusText.Text = status);

    private void OnGenerate(object? sender, RoutedEventArgs e)
    {
        try
        {
            var link = _chat.GenerateLink();
            _linkBox.Text = link;
            _qrImage.Source = QrCodeGenerator.Create(link);
        }
        catch (Exception ex)
        {
            OnStatus($"Link error: {ex.Message}");
        }
    }

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        var link = _connectBox.Text?.Trim();
        if (string.IsNullOrEmpty(link))
            return;
        try
        {
            _connectButton.IsEnabled = false;
            await _chat.ConnectAsync(link);
            _connectBox.Text = string.Empty;
        }
        catch (Exception ex)
        {
            OnStatus($"Connect error: {ex.Message}");
        }
        finally
        {
            _connectButton.IsEnabled = true;
        }
    }

    private async void OnSend(object? sender, RoutedEventArgs e)
    {
        var text = _messageBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        _chat.Username = string.IsNullOrWhiteSpace(_usernameBox.Text) ? "anon" : _usernameBox.Text!.Trim();
        _messageBox.Text = string.Empty;
        try
        {
            await _chat.SendAsync(text);
        }
        catch (Exception ex)
        {
            OnStatus($"Send error: {ex.Message}");
        }
    }
}
