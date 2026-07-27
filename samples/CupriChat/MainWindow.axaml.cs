using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
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
    private readonly CheckBox _networkDiscoveryToggle;
    private readonly Button _backTo1Button;
    private readonly Button _joinButton;

    private readonly ListBox _messagesList;
    private readonly Border _messagesBorder;
    private readonly TextBox _messageBox;
    private readonly Button _sendButton;
    private readonly ListBox _usersList;

    private IReadOnlyList<UserView> _members = [];

    // @-mention tab-completion state (to cycle through matches on repeated Tab)
    private int _mentionAt = -1;
    private string _mentionPrefix = string.Empty;
    private string _mentionLast = string.Empty;
    private int _mentionIndex;

    // Startup mode selector (Page 0).
    private readonly StackPanel _page0;
    private readonly Button _clearnetButton;
    private readonly Button _torButton;
    private readonly TextBox _joinUrlBox;
    private readonly Button _joinUrlButton;
    private readonly Button _refreshLinkButton;
    private readonly Button _copyLinkButton;

    public MainWindow()
    {
        InitializeComponent();

        // This build includes the Tor transport (references CupriNet.Tor): the "Tor" option builds an onion
        // service and dials peers over Tor. The transport uses the same per-mode encrypted store.
        _chat.OnionTransportFactory = async (store, ct) => await CupriNet.Tor.CupriTorOnionTransport.CreateAsync(store, ct);

        _page0 = this.FindControl<StackPanel>("Page0")!;
        _clearnetButton = this.FindControl<Button>("ClearnetButton")!;
        _torButton = this.FindControl<Button>("TorButton")!;
        _joinUrlBox = this.FindControl<TextBox>("JoinUrlBox")!;
        _joinUrlButton = this.FindControl<Button>("JoinUrlButton")!;
        _refreshLinkButton = this.FindControl<Button>("RefreshLinkButton")!;
        _copyLinkButton = this.FindControl<Button>("CopyLinkButton")!;

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
        _networkDiscoveryToggle = this.FindControl<CheckBox>("NetworkDiscoveryToggle")!;
        _backTo1Button = this.FindControl<Button>("BackTo1Button")!;
        _joinButton = this.FindControl<Button>("JoinButton")!;

        _messagesList = this.FindControl<ListBox>("MessagesList")!;
        _messagesBorder = this.FindControl<Border>("MessagesBorder")!;
        _messageBox = this.FindControl<TextBox>("MessageBox")!;
        _sendButton = this.FindControl<Button>("SendButton")!;
        _usersList = this.FindControl<ListBox>("UsersList")!;

        _messagesList.ItemsSource = _messageItems;
        _usersList.ItemsSource = _userItems;
        _historyList.ItemsSource = _historyItems;
        _historyList.SelectionChanged += OnHistorySelected;

        _chat.MessageArrived += OnMessage;
        _chat.Status += OnStatus;
        _chat.SystemMessage += OnSystem;
        _chat.UsersChanged += OnUsers;
        _chat.FileOfferReceived += OnFileOffer;
        _chat.FileReceived += r => OnSystem($"Received file '{r.FileName}' → {r.SavePath}");

        _clearnetButton.Click += async (_, _) => { await StartModeAsync(ReachabilityChoice.Clearnet); TryAutoGenerateLink(); };
        _torButton.Click += async (_, _) => { await StartModeAsync(ReachabilityChoice.Tor); TryAutoGenerateLink(); };
        _joinUrlButton.Click += OnJoinUrl;
        _generateButton.Click += OnGenerate;
        _refreshLinkButton.Click += OnGenerate;
        _copyLinkButton.Click += OnCopyLink;
        _chat.ReachabilityChanged += () => Dispatcher.UIThread.Post(TryAutoGenerateLink);
        _connectButton.Click += OnConnect;
        _toStep2Button.Click += (_, _) => ShowStep(2);
        _backTo1Button.Click += (_, _) => ShowStep(1);
        _joinButton.Click += OnJoin;
        _sendButton.Click += OnSend;
        _fileToggle.IsCheckedChanged += (_, _) => _chat.FileTransfersEnabled = _fileToggle.IsChecked == true;
        _networkDiscoveryToggle.IsCheckedChanged += (_, _) => _chat.NetworkDiscovery = _networkDiscoveryToggle.IsChecked == true;
        Closed += async (_, _) => await _chat.DisposeAsync();
        _messageBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OnSend(this, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && TryCompleteMention())
            {
                e.Handled = true; // consume Tab so focus doesn't move
            }
        };

        ShowStep(0); // choose Clearnet / Tor / Join-with-URL before the node starts
    }

    /// <summary>Starts the node in the chosen mode (a fresh, isolated identity per mode), then moves to Step 1.</summary>
    private async Task StartModeAsync(ReachabilityChoice mode)
    {
        try
        {
            _clearnetButton.IsEnabled = _torButton.IsEnabled = _joinUrlButton.IsEnabled = false;
            OnStatus(mode == ReachabilityChoice.Tor ? "Starting Tor — this can take a moment…" : "Starting…");
            await _chat.StartAsync(mode);
            RefreshHistory();
            ShowStep(1);
        }
        catch (Exception ex)
        {
            OnStatus($"Start failed: {ex.Message}");
            _clearnetButton.IsEnabled = _torButton.IsEnabled = _joinUrlButton.IsEnabled = true;
            ShowStep(0);
        }
    }

    /// <summary>Join by pasting a link: its type locks the mode (onion → Tor, address → Clearnet), then connect.</summary>
    private async void OnJoinUrl(object? sender, RoutedEventArgs e)
    {
        var url = _joinUrlBox.Text?.Trim();
        var mode = ChatService.DetectMode(url ?? string.Empty);
        if (mode is null)
        {
            OnStatus("That doesn't look like a valid cuprinet:// link.");
            return;
        }
        await StartModeAsync(mode.Value);
        if (_chat.Mode != mode.Value) // start failed and reset to Step 0
            return;
        try
        {
            await _chat.ConnectAsync(url!);
            ShowStep(2); // paired at L1 — go set identity + channel
        }
        catch (Exception ex) { OnStatus($"Connect error: {ex.Message}"); }
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
        _page0.IsVisible = step == 0;
        _page1.IsVisible = step == 1;
        _page2.IsVisible = step == 2;
        _page3.IsVisible = step == 3;
        _stepText.Text = step switch
        {
            0 => "Choose network",
            1 => $"Step 1 — Connect ({_chat.Mode})",
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
            _copyLinkButton.IsVisible = true;
            _refreshLinkButton.IsVisible = true; // reachability/beacons can change; let the user regenerate
        }
        catch (Exception ex) { OnStatus($"Link error: {ex.Message}"); }
    }

    /// <summary>
    /// When we've started our own network (not joined via a pasted link), show the connection link + QR right away —
    /// clearnet is reachable immediately; Tor waits for the onion, so this no-ops until <see cref="ChatService.ReachabilityChanged"/>
    /// fires with the onion published, at which point it's called again in place.
    /// </summary>
    private void TryAutoGenerateLink()
    {
        if (_page1.IsVisible && _chat.ReachabilityReady)
            OnGenerate(this, new RoutedEventArgs());
    }

    private async void OnCopyLink(object? sender, RoutedEventArgs e)
    {
        var link = _linkBox.Text;
        if (string.IsNullOrEmpty(link))
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        try
        {
            await clipboard.SetTextAsync(link);
            OnStatus("Link copied to clipboard.");
        }
        catch (Exception ex) { OnStatus($"Copy failed: {ex.Message}"); }
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
        _chat.NetworkDiscovery = _networkDiscoveryToggle.IsChecked == true;
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
        var stamp = $"[{message.At:HH:mm:ss}] ";
        var mentioned = !message.IsLocal && MentionsMe(message.Text);

        var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (mentioned)
        {
            // Someone @'d us: large red, and flash the chat box.
            line.Text = $"{stamp}{who}: {message.Text}";
            line.Foreground = Brushes.Red;
            line.FontWeight = FontWeight.Bold;
            line.FontSize = 18;
        }
        else if (message.IsLocal)
        {
            // Our own messages: white text, with our username underlined.
            line.Inlines!.Add(new Run(stamp) { Foreground = Brushes.White });
            line.Inlines.Add(new Run(who) { Foreground = Brushes.White, TextDecorations = TextDecorations.Underline });
            line.Inlines.Add(new Run($": {message.Text}") { Foreground = Brushes.White });
            line.FontWeight = FontWeight.Bold;
        }
        else
        {
            line.Text = $"{stamp}{who}: {message.Text}";
            line.Foreground = Palette.For(message.AuthorId);
        }

        AppendChatLine(line);

        if (mentioned)
            FlashChatBox();
    });

    /// <summary>Appends a chat/log line to the message pane, trimming old lines and scrolling to the end.</summary>
    private void AppendChatLine(Control line)
    {
        _messageItems.Add(line);
        while (_messageItems.Count > MaxMessagesShown)
            _messageItems.RemoveAt(0);
        if (_messageItems.Count > 0)
            _messagesList.ScrollIntoView(_messageItems.Count - 1);
    }

    /// <summary>Renders a chat event (join, leave, file activity) as an inline log line.</summary>
    private void OnSystem(string text) => Dispatcher.UIThread.Post(() =>
        AppendChatLine(new TextBlock
        {
            Text = $"[{DateTimeOffset.Now:HH:mm:ss}] • {text}",
            Foreground = Brushes.Gray,
            FontStyle = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
        }));

    private void OnUsers(IReadOnlyList<UserView> users) => Dispatcher.UIThread.Post(() =>
    {
        _members = users;
        _userItems.Clear();
        foreach (var user in users)
        {
            var row = new TextBlock
            {
                Text = user.Display,
                Foreground = Palette.For(user.Id),
                FontWeight = user.IsSelf ? FontWeight.Bold : FontWeight.Normal,
                TextDecorations = user.IsSelf ? TextDecorations.Underline : null,
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

    /// <summary>Our unique mention handle: name#id (names alone collide — many people can be "anon").</summary>
    private string SelfHandle => $"{_chat.Username}#{_chat.SelfShortId}";

    /// <summary>True if the text contains an @-mention of our unique handle (name#id), case-insensitive.</summary>
    private bool MentionsMe(string text)
    {
        var me = SelfHandle;
        if (string.IsNullOrEmpty(_chat.Username))
            return false;

        var i = 0;
        while ((i = text.IndexOf('@', i)) >= 0)
        {
            var start = i + 1;
            if (start + me.Length <= text.Length
                && string.Compare(text, start, me, 0, me.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var after = start + me.Length;
                // The handle ends in a hex id, so a following alphanumeric would be a different handle.
                if (after == text.Length || !char.IsLetterOrDigit(text[after]))
                    return true;
            }
            i++;
        }
        return false;
    }

    /// <summary>Briefly flashes the message pane border red to draw attention to a mention.</summary>
    private async void FlashChatBox()
    {
        var normal = Brush.Parse("#888");
        for (var i = 0; i < 4; i++)
        {
            _messagesBorder.BorderBrush = Brushes.Red;
            _messagesBorder.BorderThickness = new Thickness(2);
            await Task.Delay(130);
            _messagesBorder.BorderBrush = normal;
            _messagesBorder.BorderThickness = new Thickness(1);
            await Task.Delay(130);
        }
    }

    /// <summary>Tab-completes an @-mention from the current channel members (never our own name); cycles on repeated Tab.</summary>
    private bool TryCompleteMention()
    {
        var text = _messageBox.Text ?? string.Empty;
        var caret = Math.Clamp(_messageBox.CaretIndex, 0, text.Length);
        if (caret == 0)
            return false;

        var at = text.LastIndexOf('@', caret - 1);
        if (at < 0)
            return false;
        var token = text.Substring(at + 1, caret - at - 1);
        if (token.Contains(' ') || token.Contains('\n'))
            return false;

        // We are cycling if the token is exactly what we last inserted at this same '@'.
        var continuing = _mentionAt == at && _mentionLast.Length > 0 && token.Equals(_mentionLast, StringComparison.Ordinal);
        var prefix = continuing ? _mentionPrefix : token;

        var candidates = MentionCandidates(prefix);
        if (candidates.Count == 0)
            return false;

        var index = continuing ? (_mentionIndex + 1) % candidates.Count : 0;
        var chosen = candidates[index];

        _messageBox.Text = string.Concat(text.AsSpan(0, at + 1), chosen, text.AsSpan(caret));
        _messageBox.CaretIndex = at + 1 + chosen.Length;

        _mentionAt = at;
        _mentionPrefix = prefix;
        _mentionLast = chosen;
        _mentionIndex = index;
        return true;
    }

    // Completes to the unique handle (name#id), never our own, so colliding names stay distinguishable.
    private List<string> MentionCandidates(string prefix) =>
        _members
            .Where(u => !u.IsSelf && !string.IsNullOrEmpty(u.Name))
            .Select(u => $"{u.Name}#{Short(u.Id)}")
            .Where(h => h.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
        OnSystem($"{offer.FromDisplay} is offering '{offer.FileName}' ({FormatSize(offer.Size)}).");
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
