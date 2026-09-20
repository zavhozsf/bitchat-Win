using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Bitchat.Windows.App;

public partial class LocationPickerWindow : Window
{
    public string? Result { get; private set; }
    private (double Lat, double Lon, string? City, string? Country)? _detected;

    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6));

    public LocationPickerWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkChrome.Apply(this);
        Loaded += async (_, _) => await DetectAsync();
    }

    private async void Detect_Click(object sender, RoutedEventArgs e) => await DetectAsync();

    private async Task DetectAsync()
    {
        DetectButton.IsEnabled = false;
        try
        {
            DetectedText.Foreground = Gray;
            DetectedText.Text = "detecting…";
            var result = await LocationService.DetectAsync(ScopeFromUi());
            if (result == null)
            {
                DetectedText.Text = "не удалось определить — введи геохэш вручную";
                return;
            }
            _detected = (result.Latitude, result.Longitude, result.City, result.Country);
            var place = result.City != null
                ? $"{result.City}{(result.Country != null ? ", " + result.Country : "")}"
                : $"{result.Latitude:F3}, {result.Longitude:F3}";
            DetectedText.Text = $"📍 {place} → {result.Geohash}";
            DetectedText.Foreground = Brushes.LightGreen;
            GeohashBox.Text = result.Geohash;
        }
        catch (Exception ex)
        {
            DetectedText.Text = $"error: {ex.Message}";
        }
        finally
        {
            DetectButton.IsEnabled = true;
        }
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        var value = GeohashBox.Text.Trim().ToLowerInvariant();
        if (value.Length == 0)
        {
            if (_detected == null)
            {
                await DetectAsync();
                value = GeohashBox.Text.Trim().ToLowerInvariant();
            }
            else
            {
                value = GeoUtils.ChannelGeohash(_detected.Value.Lat, _detected.Value.Lon, ScopeFromUi());
            }
            if (value.Length == 0) return;
        }
        Result = value;
        DialogResult = true;
    }

    private void GeohashBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Join_Click(sender, e);
        else if (e.Key == Key.Escape) Close();
    }

    private ChannelScope ScopeFromUi() =>
        ScopeBlock.IsChecked == true ? ChannelScope.Block
        : ScopeNeighborhood.IsChecked == true ? ChannelScope.Neighborhood
        : ChannelScope.City;

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
