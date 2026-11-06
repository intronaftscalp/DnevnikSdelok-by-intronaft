using DnevnikSdelok.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DnevnikSdelok;

public partial class DashboardSettingsWindow : Window
{
    private DashboardPreferences _preferences;
    private readonly Func<DashboardPreferences, bool> _save;
    private readonly Action<string> _executeAction;
    private bool _isBuilding;

    public DashboardSettingsWindow(
        DashboardPreferences preferences,
        Func<DashboardPreferences, bool> save,
        Action<string> executeAction)
    {
        InitializeComponent();
        _preferences = preferences;
        _save = save;
        _executeAction = executeAction;
        Background = Brush(preferences.WindowBackground);
        Resources["PanelBrush"] = Brush(preferences.WindowBackground);
        Resources["BorderBrush"] = Brush(preferences.ChartGridColor);
        Resources["MutedBrush"] = Brush(preferences.MutedForeground);
        Resources["AccentBrush"] = Brush(preferences.AccentColor);
        Resources["ForegroundBrush"] = Brush(preferences.Foreground);
        SettingsTabs.Background = Brush(preferences.WindowBackground);
        Rebuild();
    }

    private void Rebuild()
    {
        _isBuilding = true;
        GlobalColorsPanel.Children.Clear();
        AddColorEditor(GlobalColorsPanel, "Фон программы", () => _preferences.WindowBackground,
            value => _preferences.WindowBackground = value, 150);
        AddColorEditor(GlobalColorsPanel, "Основной текст", () => _preferences.Foreground,
            value => _preferences.Foreground = value, 135);
        AddColorEditor(GlobalColorsPanel, "Подписи", () => _preferences.MutedForeground,
            value => _preferences.MutedForeground = value, 135);
        AddColorEditor(GlobalColorsPanel, "Цвет акцента", () => _preferences.AccentColor,
            value => _preferences.AccentColor = value, 130);
        AddColorEditor(GlobalColorsPanel, "Линии графика", () => _preferences.ChartGridColor,
            value => _preferences.ChartGridColor = value, 145);
        AddColorEditor(GlobalColorsPanel, "Свеча роста", () => _preferences.ChartBullColor,
            value => _preferences.ChartBullColor = value, 125);
        AddColorEditor(GlobalColorsPanel, "Свеча падения", () => _preferences.ChartBearColor,
            value => _preferences.ChartBearColor = value, 135);
        TimeZoneSelector.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        TimeZoneSelector.SelectedValue = _preferences.TimeZoneId;

        BuildWidgets();
        BuildColumns();
        _isBuilding = false;
    }

