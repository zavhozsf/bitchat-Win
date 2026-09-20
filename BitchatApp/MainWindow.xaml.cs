using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Bitchat.Windows.App;

public sealed record CommandSuggestion(string Command, string Args, string Description)
{
    public string FullText => Args.Length > 0 ? $"{Command} " : Command;
}

public partial class MainWindow : Window
{
    private readonly ChatViewModel _vm;
    private bool _suppressSuggestions;
    private ConversationVm? _lastClickConv;
    private System.Windows.Threading.DispatcherTimer? _audioProgressTimer;
    private DateTime _lastClickTime;
    private int _tripleClickCount;

    private static readonly CommandSuggestion[] Commands =
    {
        new("/peers", "", "list peers in the mesh"),
        new("/w", "", "see who's online"),
        new("/msg", "<nick|id> <text>", "private message"),
        new("/m", "<nick|id> <text>", "alias of /msg"),
        new("/nick", "<name>", "set & pin nickname"),
        new("/nick reset", "", "reset nickname to anon+MAC"),
        new("/slap", "<nickname>", "slap someone with a trout"),
        new("/hug", "<nickname>", "send someone a warm hug"),
        new("/geo", "<geohash>", "join geohash channel (Nostr)"),
        new("/here", "", "detect location & join city channel"),
        new("/join", "<geohash>", "alias of /geo"),
        new("/channels", "", "joined geohash channels"),
        new("/drop", "[#channel|nick]", "delete current/this chat"),
        new("/clear", "", "clear current conversation"),
        new("/block", "<nickname>", "block a peer"),
        new("/unblock", "<nickname>", "unblock a peer"),
        new("/links", "", "BLE link status"),
        new("/announce", "", "re-announce to the mesh"),
        new("/status", "", "mesh / ble / nostr status"),
        new("/debug", "", "toggle packet debug"),
        new("/help", "", "show commands"),
        new("/quit", "", "exit")
    };

    public MainWindow(ChatViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;

        _vm.MessagesChanged += OnMessagesChanged;
        _vm.SelectedChanged += OnSelectedChanged;
        _vm.UnreadChanged += OnUnreadChanged;
        _vm.PendingChanged += UpdatePendingPanel;
        App.Runtime.Player.PlaybackStopped += () => Dispatcher.Invoke(ResetAudioButtons);

        _audioProgressTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _audioProgressTimer.Tick += (_, _) =>
        {
            var player = App.Runtime.Player;
            if (player.IsPlaying)
            {
                foreach (var conv in _vm.Conversations)
                    foreach (var entry in conv.Messages)
                        if (entry.IsAudio && entry.IsPlaying)
                            UpdateAudioProgress(entry);
            }
        };
        _audioProgressTimer.Start();
        _vm.SelectRequested += conv =>
        {
            Dispatcher.Invoke(() => ConversationsList.SelectedItem = conv);
        };
        _vm.RefreshPeers();

        TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();
        _vm.UnreadChanged += UpdateTaskbarOverlay;
        UpdateStatusBar();
        SourceInitialized += (_, _) => DarkChrome.Apply(this);

        ConversationsList.SelectedItem = _vm.MeshConversation;
        NickText.Text = _vm.Nickname;
        Title = $"bitchat — {_vm.Nickname} ({_vm.PeerId})";
        Loaded += (_, _) => { InputBox.Focus(); };
    }

    private void OnMessagesChanged(ConversationVm conv)
    {
        if (conv == ConversationsList.SelectedItem)
        {
            if (MessagesList.Items.Count > 0)
                MessagesList.ScrollIntoView(MessagesList.Items[^1]);
        }
    }

    private void OnUnreadChanged(int total)
    {
        var baseTitle = $"bitchat — {_vm.Nickname} ({_vm.PeerId})";
        Title = total > 0 ? $"({total}) {baseTitle}" : baseTitle;
        UpdateTaskbarOverlay(total);
        UpdateStatusBar();
    }

