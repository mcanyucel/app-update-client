namespace Bridgewiz.AppUpdate;

/// <summary>A newer version on the update server.</summary>
/// <param name="Version">Its version, padded to at least three parts (1.2 is 1.2.0).</param>
/// <param name="DownloadUrl">The installer, on the update server itself.</param>
/// <param name="ReleaseNotes">What changed, as the release's uploader wrote it; may be empty.</param>
/// <param name="Sha256">The installer's SHA-256 as lower-case hex.</param>
public sealed record UpdateInfo(Version Version, Uri DownloadUrl, string ReleaseNotes, string Sha256);

/// <summary>An update check or download that failed; the message is written for the user.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);
