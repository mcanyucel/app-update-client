using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;

namespace Bridgewiz.AppUpdate.Avalonia;

/// <summary>
/// The update flows of a desktop app: a quiet check at startup, a check the user asks for, and the offer itself
/// (release notes, download, skip this version). Each returns true when the installer has started: the app must
/// then close, so that the installer can replace its files (it starts the app again when done).
/// </summary>
public sealed class UpdatePrompt(UpdateClient client, IUpdateSettings settings)
{
    /// <summary>How long the startup check waits for the server; it stays quiet when the server is slow.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a check the user asked for waits.</summary>
    public TimeSpan ManualTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Runs after the download, before the installer starts: save or discard open work here. Returning false cancels
    /// the update (it is offered again next time).
    /// </summary>
    public Func<Task<bool>>? BeforeInstall { get; init; }

    /// <summary>
    /// At startup: removes old installers, then looks for an update unless the user turned that off. Quiet when there
    /// is none or the server can't be reached (offline, no VPN); only an update the user hasn't skipped is offered.
    /// </summary>
    public async Task<bool> CheckOnStartupAsync(Window owner)
    {
        client.CleanUp();
        if (!settings.CheckOnStartup) return false;

        UpdateInfo? update;
        try
        {
            update = await client.CheckAsync(StartupTimeout);
        }
        catch (UpdateException e)
        {
            Trace.WriteLine($"Update check: {e.Message}");
            return false;
        }
        return update is not null && !IsSkipped(update, settings.SkippedVersion) && await OfferAsync(owner, update);
    }

    /// <summary>Help ▸ Check for updates: like the startup check, but says what it found, and offers skipped versions too.</summary>
    public async Task<bool> CheckNowAsync(Window owner)
    {
        UpdateInfo? update;
        try
        {
            update = await client.CheckAsync(ManualTimeout);
        }
        catch (UpdateException e)
        {
            await MessageWindow.ShowAsync(owner, "Check for updates", e.Message);
            return false;
        }

        if (update is not null) return await OfferAsync(owner, update);
        await MessageWindow.ShowAsync(owner, "Check for updates",
            $"You have the latest version ({UpdateClient.Normalize(client.Options.CurrentVersion).ToString(3)}).");
        return false;
    }

    /// <summary>Offers <paramref name="update"/>: Update now downloads and installs it, Skip this version remembers the choice.</summary>
    public async Task<bool> OfferAsync(Window owner, UpdateInfo update)
    {
        var decision = await new UpdateWindow(client, update) { Icon = owner.Icon }.ShowDialog<UpdateDecision?>(owner);
        switch (decision)
        {
            case { Choice: UpdateChoice.Skip }:
                settings.SkippedVersion = update.Version.ToString(3);
                settings.Save();
                return false;
            case { Choice: UpdateChoice.Install, InstallerPath: { } installer }:
                if (BeforeInstall is not null && !await BeforeInstall()) return false;
                try
                {
                    client.StartInstaller(installer);
                    return true;
                }
                catch (Exception e) when (e is Win32Exception or IOException)
                {
                    await MessageWindow.ShowAsync(owner, "Update", $"The installer could not be started.\n\n{e.Message}");
                    return false;
                }
            default:
                return false;
        }
    }

    /// <summary>Whether the user skipped exactly this version.</summary>
    public static bool IsSkipped(UpdateInfo update, string? skippedVersion) =>
        UpdateClient.ParseVersion(skippedVersion) is { } skipped && skipped == update.Version;
}