    private void UpdateTaskbarOverlay(int total)
    {
        if (TaskbarItemInfo == null) return;
        if (total <= 0)
        {
            TaskbarItemInfo.Overlay = null;
            return;
        }
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(System.Windows.Media.Brushes.DodgerBlue, null, new Rect(0, 0, 20, 20), 4, 4);
            var text = new System.Windows.Media.FormattedText(total > 99 ? "99+" : total.ToString(),
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Segoe UI"), 13, System.Windows.Media.Brushes.White, 96);
            dc.DrawText(text, new Point(10 - text.Width / 2, 3));
        }
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(20, 20, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(visual);
        TaskbarItemInfo.Overlay = bmp;
    }

    public void UpdateStatusBar()
    {
        var runtime = App.Runtime;
        var ble = runtime?.Ble?.GetStatus() ?? "off";
        var relays = runtime?.Nostr == null ? "-" : $"{runtime.NostrRelayCount.Connected}/{runtime.NostrRelayCount.Total}";
        var peers = _vm.Conversations.Count - 1;
        var geos = string.Join(", ", _vm.Conversations.Where(c => c.IsGeohash).Select(c => c.Title));
        StatusBarText.Text = $"{ble} · nostr relays: {relays} · peers: {peers}{(geos.Length > 0 ? " · " + geos : "")}";
    }

    private void OnSelectedChanged()
    {
        var conv = _vm.Selected;
        if (conv == null) return;

        MessagesList.ItemsSource = conv.Messages;
        UpdateHeader(conv);
        InputBox.Focus();
        if (conv.Messages.Count > 0)
            Dispatcher.BeginInvoke(() => MessagesList.ScrollIntoView(conv.Messages[^1]));
    }

    private void UpdateHeader(ConversationVm conv)
    {
        if (conv.IsMesh)
            HeaderText.Text = $"{_vm.Nickname} · {_vm.PeerId} · broadcast to everyone nearby";
        else if (conv.IsGeohash)
            HeaderText.Text = $"geo channel {conv.Title} · via Nostr relays (/msg not needed — just type)";
        else
            HeaderText.Text = $"PM → {conv.Title} · {conv.Key}";
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => Send();

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (SuggestionsPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down:
                    SuggestionsList.SelectedIndex = Math.Min(SuggestionsList.SelectedIndex + 1, SuggestionsList.Items.Count - 1);
                    SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Up:
                    SuggestionsList.SelectedIndex = Math.Max(SuggestionsList.SelectedIndex - 1, 0);
                    SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    AcceptSuggestion();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    HideSuggestions();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Enter)
        {
            // Shift+Enter inserts a newline; plain Enter sends.
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            Send();
            e.Handled = true;
        }
    }

    private void Send()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0 && _vm.Pending == null) return;
        InputBox.Clear();

