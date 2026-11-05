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
            using (var client = new BinanceApiClient(apiKey, apiSecret, market))
            {
                await client.TestConnectionAsync(CancellationToken.None);
            }

            _database.AddConnection(new ApiConnection
            {
                Name = accountName,
                Market = market,
                ProtectedApiKey = WindowsSecretProtection.Protect(apiKey),
                ProtectedApiSecret = WindowsSecretProtection.Protect(apiSecret)
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
}
