using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace CupriChat;

public partial class MainWindow : Window
{
    private const int MaxMessagesShown = 2000;

    private readonly ChatService _chat = new();
    private readonly ObservableCollection<Control> _messageItems = [];
    private readonly ObservableCollection<Control> _userItems = [];

    private readonly TextBlock _stepText;
    private readonly TextBlock _identityText;
    private readonly TextBlock _statusText;
    private readonly CheckBox _fileToggle;

    private readonly StackPanel _page1;
    private readonly StackPanel _page2;
    private readonly Grid _page3;

    private readonly Button _generateButton;
    private readonly TextBox _linkBox;
    private readonly Image _qrImage;
    private readonly TextBox _connectBox;
    private readonly Button _connectButton;
    private readonly Button _toStep2Button;

    private readonly TextBlock _historyHeader;
    private readonly TextBlock _historyHint;
    private readonly Border _historyBox;
    private readonly ListBox _historyList;
    private readonly ObservableCollection<Control> _historyItems = [];

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
        _fileToggle = this.FindControl<CheckBox>("FileToggle")!;

        _page1 = this.FindControl<StackPanel>("Page1")!;
        _page2 = this.FindControl<StackPanel>("Page2")!;
        _page3 = this.FindControl<Grid>("Page3")!;

        _generateButton = this.FindControl<Button>("GenerateButton")!;
        _linkBox = this.FindControl<TextBox>("LinkBox")!;
        _qrImage = this.FindControl<Image>("QrImage")!;
        _connectBox = this.FindControl<TextBox>("ConnectBox")!;
        _connectButton = this.FindControl<Button>("ConnectButton")!;
        _toStep2Button = this.FindControl<Button>("ToStep2Button")!;

        _historyHeader = this.FindControl<TextBlock>("HistoryHeader")!;
        _historyHint = this.FindControl<TextBlock>("HistoryHint")!;
        _historyBox = this.FindControl<Border>("HistoryBox")!;
        _historyList = this.FindControl<ListBox>("HistoryList")!;

        _usernameBox = this.FindControl<TextBox>("UsernameBox")!;
        _channelBox = this.FindControl<TextBox>("ChannelBox")!;
        _backTo1Button = this.FindControl<Button>("BackTo1Button")!;
        _joinButton = this.FindControl<Button>("JoinButton")!;

        _messagesList = this.FindControl<ListBox>("MessagesList")!;
        _messageBox = this.FindControl<TextBox>("MessageBox")!;
        _sendButton = this.FindControl<Button>("SendButton")!;
        _usersList = this.FindControl<ListBox>("UsersList")!;

        _messagesList.ItemsSource = _messageItems;
        _usersList.ItemsSource = _userItems;
        _historyList.ItemsSource = _historyItems;
        _historyList.SelectionChanged += OnHistorySelected;

        _chat.MessageArrived += OnMessage;
        _chat.Status += OnStatus;
        _chat.UsersChanged += OnUsers;
        _chat.FileOfferReceived += OnFileOffer;
        _chat.FileReceived += r => OnStatus($"Saved '{r.FileName}' to {r.SavePath}");

