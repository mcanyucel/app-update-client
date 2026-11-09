namespace Bridgewiz.AppUpdate;

/// <summary>
/// What the client needs to know about the app. Fixed for an app, so set it in code; nothing here is secret. The
/// server's API key is for uploading releases only and never belongs in an app.
/// </summary>
public sealed record UpdateOptions
{
    /// <summary>The app's name as the update server knows it, e.g. "Section Analyzer".</summary>
    public required string AppName { get; init; }

    /// <summary>The running version; only a higher version on the server counts as an update.</summary>
    public required Version CurrentVersion { get; init; }

    /// <summary>The update server, e.g. https://updates.example.com/. Downloads must come from this same site.</summary>
    public required Uri Server { get; init; }

    /// <summary>Where installers are downloaded. Default: %TEMP%\&lt;AppName without spaces&gt;\updates.</summary>
    public string DownloadFolder
    {
        get => field ?? Path.Combine(Path.GetTempPath(), FileName, "updates");
        init;
    }

    /// <summary>
    /// The installer's command line when an update runs it. The default suits Inno Setup: progress only, no
    /// questions, the running app closed (Restart Manager) and started again by the installer's [Run] entry.
    /// </summary>
    public string InstallerArguments { get; init; } = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    /// <summary>The app name without spaces, for file and folder names.</summary>
    internal string FileName => string.Concat(AppName.Where(c => !char.IsWhiteSpace(c) && !Path.GetInvalidFileNameChars().Contains(c)));
}
