using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Bridgewiz.AppUpdate.Avalonia;

internal enum UpdateChoice { Later, Skip, Install }

/// <summary>What the user chose; with Install, the downloaded and checked installer.</summary>
internal sealed record UpdateDecision(UpdateChoice Choice, string? InstallerPath = null);

/// <summary>
/// Offers a newer version: Update now downloads the installer here (with progress) and closes with its path; Later
/// and Skip this version close without downloading. Closing during the download cancels it.
/// </summary>
internal sealed class UpdateWindow : Window
{
    private readonly UpdateClient _client;
    private readonly UpdateInfo _update;
    private readonly Button _skip, _later, _install;
    private readonly ProgressBar _progress;
    private readonly TextBlock _status;
    private CancellationTokenSource? _download;

    public UpdateWindow(UpdateClient client, UpdateInfo update)
    {
        _client = client;
        _update = update;
        string current = UpdateClient.Normalize(client.Options.CurrentVersion).ToString(3);

        Title = "Update available";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _skip = new Button { Content = "Skip this version" };
        _later = new Button { Content = "Later", IsCancel = true, MinWidth = 90 };
        _install = new Button { Content = "Update now", IsDefault = true, MinWidth = 90 };
        Primary(_install);
        _skip.Click += (_, _) => Close(new UpdateDecision(UpdateChoice.Skip));
        _later.Click += (_, _) => Close(new UpdateDecision(UpdateChoice.Later));
        _install.Click += async (_, _) => await DownloadAsync();

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 20, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _later, _install } };
        Grid.SetColumn(right, 2);
        buttons.Children.Add(_skip);
        buttons.Children.Add(right);
        DockPanel.SetDock(buttons, Dock.Bottom);

        var notes = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(update.ReleaseNotes) ? "No release notes." : update.ReleaseNotes.Trim(),
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            MinHeight = 80, MaxHeight = 240, VerticalContentAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(notes, "Release notes");
        _progress = new ProgressBar { IsVisible = false, MinWidth = 0, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 0) };
        _status = new TextBlock { IsVisible = false, TextWrapping = TextWrapping.Wrap };

        Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                buttons,
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"{client.Options.AppName} {update.Version.ToString(3)} is available. You have {current}.",
                            FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = "The app closes while the update installs and opens again when it is done.",
                            TextWrapping = TextWrapping.Wrap, Opacity = 0.7,
                        },
                        new TextBlock { Text = "What's new", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                        notes,
                        _progress,
                        _status,
                    },
                },
            },
        };
    }

    /// <summary>The app theme's accented button where it has one (Semi.Avalonia's SolidButton), a plain button otherwise.</summary>
    internal static void Primary(Button button)
    {
        button.Classes.Add("Primary");
        if (Application.Current?.TryFindResource("SolidButton", out object? theme) == true && theme is ControlTheme solid)
            button.Theme = solid;
    }

    private async Task DownloadAsync()
    {
        if (_download is not null) return;

        _download = new CancellationTokenSource();
        _skip.IsEnabled = _install.IsEnabled = false;
        _later.Content = "Cancel";
        _progress.IsVisible = true;
        _progress.IsIndeterminate = true;
        ShowStatus("Downloading the update…", error: false);

        var progress = new Progress<double>(fraction =>
        {
            _progress.IsIndeterminate = false;
            _progress.Value = fraction;
        });
        try
        {
            string path = await _client.DownloadAsync(_update, progress, _download.Token);
            Close(new UpdateDecision(UpdateChoice.Install, path));
        }
        catch (OperationCanceledException)
        {
            // Cancel or the close box: the window is closing already.
        }
        catch (UpdateException e)
        {
            _progress.IsVisible = false;
            ShowStatus(e.Message, error: true);
            _skip.IsEnabled = _install.IsEnabled = true;
            _install.Content = "Try again";
            _later.Content = "Later";
        }
        finally
        {
            _download?.Dispose();
            _download = null;
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        _download?.Cancel();
    }

    private void ShowStatus(string text, bool error)
    {
        _status.Text = text;
        _status.IsVisible = true;
        if (error) _status.Foreground = Brushes.Firebrick;
        else _status.ClearValue(TextBlock.ForegroundProperty);
    }
}