        if (text.StartsWith('/') && _vm.Pending == null)
            HandleCommand(text);
        else
            _vm.Send(text);
    }

    private void HandleCommand(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        switch (cmd)
        {
            case "/nick":
                if (parts.Length >= 2)
                {
                    if (parts[1] == "reset")
                        _vm.ResetNickname();
                    else
                        _vm.ChangeNickname(parts[1]);
                    NickText.Text = _vm.Nickname;
                }
                break;
            case "/geo":
                if (parts.Length >= 2)
                {
                    _vm.JoinGeohash(parts[1]);
                    _vm.AddSystem($"joining geohash #{parts[1]} — connecting to relays…");
                }
                else
                {
                    OpenGeoPicker();
                }
                break;
            case "/join":
            {
                if (parts.Length >= 2)
                {
                    var g = parts[1].TrimStart('#');
                    _vm.JoinGeohash(g);
                    _vm.AddSystem($"joining geohash #{g}…");
                }
                else _vm.AddSystem("usage: /join <geohash>");
                break;
            }
            case "/here":
                OpenGeoPicker();
                break;
            case "/w":
            case "/who":
            {
                var peers = _vm.Conversations.Where(c => !c.IsMesh && !c.IsGeohash).Select(c => c.Title).ToList();
                _vm.AddSystem(peers.Count > 0
                    ? $"online users ({peers.Count}): {string.Join(", ", peers)}"
                    : "online users: none (waiting for nearby bitchat devices)");
                break;
            }
            case "/slap":
            case "/hug":
            {
                if (parts.Length < 2)
                {
                    _vm.AddSystem($"usage: {cmd} <nickname>");
                    break;
                }
                var target = parts[1].TrimStart('@');
                var action = cmd == "/slap"
                    ? $"* {_vm.Nickname} slaps {target} around a bit with a large trout 🐟 *"
                    : $"* {_vm.Nickname} gives {target} a warm hug 🤗 *";
                _vm.Send(action);
                break;
            }
            case "/clear":
                _vm.ClearCurrentConversation();
                break;
            case "/drop":
            {
                ConversationVm? target = null;
                if (parts.Length >= 2)
                {
                    var key = parts[1].ToLowerInvariant();
                    target = key.StartsWith("geo:")
                        ? _vm.Conversations.FirstOrDefault(c => c.Key == key)
                        : key.StartsWith("#")
                            ? _vm.Conversations.FirstOrDefault(c => c.Title == key)
                            : _vm.Conversations.FirstOrDefault(c => c.Title.Equals(key, StringComparison.OrdinalIgnoreCase)) ??
                              _vm.Conversations.FirstOrDefault(c => c.Key == key || c.Key == "geo:" + key);
                }
                target ??= _vm.Selected;
                if (target == null || target.IsMesh)
                {
                    _vm.AddSystem("usage: /drop <#geohash|nickname> (or open a chat and run /drop)");
                    break;
                }
                DeleteConversation(target);
                break;
            }
            case "/channels":
            {
                var geos = _vm.Conversations.Where(c => c.IsGeohash).Select(c => c.Title).ToList();
                _vm.AddSystem(geos.Count > 0
                    ? "joined channels: " + string.Join(", ", geos)
                    : "no geohash channels joined — /here or /geo <geohash>");
                break;
            }
            case "/block":
            case "/unblock":
            {
                if (parts.Length < 2)
                {
                    var blocked = App.Runtime.Mesh.BlockedPeers;
                    _vm.AddSystem(blocked.Count > 0
                        ? "blocked: " + string.Join(", ", blocked.Select(p => p[..8]))
                        : "usage: /block <nickname>");
                    break;
                }
                var peer = _vm.Conversations.Where(c => !c.IsMesh && !c.IsGeohash)
                    .FirstOrDefault(c => c.Title.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
                if (peer == null)
                {
                    _vm.AddSystem($"no peer named '{parts[1]}'");
                    break;
                }
                if (cmd == "/block")
                {
                    App.Runtime.Mesh.BlockPeer(peer.Key);
                    _vm.RemoveConversation(peer, leaveChannel: false);
                }
                else
                {
                    App.Runtime.Mesh.UnblockPeer(peer.Key);
                    _vm.AddSystem($"peer {parts[1]} unblocked");
                }
                break;
            }
            case "/announce":
                App.Runtime.Mesh.SendAnnouncement();
                _vm.AddSystem("announcement sent");
                break;
            case "/links":
                _vm.AddSystem($"ble: {App.Runtime.Ble.GetStatus()}");
                break;
            case "/debug":
                App.Runtime.DebugEnabled = !App.Runtime.DebugEnabled;
                App.Runtime.Ble.ScanDebug = App.Runtime.DebugEnabled;
                Bitchat.Windows.Mesh.MeshEngine.DebugPackets = App.Runtime.DebugEnabled;
                _vm.AddSystem($"packet debug: {(App.Runtime.DebugEnabled ? "ON" : "OFF")}");
                break;
            case "/status":
            {
                var (conn, total) = App.Runtime.NostrRelayCount;
                _vm.AddSystem($"mesh: {_vm.PeerId} · peers: {_vm.Conversations.Count - 1 - _vm.Conversations.Count(c => c.IsGeohash)} · " +
                              $"ble: {App.Runtime.Ble.GetStatus()} · nostr: {conn}/{total} relays");
                break;
            }
            case "/help":
                _vm.AddSystem("commands: " + string.Join(", ", Commands.Select(c => c.Command).Distinct()));
                break;
            case "/peers":
                _vm.AddSystem($"{_vm.Conversations.Count - 1} peers: " +
                              string.Join(", ", _vm.Conversations.Where(c => !c.IsMesh).Select(c => c.Title)));
                break;
            case "/quit":
                QuitApp();
                break;
            default:
                _vm.AddSystem($"unknown command {parts[0]} — type / for suggestions");
                break;
        }
    }

    private void VoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Runtime.Mesh.Voice.IsRecording)
        {
            var duration = App.Runtime.Mesh.StopVoiceRecording();
            VoiceButton.Content = "🎤";
            VoiceButton.Foreground = System.Windows.Media.Brushes.White;
            if (duration > 0)
                _vm.AddSystemToCurrent($"🎙 голосовое отправлено ({duration / 1000.0:F0} с)");
        }
        else
        {
            var target = _vm.Selected ?? _vm.MeshConversation;
            if (target.IsGeohash)
            {
                _vm.AddSystem("голос в геоканалах пока не поддерживается — только Mesh и личные чаты");
                return;
            }
            App.Runtime.Mesh.SetVoiceTarget(!target.IsMesh, target.IsMesh ? null : target.Key);
            App.Runtime.Mesh.StartVoiceRecording();
            VoiceButton.Content = "⏹";
            VoiceButton.Foreground = System.Windows.Media.Brushes.IndianRed;
            _vm.AddSystemToCurrent("🎙 запись голосового… нажмите ⏹ чтобы отправить");
        }
    }

    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Прикрепить файл",
            Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|Все файлы|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            _vm.SetPending(dialog.FileName);
        }
    }

    private void ResetAudioButtons()
    {
        // Clear IsPlaying + progress on all audio entries so ▶/⏸ buttons reset.
        foreach (var conv in _vm.Conversations)
            foreach (var entry in conv.Messages)
                if (entry.IsAudio)
                {
                    entry.IsPlaying = false;
                    entry.PlayPosition = 0;
                }
    }

    private void PendingRemove_Click(object sender, RoutedEventArgs e) => _vm.ClearPending();

    private void UpdatePendingPanel()
    {
        Dispatcher.Invoke(() =>
        {
            var pending = _vm.Pending;
            if (pending == null)
            {
                PendingPanel.Visibility = Visibility.Collapsed;
                return;
            }
            PendingPanel.Visibility = Visibility.Visible;
            PendingText.Text = pending.IsImage ? pending.Name : $"📎 {pending.Name} ({pending.SizeBytes / 1024.0:F0} KB)";
            if (pending.IsImage)
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 88;
                    bmp.UriSource = new Uri(pending.Path);
                    bmp.EndInit();
                    PendingThumb.Source = bmp;
                    PendingThumb.Visibility = Visibility.Visible;
                }
                catch
                {
                    PendingThumb.Source = null;
                    PendingThumb.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                PendingThumb.Source = null;
                PendingThumb.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void MediaImage_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatEntry entry } && entry.FilePath != null)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = entry.FilePath,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private void AudioPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ChatEntry entry && entry.FilePath != null)
        {
            var player = App.Runtime.Player;
            player.Toggle(entry.FilePath);
            entry.IsPlaying = player.IsPlaying && player.CurrentFile == entry.FilePath;
            UpdateAudioProgress(entry);
        }
    }

    private void UpdateAudioProgress(ChatEntry entry)
    {
        var player = App.Runtime.Player;
        var duration = player.Duration;
        var position = player.Position;
        entry.PlayPosition = duration.TotalSeconds > 0 ? position.TotalSeconds / duration.TotalSeconds : 0;
        entry.PlayTimeText = duration.TotalSeconds > 0
            ? $"{position:mm\\:ss} / {duration:mm\\:ss}"
            : "";
    }

    private void GeoButton_Click(object sender, RoutedEventArgs e) => OpenGeoPicker();

    private void OpenGeoPicker()
    {
        var dialog = new LocationPickerWindow { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _vm.JoinGeohash(dialog.Result);
            _vm.AddSystem($"joining geohash #{dialog.Result}…");
        }
    }

    private void NickButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NickWindow(_vm.Nickname, App.Runtime.DefaultNick, NickStore.LoadNickname() != null) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (dialog.ResetRequested)
        {
            _vm.ResetNickname();
        }
        else if (dialog.Result != null)
        {
            _vm.ChangeNickname(dialog.Result);
        }
        NickText.Text = _vm.Nickname;
    }

    private void ConversationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationsList.SelectedItem is ConversationVm conv)
            _vm.Select(conv);
    }

    // ---- Triple-click deletes a conversation (like bitchat mobile) ----

    private void ConversationsList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ConversationsList.SelectedItem is not ConversationVm conv) return;
        var now = DateTime.UtcNow;
        _tripleClickCount = conv == _lastClickConv && (now - _lastClickTime).TotalMilliseconds < 700
            ? _tripleClickCount + 1
            : 1;
        _lastClickConv = conv;
        _lastClickTime = now;

        if (_tripleClickCount < 3) return;
        _tripleClickCount = 0;
        DeleteConversation(conv);
        e.Handled = true;
    }

    private void DeleteConversation(ConversationVm conv)
    {
        if (conv.IsMesh)
        {
            _vm.AddSystem("общий чат Mesh удалить нельзя");
            return;
        }
        var what = conv.IsGeohash ? $"геоканал {conv.Title} (отписаться от релеев)" : $"чат с {conv.Title}";
        var choice = MessageBox.Show($"Удалить {what}?", "bitchat", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes) return;
        _vm.RemoveConversation(conv, leaveChannel: conv.IsGeohash);
        _vm.AddSystem(conv.IsGeohash ? $"геоканал {conv.Title} удалён" : $"чат с {conv.Title} удалён (вернётся при новом сообщении)");
    }

    // ---- Copy context menu (right-click a message) ----

    private void MessagesList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d != null && d is not ListBoxItem)
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        if (d is not ListBoxItem item || item.DataContext is not ChatEntry entry) return;

        var menu = new ContextMenu();
        var copyText = new MenuItem { Header = "Копировать текст" };
        copyText.Click += (_, _) =>
        {
            try { Clipboard.SetText(entry.Kind == ChatKind.File ? entry.FileBadge : entry.Content); }
            catch { }
        };
        menu.Items.Add(copyText);

        if (!string.IsNullOrEmpty(entry.FilePath))
        {
            var copyPath = new MenuItem { Header = "Копировать путь к файлу" };
            copyPath.Click += (_, _) =>
            {
                try { Clipboard.SetText(entry.FilePath!); } catch { }
            };
            menu.Items.Add(copyPath);
            var openItem = new MenuItem { Header = "Открыть файл" };
            openItem.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = entry.FilePath!,
                        UseShellExecute = true
                    });
                }
                catch { }
            };
            menu.Items.Add(openItem);
        }

        menu.PlacementTarget = MessagesList;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---- Command suggestions (like bitchat mobile: type "/" to see commands) ----

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        AutoResizeInput();
        if (_suppressSuggestions) { HideSuggestions(); return; }
        var text = InputBox.Text;
        if (text.Length == 0 || text[0] != '/')
        {
            HideSuggestions();
            return;
        }

        var spaceTyped = text.Contains(' ');
        var token = text.Split(' ')[0].ToLowerInvariant();
        var matches = Commands
            .Where(cmd => spaceTyped
                ? text.StartsWith(cmd.Command + " ", StringComparison.OrdinalIgnoreCase) ||
                  (cmd.Args.Length > 0 && cmd.Command.Equals(token, StringComparison.OrdinalIgnoreCase))
                : cmd.Command.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (spaceTyped || matches.Count == 0)
        {
            // While typing arguments the popup stays out of the way.
            HideSuggestions();
            return;
        }

        SuggestionsList.ItemsSource = matches;
        SuggestionsList.SelectedIndex = 0;
        SuggestionsPopup.IsOpen = true;
    }

    private void AutoResizeInput()
    {
        // Grow the input with its content, starting at Send-button height (46) up to ~6 lines.
        const double lineHeight = 20;
        const double minHeight = 46;
        const double maxHeight = 130;

        var lines = InputBox.Text.Count(c => c == '\n') + 1;
        var charsPerLine = Math.Max(20, InputBox.ActualWidth / 8.0);
        var wrappedLines = (int)Math.Ceiling(InputBox.Text.Length / charsPerLine);
        var totalLines = Math.Max(lines, wrappedLines);

        var desired = Math.Max(minHeight, totalLines * lineHeight + 16);
        InputBox.Height = Math.Min(desired, maxHeight);
    }

    private void HideSuggestions()
    {
        SuggestionsPopup.IsOpen = false;
    }

    private void AcceptSuggestion()
    {
        if (SuggestionsList.SelectedItem is CommandSuggestion s)
        {
            _suppressSuggestions = true;
            InputBox.Text = s.FullText;
            InputBox.CaretIndex = InputBox.Text.Length;
            _suppressSuggestions = false;
        }
        HideSuggestions();
        InputBox.Focus();
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    private void Suggestions_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                AcceptSuggestion();
                e.Handled = true;
                break;
            case Key.Escape:
                HideSuggestions();
                InputBox.Focus();
                e.Handled = true;
                break;
        }
    }

    private void Suggestions_MouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSuggestion();

    private void QuitApp()
    {
        Application.Current.Shutdown();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Minimize to tray instead of exiting.
        e.Cancel = true;
        Hide();
        _vm.AddSystem("окно свёрнуто в трей (иконка у часов) — там же Quit");
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
    }
}
