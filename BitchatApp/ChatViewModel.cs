using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Bitchat.Windows;
using Bitchat.Windows.Mesh;

namespace Bitchat.Windows.App;

public enum ChatKind { Broadcast, Private, System, Geohash, File }

public sealed class ChatEntry : INotifyPropertyChanged
{
    public DateTimeOffset Time { get; init; }
    public string Header { get; init; } = "";
    public string Content { get; init; } = "";
    public ChatKind Kind { get; init; }
    public bool IsOutgoing { get; init; }
    public string? MessageId { get; init; }
    public string? FilePath { get; init; }
    public bool IsImage => FilePath != null &&
        (FilePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
         FilePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
         FilePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
         FilePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
         FilePath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
         FilePath.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));
    public string FileBadge => FilePath == null ? "" : $"📎 {Content} ({FileSizeBytes / 1024.0:F0} KB)";
    public bool IsAudio => FilePath != null && Bitchat.Windows.Voice.AudioPlayer.IsAudioFile(FilePath);
    public long FileSizeBytes { get; init; }

    private string? _status;
    public string? Status
    {
        get => _status;
        set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set { _isPlaying = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying))); }
    }

    private double _playPosition;
    public double PlayPosition
    {
        get => _playPosition;
        set { _playPosition = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayPosition))); }
    }

    private string _playTimeText = "";
    public string PlayTimeText
    {
        get => _playTimeText;
        set { _playTimeText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlayTimeText))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ConversationVm : INotifyPropertyChanged
{
    private string _title;
    private int _unread;

    public string Key { get; init; } = "";
    public bool IsMesh { get; init; }
    public bool IsGeohash { get; init; }
    public ObservableCollection<ChatEntry> Messages { get; } = new();

    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title))); }
    }

    public int Unread
    {
        get => _unread;
        set
        {
            _unread = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Unread)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasUnread)));
        }
    }

    public bool HasUnread => _unread > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ChatViewModel
{
    private readonly BitchatRuntime _runtime;
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly Dictionary<string, ChatEntry> _entriesByMessageId = new();
    private readonly HashSet<string> _hiddenConversations = new();

    public ObservableCollection<ConversationVm> Conversations { get; } = new();
    public ConversationVm? Selected { get; private set; }

    public string Nickname => _runtime.Nickname;
    public string PeerId => _runtime.Noise.MyPeerId;

    public event Action<ConversationVm>? MessagesChanged;
    public event Action? SelectedChanged;
    public event Action? TitleChanged;
    public event Action<int>? UnreadChanged;
    public event Action? PendingChanged;

    public sealed record PendingFile(string Path, string Name, long SizeBytes, bool IsImage);
    public PendingFile? Pending { get; private set; }

    public void SetPending(string filePath)
    {
        var info = new FileInfo(filePath);
        Pending = new PendingFile(
            filePath,
            info.Name,
            info.Length,
            info.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
            info.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            info.Extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            info.Extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
            info.Extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
            info.Extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase));
        PendingChanged?.Invoke();
    }

    public void ClearPending()
    {
        Pending = null;
        PendingChanged?.Invoke();
    }

    private void RaiseUnread()
    {
        UnreadChanged?.Invoke(Conversations.Sum(c => c.Unread));
    }

    public ChatViewModel(BitchatRuntime runtime)
    {
        _runtime = runtime;
        var mesh = new ConversationVm { Key = "mesh", IsMesh = true, Title = "Mesh" };
        Conversations.Add(mesh);
        Selected = mesh;

        _runtime.Mesh.OutgoingStatusChanged += (messageId, peerId, status) =>
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (_entriesByMessageId.TryGetValue(messageId, out var entry))
                    entry.Status = status switch
                    {
                        "delivered" => "✓✓",
                        "read" => "✓✓ прочитано",
                        _ => null
                    };
            });
        };
    }

    public ConversationVm MeshConversation => Conversations[0];

    public void Select(ConversationVm conversation)
    {
        Selected = conversation;
        conversation.Unread = 0;
        SelectedChanged?.Invoke();
        MessagesChanged?.Invoke(conversation);
        RaiseUnread();

        if (!conversation.IsMesh)
            _runtime.Mesh.MarkIncomingRead(conversation.Key);
    }

    // ---- Incoming events (background threads; marshaled to UI) ----

    public void AddMessage(ChatMessage message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (message.FilePath != null)
            {
                var peerId = message.SenderPeerId;
                var title = message.IsFromMe ? TitleForPeer(peerId) : NickOrId(message.SenderPeerId, message.Sender);
                var conv = message.IsPrivate
                    ? EnsureConversation(peerId, title, force: !message.IsFromMe)
                    : MeshConversation;
                if (conv == null) return;
                Append(conv, new ChatEntry
                {
                    Time = message.Timestamp,
                    Header = message.IsPrivate
                        ? (message.IsFromMe ? "me → " + ShortPeer(message.SenderPeerId) : message.Sender + " (PM)")
                        : message.IsFromMe ? "me" : message.Sender,
                    Content = message.Content,
                    Kind = ChatKind.File,
                    IsOutgoing = message.IsFromMe,
                    FilePath = message.FilePath,
                    FileSizeBytes = message.FileSizeBytes,
                    MessageId = message.MessageId
                });

                if (message.IsFromMe && message.MessageId != null)
                    _entriesByMessageId[message.MessageId] = new ChatEntry
                    {
                        Time = message.Timestamp, Header = "me", Content = message.Content,
                        Kind = ChatKind.File, IsOutgoing = true, FilePath = message.FilePath,
                        MessageId = message.MessageId
                    };
                return;
            }

            if (message.IsPrivate)
            {
                var peerId = message.SenderPeerId;
                var title = message.IsFromMe ? TitleForPeer(peerId) : NickOrId(message.SenderPeerId, message.Sender);
                var conv = EnsureConversation(peerId, title, force: !message.IsFromMe);
                if (conv == null) return;

                var header = message.IsFromMe
                    ? "me → " + ShortPeer(peerId)
                    : message.Sender + " (PM)";
                var entry = new ChatEntry
                {
                    Time = message.Timestamp,
                    Header = header,
                    Content = message.Content,
                    Kind = ChatKind.Private,
                    IsOutgoing = message.IsFromMe,
                    MessageId = message.MessageId,
                    Status = message.IsFromMe ? "✓" : null
                };
                Append(conv, entry);

                if (message.IsFromMe && message.MessageId != null)
                    _entriesByMessageId[message.MessageId] = entry;
            }
            else if (message.FilePath != null)
            {
                var conv = message.IsPrivate
                    ? EnsureConversation(message.SenderPeerId, message.IsFromMe ? TitleForPeer(message.SenderPeerId) : NickOrId(message.SenderPeerId, message.Sender), force: !message.IsFromMe)
                    : MeshConversation;
                if (conv == null) return;
                Append(conv, new ChatEntry
                {
                    Time = message.Timestamp,
                    Header = message.IsPrivate
                        ? (message.IsFromMe ? "me → " + ShortPeer(message.SenderPeerId) : message.Sender + " (PM)")
                        : message.IsFromMe ? "me" : message.Sender,
                    Content = message.Content,
                    Kind = ChatKind.File,
                    IsOutgoing = message.IsFromMe,
                    FilePath = message.FilePath,
                    FileSizeBytes = message.FileSizeBytes
                });
            }
            else if (message.Content.StartsWith("🎙"))
            {
                var conv = message.IsPrivate
                    ? EnsureConversation(message.SenderPeerId, message.IsFromMe ? TitleForPeer(message.SenderPeerId) : NickOrId(message.SenderPeerId, message.Sender), force: !message.IsFromMe)
                    : MeshConversation;
                if (conv == null) return;
                Append(conv, new ChatEntry
                {
                    Time = message.Timestamp,
                    Header = message.IsPrivate
                        ? (message.IsFromMe ? "me → " + ShortPeer(message.SenderPeerId) : message.Sender + " (PM)")
                        : message.IsFromMe ? "me" : message.Sender,
                    Content = message.Content,
                    Kind = ChatKind.Broadcast,
                    IsOutgoing = message.IsFromMe
                });
            }
            else
            {
                var header = message.IsFromMe ? "me" : message.Sender;
                Append(MeshConversation, new ChatEntry
                {
                    Time = message.Timestamp,
                    Header = header,
                    Content = message.Content,
                    Kind = ChatKind.Broadcast,
                    IsOutgoing = message.IsFromMe
                });
            }

            // Auto-mark read: incoming PM arrived while its conversation is open.
            if (!message.IsFromMe && message.IsPrivate &&
                Selected is { } sel && sel.Key == message.SenderPeerId)
            {
                _runtime.Mesh.MarkIncomingRead(sel.Key);
            }
        });
    }

    public void AddNostrMessage(string geohash, string? nickname, string content)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var key = "geo:" + geohash;
            var conv = Conversations.FirstOrDefault(c => c.Key == key);
            if (conv == null)
            {
                conv = new ConversationVm { Key = key, IsGeohash = true, Title = "#" + geohash };
                Conversations.Add(conv);
            }
            Append(conv, new ChatEntry
            {
                Time = DateTimeOffset.Now,
                Header = string.IsNullOrWhiteSpace(nickname) ? "nostr" : nickname,
                Content = content,
                Kind = ChatKind.Geohash
            });
        });
    }

    public void AddSystem(string text)
    {
        _dispatcher.BeginInvoke(() =>
        {
            Append(MeshConversation, new ChatEntry
            {
                Time = DateTimeOffset.Now,
                Header = "•",
                Content = text,
                Kind = ChatKind.System
            });
        });
    }

    public void AddSystemToCurrent(string text)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var conv = Selected ?? MeshConversation;
            Append(conv, new ChatEntry
            {
                Time = DateTimeOffset.Now,
                Header = "•",
                Content = text,
                Kind = ChatKind.System
            });
        });
    }

    public void RefreshPeers()
    {
        var snapshot = _runtime.Mesh.GetPeersSnapshot();
        _dispatcher.BeginInvoke(() =>
        {
            foreach (var peer in snapshot)
            {
                var conv = EnsureConversation(peer.PeerId, peer.Nickname);
                if (conv == null) continue;
                if (!conv.IsMesh) conv.Title = peer.Nickname;
            }
        });
    }

    public void Send(string text)
    {
        var hasText = !string.IsNullOrWhiteSpace(text);
        if (Pending == null && !hasText) return;

        var target = Selected ?? MeshConversation;

        // Attached file goes out first (like the mobile clients).
        if (Pending != null)
        {
            if (target.IsGeohash)
            {
                AddSystem("файлы в геоканалах пока не поддерживаются - только Mesh и личные чаты");
            }
            else
            {
                if (target.IsMesh)
                    _runtime.Mesh.SendFileBroadcast(Pending.Path);
                else
                    _runtime.Mesh.SendFilePrivate(target.Key, Pending.Path);
                ClearPending();
            }
        }

        if (!hasText) return;

        if (target.IsGeohash)
        {
            _runtime.SendGeohash(target.Key["geo:".Length..], text);
            Append(target, new ChatEntry
            {
                Time = DateTimeOffset.Now,
                Header = "me",
                Content = text,
                Kind = ChatKind.Geohash,
                IsOutgoing = true
            });
        }
        else if (target.IsMesh)
            _runtime.Mesh.SendBroadcast(text);
        else
            _runtime.Mesh.SendPrivate(text, target.Key);
    }

    public void JoinGeohash(string geohash)
    {
        _ = _runtime.JoinGeohashAsync(geohash);
        var conv = EnsureGeohashConversation(geohash);
        _dispatcher.BeginInvoke(() =>
        {
            var view = Conversations.FirstOrDefault(c => c.Key == conv.Key);
            if (view != null) SelectFromUi(view);
        });
    }

    private ConversationVm EnsureGeohashConversation(string geohash)
    {
        var key = "geo:" + geohash;
        var conv = Conversations.FirstOrDefault(c => c.Key == key);
        if (conv == null)
        {
            conv = new ConversationVm { Key = key, IsGeohash = true, Title = "#" + geohash };
            _dispatcher.BeginInvoke(() =>
            {
                if (!Conversations.Any(c => c.Key == key))
                    Conversations.Add(conv);
            });
        }
        return conv;
    }

    public event Action<ConversationVm>? SelectRequested;

    private void SelectFromUi(ConversationVm conv)
    {
        SelectRequested?.Invoke(conv);
    }

    public void ClearCurrentConversation()
    {
        if (Selected == null) return;
        _dispatcher.BeginInvoke(() =>
        {
            Selected.Messages.Clear();
            AddSystem($"чат {Selected.Title} очищен (история у пира сохранена)");
        });
    }

    public void SendFile(string filePath)
    {
        var target = Selected ?? MeshConversation;
        if (target.IsGeohash)
        {
            AddSystem("файлы в геоканалах пока не поддерживаются - только Mesh и личные чаты");
            return;
        }
        if (target.IsMesh)
            _runtime.Mesh.SendFileBroadcast(filePath);
        else
            _runtime.Mesh.SendFilePrivate(target.Key, filePath);
    }

    public void ChangeNickname(string nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname)) return;
        _runtime.SetNickname(nickname);
        TitleChanged?.Invoke();
        AddSystem($"nickname set to {_runtime.Nickname} (pinned) - re-announced");
    }

    public void ResetNickname()
    {
        _runtime.ResetNickname();
        TitleChanged?.Invoke();
        AddSystem($"nickname reset to device default: {_runtime.Nickname} - re-announced");
    }

    // ---- helpers ----

    private ConversationVm EnsureConversation(string peerId, string title, bool force = false)
    {
        var conv = Conversations.FirstOrDefault(c => c.Key == peerId);
        if (conv == null)
        {
            if (!force && _hiddenConversations.Contains(peerId)) return null!;
            conv = new ConversationVm { Key = peerId, Title = string.IsNullOrWhiteSpace(title) ? ShortPeer(peerId) : title };
            var index = 1 + Conversations.Skip(1).Count(c => string.Compare(c.Title, conv.Title, StringComparison.OrdinalIgnoreCase) < 0);
            Conversations.Insert(Math.Min(index, Conversations.Count), conv);
        }
        return conv;
    }

    /// <summary>Remove a conversation from the list (triple-click delete). Geohash channels also unsubscribe.</summary>
    public void RemoveConversation(ConversationVm conv, bool leaveChannel)
    {
        _hiddenConversations.Add(conv.Key);
        _dispatcher.BeginInvoke(() =>
        {
            Conversations.Remove(conv);
            if (Selected == conv)
            {
                Selected = null;
                Select(MeshConversation);
            }
            RaiseUnread();
        });
        if (leaveChannel && conv.IsGeohash)
            _runtime.LeaveGeohash(conv.Key["geo:".Length..]);
    }

    /// <summary>Restore a geohash conversation on startup (автопродление).</summary>
    public void EnsureGeohashVisible(string geohash)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var key = "geo:" + geohash;
            if (Conversations.Any(c => c.Key == key)) return;
            Conversations.Add(new ConversationVm { Key = key, IsGeohash = true, Title = "#" + geohash });
        });
    }

    private string TitleForPeer(string peerId)
    {
        var peer = _runtime.Mesh.GetPeersSnapshot().FirstOrDefault(p => p.PeerId == peerId);
        return peer?.Nickname ?? ShortPeer(peerId);
    }

    private static string NickOrId(string peerId, string fallback) =>
        string.IsNullOrWhiteSpace(fallback) ? ShortPeer(peerId) : fallback;

    private static string ShortPeer(string peerId) => peerId.Length > 8 ? peerId[..8] : peerId;

    private void Append(ConversationVm conv, ChatEntry entry)
    {
        conv.Messages.Add(entry);
        if (conv != Selected) conv.Unread++;
        MessagesChanged?.Invoke(conv);
        RaiseUnread();
    }
}
