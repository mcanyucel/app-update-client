using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bridgewiz.AppUpdate.Avalonia;

/// <summary>A modal message with an OK button, for what a check found.</summary>
internal sealed class MessageWindow : Window
{
    private MessageWindow(string title, string message)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 520;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        UpdateWindow.Primary(ok);
        ok.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 20,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MinWidth = 280 }, ok },
        };
    }

    public static Task ShowAsync(Window owner, string title, string message) =>
        new MessageWindow(title, message) { Icon = owner.Icon }.ShowDialog(owner);
}
