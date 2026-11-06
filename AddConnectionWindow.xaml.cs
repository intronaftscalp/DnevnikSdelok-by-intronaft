using DnevnikSdelok.Data;
using DnevnikSdelok.Models;
using DnevnikSdelok.Security;
using DnevnikSdelok.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DnevnikSdelok;

public partial class AddConnectionWindow : Window
{
    private readonly JournalDatabase _database;
    private bool _saving;

    public AddConnectionWindow(JournalDatabase database)
    {
        InitializeComponent();
        _database = database;
    }

    public void ApplyTheme(DashboardPreferences preferences)
    {
        Background = Brush(preferences.WindowBackground);
        Foreground = Brush(preferences.Foreground);
        Resources["PanelBrush"] = Brush(preferences.WindowBackground);
        Resources["BorderBrush"] = Brush(preferences.ChartGridColor);
        Resources["MutedBrush"] = Brush(preferences.MutedForeground);
        Resources["AccentBrush"] = Brush(preferences.AccentColor);
        Resources["ForegroundBrush"] = Brush(preferences.Foreground);
        Resources["ControlBrush"] = Brush(Blend(preferences.WindowBackground, preferences.AccentColor, 0.035));
        Resources["ControlHoverBrush"] = Brush(Blend(preferences.WindowBackground, preferences.AccentColor, 0.08));
    }

    private void InputChanged(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled =
            !string.IsNullOrWhiteSpace(AccountNameBox.Text) &&
            !string.IsNullOrWhiteSpace(ApiKeyBox.Text) &&
            !string.IsNullOrWhiteSpace(ApiSecretBox.Password) &&
            !_saving;
    }

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (MarketBox.SelectedItem is not ComboBoxItem selectedMarket)
        {
            return;
        }

        var market = selectedMarket.Tag?.ToString() ?? "";
        var accountName = AccountNameBox.Text.Trim();
        var apiKey = ApiKeyBox.Text.Trim();
        var apiSecret = ApiSecretBox.Password;
        _saving = true;
        ConnectButton.IsEnabled = false;
        StatusText.Text = "Проверяю доступ к Binance…";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(154, 168, 181));

        try
        {
            decimal currentBalance;
            using (var client = new BinanceApiClient(apiKey, apiSecret, market))
            {
                await client.TestConnectionAsync(CancellationToken.None);
                currentBalance = await client.GetCurrentBalanceAsync(CancellationToken.None);
            }

            _database.AddConnection(new ApiConnection
            {
                Name = accountName,
                Market = market,
                ProtectedApiKey = WindowsSecretProtection.Protect(apiKey),
                ProtectedApiSecret = WindowsSecretProtection.Protect(apiSecret),
                CurrentBalance = currentBalance
            });
            ApiKeyBox.Clear();
            ApiSecretBox.Clear();
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Не удалось проверить и сохранить ключ: {exception.Message}";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 139, 128));
            _saving = false;
            InputChanged(this, new RoutedEventArgs());
        }
    }

    private void CancelClick(object sender, RoutedEventArgs e) => Close();

    private static string Blend(string first, string second, double amount)
    {
        var a = (Color)ColorConverter.ConvertFromString(first);
        var b = (Color)ColorConverter.ConvertFromString(second);
        return $"#{(byte)(a.R * (1 - amount) + b.R * amount):X2}" +
               $"{(byte)(a.G * (1 - amount) + b.G * amount):X2}" +
               $"{(byte)(a.B * (1 - amount) + b.B * amount):X2}";
    }

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
