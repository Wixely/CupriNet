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
    private readonly ObservableCollection<string> _users = [];

    private readonly TextBlock _stepText;
    private readonly TextBlock _identityText;
    private readonly TextBlock _statusText;

    private readonly StackPanel _page1;
    private readonly StackPanel _page2;
    private readonly Grid _page3;

    private readonly Button _generateButton;
    private readonly TextBox _linkBox;
    private readonly Image _qrImage;
    private readonly TextBox _connectBox;
    private readonly Button _connectButton;
    private readonly Button _toStep2Button;

    private readonly TextBox _usernameBox;
    private readonly TextBox _channelBox;
    private readonly Button _backTo1Button;
    private readonly Button _joinButton;

    private readonly ListBox _messagesList;
    private readonly TextBox _messageBox;
    private readonly Button _sendButton;
    private readonly ListBox _usersList;

    public MainWindow()
    {
        InitializeComponent();

        _stepText = this.FindControl<TextBlock>("StepText")!;
        _identityText = this.FindControl<TextBlock>("IdentityText")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;

        _page1 = this.FindControl<StackPanel>("Page1")!;
        _page2 = this.FindControl<StackPanel>("Page2")!;
        _page3 = this.FindControl<Grid>("Page3")!;

        _generateButton = this.FindControl<Button>("GenerateButton")!;
        _linkBox = this.FindControl<TextBox>("LinkBox")!;
        _qrImage = this.FindControl<Image>("QrImage")!;
        _connectBox = this.FindControl<TextBox>("ConnectBox")!;
        _connectButton = this.FindControl<Button>("ConnectButton")!;
        _toStep2Button = this.FindControl<Button>("ToStep2Button")!;

        _usernameBox = this.FindControl<TextBox>("UsernameBox")!;
        _channelBox = this.FindControl<TextBox>("ChannelBox")!;
        _backTo1Button = this.FindControl<Button>("BackTo1Button")!;
        _joinButton = this.FindControl<Button>("JoinButton")!;

        _messagesList = this.FindControl<ListBox>("MessagesList")!;
        _messageBox = this.FindControl<TextBox>("MessageBox")!;
        _sendButton = this.FindControl<Button>("SendButton")!;
        _usersList = this.FindControl<ListBox>("UsersList")!;

        _messagesList.ItemsSource = _messages;
        _usersList.ItemsSource = _users;

        _chat.MessageArrived += OnMessage;
        _chat.Status += OnStatus;
        _chat.UsersChanged += OnUsers;

        _generateButton.Click += OnGenerate;
        _connectButton.Click += OnConnect;
        _toStep2Button.Click += (_, _) => ShowStep(2);
        _backTo1Button.Click += (_, _) => ShowStep(1);
        _joinButton.Click += OnJoin;
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

        ShowStep(1);
    }

    private void ShowStep(int step)
    {
        _page1.IsVisible = step == 1;
        _page2.IsVisible = step == 2;
        _page3.IsVisible = step == 3;
        _stepText.Text = step switch
        {
            1 => "Step 1 — Connect",
            2 => "Step 2 — Identity & channel",
            _ => "Step 3 — Chat",
        };
        if (step == 3)
            _messageBox.Focus();
    }

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

    private void OnJoin(object? sender, RoutedEventArgs e)
    {
        _chat.SetIdentity(_usernameBox.Text ?? "anon", _channelBox.Text ?? ChatService.DefaultChannelName);
        _chat.JoinChannel();
        _identityText.Text = $"you: {_chat.Username}#{_chat.SelfShortId}";
        ShowStep(3);
    }

    private async void OnSend(object? sender, RoutedEventArgs e)
    {
        var text = _messageBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;
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

    private void OnMessage(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        var who = message.IsLocal ? $"you#{message.ShortId}" : $"{message.User}#{message.ShortId}";
        _messages.Add($"[{message.At:HH:mm}] {who}: {message.Text}");
        if (_messages.Count > 0)
            _messagesList.ScrollIntoView(_messages.Count - 1);
    });

    private void OnUsers(IReadOnlyList<UserView> users) => Dispatcher.UIThread.Post(() =>
    {
        _users.Clear();
        foreach (var user in users)
            _users.Add(user.Display);
    });

    private void OnStatus(string status) => Dispatcher.UIThread.Post(() => _statusText.Text = status);
}
