using System.Windows;
using System.Windows.Input;

namespace Bitchat.Windows.App;

public partial class NickWindow : Window
{
    public string? Result { get; private set; }
    public bool ResetRequested { get; private set; }

    public NickWindow(string current, string defaultNick, bool pinned)
    {
        InitializeComponent();
        NickBox.Text = current;
        NickBox.SelectAll();
        HintText.Text = pinned
            ? $"закреплён в конфиге. Дефолт устройства: {defaultNick} (anon + 4 цифры MAC). «Reset» снимет закрепление."
            : $"дефолт устройства: {defaultNick} (anon + 4 цифры MAC). Введи имя и OK — будет закреплён.";
        SourceInitialized += (_, _) => DarkChrome.Apply(this);
        Loaded += (_, _) => NickBox.Focus();
    }

    private void NickBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
        else if (e.Key == Key.Escape) Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var value = NickBox.Text.Trim();
        if (value.Length == 0) return;
        Result = value;
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        ResetRequested = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
