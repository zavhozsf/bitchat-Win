using WpfBrushes = System.Windows.Media.Brushes;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bitchat.Windows;
using Bitchat.Windows.Mesh;
using Hardcodet.Wpf.TaskbarNotification;

namespace Bitchat.Windows.App;

public partial class App : Application
{
    private static Mutex? _mutex;
    public static BitchatRuntime Runtime { get; private set; } = null!;
    private TaskbarIcon _tray = null!;
    private MainWindow _main = null!;
    private ChatViewModel _vm = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            LogCrash(ex.Exception);
            MessageBox.Show(ex.Exception.Message, "bitchat error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash(ex.ExceptionObject as Exception);

        _mutex = new Mutex(true, "Bitchat.Windows.Gui", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("bitchat уже запущен - смотри трей у часов (чёрная иконка b).", "bitchat",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        Log("=== start ===");

        var args = e.Args;
        string? nick = null, identity = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--nick" or "-n") nick = args[i + 1];
            if (args[i] == "--identity") identity = args[i + 1];
        }
        var debug = args.Contains("--debug") || args.Contains("--scan-debug");
        var resolved = await BitchatRuntime.ResolveNicknameAsync(nick);

        Runtime = new BitchatRuntime(resolved, identity, debug);
        _vm = new ChatViewModel(Runtime);

        _tray = (TaskbarIcon)Resources["TrayIcon"];
        _tray.IconSource = LoadAppIcon();
        _tray.ToolTipText = $"bitchat - {_vm.Nickname}";
        _tray.ContextMenu = BuildTrayMenu();

        _main = new MainWindow(_vm) { Owner = null };
        _main.Show();

        Runtime.Mesh.MessageReceived += OnMessage;
        Runtime.Mesh.SystemMessage += msg => _vm.AddSystem(msg);
        Runtime.Mesh.PeersChanged += _ => _vm.RefreshPeers();
        Runtime.NostrMessageReceived += (geohash, nickname, content) => _vm.AddNostrMessage(geohash, nickname, content);
        Runtime.SystemLog += msg => _vm.AddSystem(msg);
        Runtime.NostrRelayStatus += (connected, total) => _vm.AddSystem($"nostr relays: {connected}/{total} connected");
        Runtime.NostrJoined += geohash =>
        {
            _vm.EnsureGeohashVisible(geohash);
            _vm.AddSystem($"geohash #{geohash} подключён (автопродление из config)");
        };
        MeshEngine.SetStaticLog(msg => _vm.AddSystem(msg));
        MeshEngine.DebugPackets = debug;
        Runtime.Ble.ScanDebug = debug;

        _vm.TitleChanged += () =>
        {
            _tray.ToolTipText = $"bitchat - {Runtime.Nickname} ({Runtime.Noise.MyPeerId})";
        };

        _vm.UnreadChanged += total =>
        {
            var baseText = $"bitchat - {Runtime.Nickname} ({Runtime.Noise.MyPeerId})";
            _tray.ToolTipText = total > 0 ? $"({total} new) {baseText}" : baseText;
        };

        var ok = await Runtime.StartAsync();
        _vm.AddSystem(ok
            ? "BLE mesh running - nearby bitchat devices will appear automatically"
            : "BLE transport failed to start. Check that Bluetooth is on.");

        if (ok)
            _tray.ShowBalloonTip("bitchat работает", "Окно можно закрыть - свернётся в трей у часов. Сообщения придут уведомлением.", BalloonIcon.None);
        else
            _tray.ShowBalloonTip("bitchat", "Bluetooth не запустился - проверь, включён ли адаптер.", BalloonIcon.Warning);
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open bitchat" };
        open.Click += (_, _) => ShowMainWindow();
        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => Quit();
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        return menu;
    }

    private void OnMessage(ChatMessage message)
    {
        // Events arrive on WinRT threadpool threads - marshal everything to the UI thread.
        Dispatcher.BeginInvoke(() =>
        {
            _vm.AddMessage(message);

            var hidden = _main == null || _main.WindowState != WindowState.Normal || !_main.IsActive;
            if (!hidden) return;

            if (message.IsFromMe) return;
            var title = message.IsPrivate ? $"bitchat PM from {message.Sender}" : $"bitchat - {message.Sender}";
            _tray.ShowBalloonTip(title, message.Content, BalloonIcon.None);
        });
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "bitchat-gui.log"),
                $"[{DateTimeOffset.Now:HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    private static void Log(string line)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "bitchat-gui.log"),
                $"[{DateTimeOffset.Now:HH:mm:ss}] {line}\n");
        }
        catch { }
    }

    private void TrayIcon_TrayMouseDoubleClick(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void ShowMainWindow()
    {
        _main.Show();
        _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    private void Quit()
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Runtime.Stop(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    /// <summary>Official bitchat icon (from the mobile clients) for the tray.</summary>
    private static ImageSource LoadAppIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/icon32.png");
        return System.Windows.Media.Imaging.BitmapFrame.Create(uri);
    }
}
