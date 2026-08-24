using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SlimeHelper
{
    public static class SlimeInputDialog
    {
        public static string Show(string title, string prompt, string defaultValue = "")
        {
            var brushConverter = new BrushConverter();
            var slimeBeige = brushConverter.ConvertFromString("#F5F5DC") as Brush ?? Brushes.Beige;
            var slimeBorder = brushConverter.ConvertFromString("#8B4513") as Brush ?? Brushes.SaddleBrown;

            Window window = new()
            {
                Title = title,
                Width = 420,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                Background = Brushes.Transparent,
                AllowsTransparency = true
            };

            Border mainBorder = new()
            {
                Background = slimeBeige,
                BorderBrush = slimeBorder,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(20)
            };
            mainBorder.Effect = new DropShadowEffect { BlurRadius = 15, ShadowDepth = 5, Opacity = 0.4 };

            StackPanel stackPanel = new();

            // Header
            stackPanel.Children.Add(new TextBlock
            {
                Text = prompt,
                Margin = new Thickness(0, 0, 0, 15),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.DarkSlateGray
            });

            Border textBorder = new()
            {
                Background = Brushes.White,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5)
            };
            TextBox textBox = new()
            {
                Text = defaultValue,
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Background = Brushes.Transparent
            };
            textBorder.Child = textBox;
            stackPanel.Children.Add(textBorder);

            Grid buttonGrid = new() { Margin = new Thickness(0, 20, 0, 0) };
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition());
            buttonGrid.ColumnDefinitions.Add(new ColumnDefinition());

            Button saveBtn = new()
            {
                Content = "Memorize Key",
                Padding = new Thickness(10, 8, 10, 8),
                Background = Brushes.LightGreen,
                FontWeight = FontWeights.Bold,
                IsDefault = true,
                Margin = new Thickness(0, 0, 5, 0)
            };

            Button cancelBtn = new()
            {
                Content = "Cancel",
                Padding = new Thickness(10, 8, 10, 8),
                Background = Brushes.LightCoral,
                Margin = new Thickness(5, 0, 0, 0)
            };

            saveBtn.Click += (s, e) => { window.DialogResult = true; window.Close(); };
            cancelBtn.Click += (s, e) => { window.DialogResult = false; window.Close(); };

            Grid.SetColumn(saveBtn, 0);
            Grid.SetColumn(cancelBtn, 1);
            buttonGrid.Children.Add(saveBtn);
            buttonGrid.Children.Add(cancelBtn);
            stackPanel.Children.Add(buttonGrid);

            mainBorder.Child = stackPanel;
            window.Content = mainBorder;

            window.Loaded += (s, e) => textBox.Focus();

            if (window.ShowDialog() == true)
            {
                return textBox.Text;
            }

            return string.Empty;
        }
    }
}