        _generateButton.Click += OnGenerate;
        _connectButton.Click += OnConnect;
        _toStep2Button.Click += (_, _) => ShowStep(2);
        _backTo1Button.Click += (_, _) => ShowStep(1);
        _joinButton.Click += OnJoin;
        _sendButton.Click += OnSend;
        _fileToggle.IsCheckedChanged += (_, _) => _chat.FileTransfersEnabled = _fileToggle.IsChecked == true;
        Closed += async (_, _) => await _chat.DisposeAsync();
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
                RefreshHistory();
            }
            catch (Exception ex) { OnStatus($"Start failed: {ex.Message}"); }
        };

        ShowStep(1);
    }

    private void RefreshHistory()
    {
        _historyItems.Clear();
        var history = _chat.History();
        foreach (var entry in history)
        {
            var peers = entry.PeerShortIds.Count == 0
                ? "no cached peers"
                : $"{entry.PeerShortIds.Count} peer(s): {string.Join(", ", entry.PeerShortIds)}";
            var item = new StackPanel { Tag = entry.ChannelName, Margin = new Thickness(2) };
            item.Children.Add(new TextBlock { Text = entry.ChannelName, FontWeight = FontWeight.Bold });
            item.Children.Add(new TextBlock { Text = peers, Foreground = Brushes.Gray, FontSize = 11 });
            _historyItems.Add(item);
        }

        var any = _historyItems.Count > 0;
        _historyHeader.IsVisible = any;
        _historyHint.IsVisible = any;
        _historyBox.IsVisible = any;
    }

    private async void OnHistorySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_historyList.SelectedItem is not Control { Tag: string channelName })
            return;
        _historyList.SelectedItem = null;

        var username = string.IsNullOrWhiteSpace(_usernameBox.Text) ? "anon" : _usernameBox.Text!.Trim();
        _channelBox.Text = channelName;
        _identityText.Text = $"you: {username}#{_chat.SelfShortId}";
        ShowStep(3);
        try { await _chat.ReconnectChannelAsync(channelName, username); }
        catch (Exception ex) { OnStatus($"Reconnect error: {ex.Message}"); }
    }

    private void ShowStep(int step)
    {
        if (step == 1)
            RefreshHistory();
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
        catch (Exception ex) { OnStatus($"Link error: {ex.Message}"); }
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
        catch (Exception ex) { OnStatus($"Connect error: {ex.Message}"); }
        finally { _connectButton.IsEnabled = true; }
    }

    private async void OnJoin(object? sender, RoutedEventArgs e)
    {
        _chat.SetIdentity(_usernameBox.Text ?? "anon", _channelBox.Text ?? ChatService.DefaultChannelName);
        ShowStep(3);
        await _chat.JoinChannelAsync();
        _identityText.Text = $"you: {_chat.Username}#{_chat.SelfShortId} (channel persona)";
    }

    private async void OnSend(object? sender, RoutedEventArgs e)
    {
        var text = _messageBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;
        _messageBox.Text = string.Empty;
        try { await _chat.SendAsync(text); }
        catch (Exception ex) { OnStatus($"Send error: {ex.Message}"); }
    }

    private void OnMessage(ChatMessage message) => Dispatcher.UIThread.Post(() =>
    {
        var who = $"{message.User}#{Short(message.AuthorId)}";
        var line = new TextBlock
        {
            Text = $"[{message.At:HH:mm}] {who}: {message.Text}",
            Foreground = Palette.For(message.AuthorId),
            FontWeight = message.IsLocal ? FontWeight.Bold : FontWeight.Normal,
            TextWrapping = TextWrapping.Wrap,
        };
        _messageItems.Add(line);
        while (_messageItems.Count > MaxMessagesShown)
            _messageItems.RemoveAt(0);
        if (_messageItems.Count > 0)
            _messagesList.ScrollIntoView(_messageItems.Count - 1);
    });

    private void OnUsers(IReadOnlyList<UserView> users) => Dispatcher.UIThread.Post(() =>
    {
        _userItems.Clear();
        foreach (var user in users)
        {
            var row = new TextBlock
            {
                Text = user.Display,
                Foreground = Palette.For(user.Id),
                FontWeight = user.IsSelf ? FontWeight.Bold : FontWeight.Normal,
                TextWrapping = TextWrapping.Wrap,
            };

            if (user.IsDirectPeer && !user.IsSelf)
            {
                var send = new MenuItem { Header = "Send file…" };
                var id = user.Id;
                send.Click += (_, _) => _ = SendFileToAsync(id);
                row.ContextMenu = new ContextMenu { ItemsSource = new[] { send } };
            }

            _userItems.Add(row);
        }
    });

    private void OnStatus(string status) => Dispatcher.UIThread.Post(() => _statusText.Text = status);

    private async Task SendFileToAsync(string peerId)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Send a file",
                AllowMultiple = false,
            });
            if (files.Count == 0)
                return;

            var path = files[0].TryGetLocalPath();
            if (path is null)
            {
                OnStatus("Cannot access that file's local path.");
                return;
            }

            await _chat.SendFileAsync(peerId, path);
        }
        catch (Exception ex) { OnStatus($"Send file error: {ex.Message}"); }
    }

    private void OnFileOffer(FileOffer offer) => Dispatcher.UIThread.Post(async () =>
    {
        try
        {
            var dialog = new FileOfferDialog($"{offer.FromDisplay} wants to send you:\n\n{offer.FileName}  ({FormatSize(offer.Size)})");
            var result = await dialog.ShowDialog<FileOfferResult>(this);

            switch (result)
            {
                case FileOfferResult.Accept:
                    var suggestedFolder = await TryDownloadsFolderAsync();
                    var save = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                    {
                        Title = "Save received file",
                        SuggestedFileName = offer.FileName,
                        SuggestedStartLocation = suggestedFolder,
                    });
                    var path = save?.TryGetLocalPath();
                    if (path is null)
                        await _chat.DeclineFileAsync(offer.TransferId);
                    else
                        await _chat.AcceptFileAsync(offer.TransferId, path);
                    break;

                case FileOfferResult.Disable:
                    await _chat.DeclineFileAsync(offer.TransferId);
                    _chat.FileTransfersEnabled = false;
                    _fileToggle.IsChecked = false;
                    break;

                default:
                    await _chat.DeclineFileAsync(offer.TransferId);
                    break;
            }
        }
        catch (Exception ex) { OnStatus($"File offer error: {ex.Message}"); }
    });

    private async Task<IStorageFolder?> TryDownloadsFolderAsync()
    {
        try { return await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Downloads); }
        catch { return null; }
    }

    private static string Short(string idHex) => idHex.Length >= 6 ? idHex[..6] : idHex;

    private static string FormatSize(long bytes)
        => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";
}
