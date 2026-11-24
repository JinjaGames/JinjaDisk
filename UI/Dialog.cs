using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace JinjaDisk;

/// <summary>Koyu temalı onay / bilgi penceresi.</summary>
public static class Dialog
{
    public static bool Confirm(Window owner, string title, string message, string ok = "Tamam", bool danger = false, string cancel = "Vazgeç")
        => Show(owner, title, message, ok, cancel, danger);

    public static void Info(Window owner, string title, string message) => Show(owner, title, message, "Tamam", null, false);

    static bool Show(Window owner, string title, string message, string ok, string cancel, bool danger)
    {
        var app = Application.Current;
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (owner != null && owner.IsVisible) w.Owner = owner;

        bool result = false;
        var root = new Border
        {
            Background = (Brush)app.FindResource("Panel"),
            BorderBrush = (Brush)app.FindResource("Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(26, 22, 26, 20),
            Margin = new Thickness(24),
            MaxWidth = 560,
            MinWidth = 380,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black }
        };
        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new TextBlock
        {
            Text = danger ? "" : "",
            FontFamily = (FontFamily)app.FindResource("IconFont"),
            FontSize = 20,
            Foreground = (Brush)app.FindResource(danger ? "Danger" : "Accent2"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        });
        head.Children.Add(new TextBlock { Text = title, Style = (Style)app.FindResource("H2"), FontSize = 17, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock
        {
            Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 21,
            Foreground = (Brush)app.FindResource("SubText"), Margin = new Thickness(0, 0, 0, 22)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (cancel != null)
        {
            var c = new Button { Content = cancel, Margin = new Thickness(0, 0, 10, 0), MinWidth = 96, IsCancel = true };
            c.Click += (_, _) => { result = false; w.Close(); };
            buttons.Children.Add(c);
        }
        var o = new Button
        {
            Content = ok, MinWidth = 110, IsDefault = true,
            Style = (Style)app.FindResource(danger ? "BtnDanger" : "BtnPrimary")
        };
        o.Click += (_, _) => { result = true; w.Close(); };
        buttons.Children.Add(o);
        sp.Children.Add(buttons);
        root.Child = sp;
        root.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) w.DragMove(); };
        w.Content = root;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.ShowDialog();
        return result;
    }
}
