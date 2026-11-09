namespace Bridgewiz.AppUpdate.Avalonia;

/// <summary>The user's update choices, kept in the app's own settings.</summary>
public interface IUpdateSettings
{
    /// <summary>Whether to look for an update at startup.</summary>
    bool CheckOnStartup { get; }

    /// <summary>The version (major.minor.patch) the user chose to skip; the startup check doesn't offer it again.</summary>
    string? SkippedVersion { get; set; }

    /// <summary>Stores the settings after <see cref="SkippedVersion"/> changed.</summary>
    void Save();
}