    private void BuildWidgets()
    {
        WidgetsPanel.Children.Clear();
        var widgets = _preferences.Widgets.OrderBy(item => item.OrderIndex).ToList();
        foreach (var widget in widgets)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(22, 32, 42)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(41, 55, 68)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var panel = new StackPanel();
            var header = new DockPanel();
            var up = SmallButton("↑", () => MoveWidget(widget, -1), 34);
            var down = SmallButton("↓", () => MoveWidget(widget, 1), 34);
            DockPanel.SetDock(down, Dock.Right);
            DockPanel.SetDock(up, Dock.Right);
            header.Children.Add(down);
            header.Children.Add(up);
            var check = new CheckBox
            {
                Content = widget.Title,
                IsChecked = widget.Visible,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush(widget.Foreground)
            };
            check.Checked += (_, _) => UpdateWidget(widget, item => item.Visible = true);
            check.Unchecked += (_, _) => UpdateWidget(widget, item => item.Visible = false);
            header.Children.Add(check);
            panel.Children.Add(header);

            var sizes = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(28, 8, 0, 0)
            };
            sizes.Children.Add(new TextBlock
            {
                Text = "Ширина",
                Foreground = Brush(_preferences.MutedForeground),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            sizes.Children.Add(CreateCombo(
                [("small", "Малая"), ("medium", "Средняя"), ("wide", "Широкая"), ("full", "На всю ширину")],
                widget.Width,
                value => UpdateWidget(widget, item => item.Width = value),
                140));
            sizes.Children.Add(new TextBlock
            {
                Text = "Высота",
                Foreground = Brush(_preferences.MutedForeground),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 6, 0)
            });
            sizes.Children.Add(CreateCombo(
                [("small", "Низкая"), ("medium", "Средняя"), ("large", "Высокая")],
                widget.Height,
                value => UpdateWidget(widget, item => item.Height = value),
                125));
            panel.Children.Add(sizes);

            var colors = new WrapPanel { Margin = new Thickness(28, 8, 0, 0) };
            AddColorEditor(colors, "Фон карточки", () => widget.Background,
                value => UpdateWidget(widget, item => item.Background = value), 135);
            AddColorEditor(colors, "Текст", () => widget.Foreground,
                value => UpdateWidget(widget, item => item.Foreground = value), 115);
            AddColorEditor(colors, "Акцент/график", () => widget.Accent,
                value => UpdateWidget(widget, item => item.Accent = value), 145);
            panel.Children.Add(colors);
            card.Child = panel;
            WidgetsPanel.Children.Add(card);
        }
    }

    private void BuildColumns()
    {
        ColumnsPanel.Children.Clear();
        foreach (var column in _preferences.Columns.OrderBy(item => item.Order))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5), Height = 38 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var check = new CheckBox
            {
                Content = column.Title,
                IsChecked = column.Visible,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush(_preferences.Foreground),
                FontSize = 13
            };
            check.Checked += (_, _) => UpdateColumn(column, item => item.Visible = true);
            check.Unchecked += (_, _) => UpdateColumn(column, item => item.Visible = false);
            row.Children.Add(check);

            var width = new TextBox
            {
                Text = column.Width.ToString("0", CultureInfo.InvariantCulture),
                Width = 60,
                Height = 29,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "Ширина колонки в пикселях"
            };
            width.TextChanged += (_, _) =>
            {
                if (!_isBuilding && double.TryParse(width.Text, CultureInfo.InvariantCulture, out var value) &&
                    value is >= 50 and <= 600)
                {
                    UpdateColumn(column, item => item.Width = value);
                }
            };
            Grid.SetColumn(width, 1);
            row.Children.Add(width);
            var up = SmallButton("↑", () => MoveColumn(column, -1), 36);
            Grid.SetColumn(up, 2);
            row.Children.Add(up);
            var down = SmallButton("↓", () => MoveColumn(column, 1), 36);
            Grid.SetColumn(down, 3);
            row.Children.Add(down);
            ColumnsPanel.Children.Add(row);
        }
    }

    private void AddColorEditor(
        Panel panel,
        string title,
        Func<string> getValue,
        Action<string> setValue,
        double width)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 10, 8), Width = width };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brush(_preferences.MutedForeground),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 3)
        });
        var editor = new DockPanel();
        var preview = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(_preferences.ChartGridColor),
            Background = Brush(getValue()),
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        editor.Children.Add(preview);
        var textBox = new TextBox
        {
            Text = getValue(),
            MaxLength = 7,
            Height = 29,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        textBox.TextChanged += (_, _) =>
        {
            if (_isBuilding || !IsHexColor(textBox.Text))
            {
                return;
            }
            setValue(textBox.Text.ToUpperInvariant());
            preview.Background = Brush(getValue());
            SaveAndApply();
        };
        editor.Children.Add(textBox);
        stack.Children.Add(editor);
        panel.Children.Add(stack);
    }

    private static ComboBox CreateCombo(
        IReadOnlyList<(string Value, string Label)> choices,
        string selectedValue,
        Action<string> changed,
        double width)
    {
        var combo = new ComboBox
        {
            Width = width,
            Height = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brush("#020502"),
            Foreground = Brush("#00FF41")
        };
        foreach (var choice in choices)
        {
            combo.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value });
        }
        combo.SelectedItem = combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == selectedValue);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string value)
            {
                changed(value);
            }
        };
        return combo;
    }

    private static Button SmallButton(string content, Action click, double width)
    {
        var button = new Button { Content = content, Width = width, Height = 27, Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(3, 0, 0, 0) };
        button.Click += (_, _) => click();
        return button;
    }

    private void MoveWidget(WidgetPreference widget, int delta)
    {
        var list = _preferences.Widgets.OrderBy(item => item.OrderIndex).ToList();
        var index = list.IndexOf(widget);
        var target = index + delta;
        if (target < 0 || target >= list.Count)
        {
            return;
        }
        (list[index].OrderIndex, list[target].OrderIndex) = (list[target].OrderIndex, list[index].OrderIndex);
        _preferences.Widgets = list;
        SaveAndApply();
        Rebuild();
    }

    private void MoveColumn(ColumnPreference column, int delta)
    {
        var list = _preferences.Columns.OrderBy(item => item.Order).ToList();
        var index = list.IndexOf(column);
        var target = index + delta;
        if (target < 0 || target >= list.Count)
        {
            return;
        }
        (list[index].Order, list[target].Order) = (list[target].Order, list[index].Order);
        _preferences.Columns = list;
        SaveAndApply();
        BuildColumns();
    }

    private void UpdateWidget(WidgetPreference widget, Action<WidgetPreference> update)
    {
        update(widget);
        SaveAndApply();
    }

    private void UpdateColumn(ColumnPreference column, Action<ColumnPreference> update)
    {
        update(column);
        SaveAndApply();
    }

    private void SaveAndApply()
    {
        SavedText.Text = _save(_preferences)
            ? $"Сохранено {DateTime.Now:HH:mm:ss}"
            : "Не удалось сохранить настройки.";
    }

    private void TimeZoneSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isBuilding || TimeZoneSelector.SelectedValue is not string timeZoneId)
        {
            return;
        }

        _preferences.TimeZoneId = timeZoneId;
        SaveAndApply();
    }

    private void ActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action })
        {
            _executeAction(action);
        }
    }

    public void SetRefreshing(bool isRefreshing)
    {
        RefreshActionButton.IsEnabled = !isRefreshing;
        RefreshActionButton.Content = isRefreshing ? "Обновление…" : "↻  Обновить список сделок";
    }

    private static bool IsHexColor(string value) =>
        value.Length == 7 &&
        value[0] == '#' &&
        value.AsSpan(1).ToString().All(Uri.IsHexDigit);

    private static SolidColorBrush Brush(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private void ResetClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "Вернуть порядок колонок, виджеты и цвета к настройкам по умолчанию?",
            "Сбросить настройки",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }
        _preferences = DashboardPreferences.CreateDefault();
        _preferences.EnsureCatalog();
        SaveAndApply();
        Rebuild();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
