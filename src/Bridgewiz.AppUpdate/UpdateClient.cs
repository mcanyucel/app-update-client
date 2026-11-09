using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bridgewiz.AppUpdate;

/// <summary>
/// Talks to app-update-server (github.com/mcanyucel/app-update-server): POST /api/update with the app's identity and
/// a random seed; the answer names the latest version, its download path and SHA-256, and a state derived from the
/// seed that shows the answer is fresh. Checking and downloading need no API key.
/// </summary>
public sealed class UpdateClient(HttpClient http, UpdateOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The app this client checks for.</summary>
    public UpdateOptions Options { get; } = options;

    /// <summary>
    /// An HttpClient for this client: no overall timeout (each check takes its own; a download can take long) and a
    /// User-Agent naming the app and its version. Keep one for the app's lifetime.
    /// </summary>
    public static HttpClient CreateHttpClient(UpdateOptions options)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{options.FileName}/{Normalize(options.CurrentVersion).ToString(3)}");
        return http;
    }

    // Read by the JSON serializer only.
    // ReSharper disable NotAccessedPositionalProperty.Local
    private sealed record CheckRequest(string AppIdentity, int StateSeed);
    // ReSharper restore NotAccessedPositionalProperty.Local

    private sealed record CheckResponse(string? Version, string? DownloadUrl, string? ReleaseNotes, string? Sha256Hash, string? State);

    /// <summary>The newer version on the server, or null when the running version is the latest.</summary>
    /// <remarks>
    /// The server names its most recent upload, which after a rollback can be older than the running version: only a
    /// higher version number counts as an update, so nobody is downgraded.
    /// </remarks>
    /// <exception cref="UpdateException">The server could not be reached in time, or its answer was not usable.</exception>
    public async Task<UpdateInfo?> CheckAsync(TimeSpan timeout, CancellationToken cancel = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);

        Version current = Normalize(Options.CurrentVersion);
        int seed = RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);
        string identity = $"{{ Name={Options.AppName}, Version={current.ToString(3)}, ProcessorArchitecture={RuntimeInformation.ProcessArchitecture} }}";
        try
        {
            using HttpResponseMessage response =
                await http.PostAsJsonAsync(new Uri(Options.Server, "api/update"), new CheckRequest(identity, seed), Json, cts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new UpdateException($"{Options.AppName} is not on the update server yet.");
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"The update server answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            CheckResponse? body = await response.Content.ReadFromJsonAsync<CheckResponse>(Json, cts.Token);
            return Interpret(body, seed, current);
        }
        catch (OperationCanceledException e) when (!cancel.IsCancellationRequested)
        {
            throw new UpdateException($"The update server did not answer within {timeout.TotalSeconds:0} seconds.", e);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException($"Could not reach the update server: {e.Message}", e);
        }
        catch (JsonException e)
        {
            throw new UpdateException("The update server's answer could not be read.", e);
        }
    }

    private UpdateInfo? Interpret(CheckResponse? body, int seed, Version current)
    {
        if (body is null) throw new UpdateException("The update server sent an empty answer.");
        if (body.State != ExpectedState(seed))
            throw new UpdateException("The update server's answer failed its freshness check and was ignored.");
        if (ParseVersion(body.Version) is not { } latest)
            throw new UpdateException($"The update server reports version \"{body.Version}\", which is not a version number.");
        if (latest <= current) return null;

        // The server sends a path on itself; anything pointing elsewhere is refused.
        Uri server = Options.Server;
        if (!Uri.TryCreate(server, body.DownloadUrl, out Uri? url)
            || url.Scheme != server.Scheme || !string.Equals(url.Authority, server.Authority, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException($"The update server gave a download address on another site ({body.DownloadUrl}).");
        if (body.Sha256Hash is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
            throw new UpdateException("The update server sent no valid checksum for the installer.");

        return new UpdateInfo(latest, url, body.ReleaseNotes ?? "", hash.ToLowerInvariant());
    }

    /// <summary>
    /// Downloads the installer into <see cref="UpdateOptions.DownloadFolder"/> and checks its SHA-256; returns its
    /// path. A file that doesn't match is deleted.
    /// </summary>
    /// <param name="update">What <see cref="CheckAsync"/> found.</param>
    /// <param name="progress">The fraction downloaded, 0 to 1 (only when the server sends the size).</param>
    /// <param name="cancel">Stops the download; nothing is left behind.</param>
    /// <exception cref="UpdateException">The download failed or the file did not match its checksum.</exception>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        string folder = Options.DownloadFolder;
        string path = Path.Combine(folder, $"{Options.FileName}-{update.Version.ToString(3)}-setup.exe");
        string partial = path + ".part";
        try
        {
            Directory.CreateDirectory(folder);
            using HttpResponseMessage response = await http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"The download failed: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            long? length = response.Content.Headers.ContentLength;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancel))
            await using (FileStream target = File.Create(partial))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    sha.AppendData(buffer, 0, read);
                    done += read;
                    if (length > 0) progress?.Report((double)done / length.Value);
                }
            }

            if (Convert.ToHexStringLower(sha.GetHashAndReset()) != update.Sha256)
                throw new UpdateException("The downloaded installer does not match its checksum, so it was deleted. Try again later.");

            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException($"The download failed: {e.Message}", e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"The installer could not be saved: {e.Message}", e);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>
    /// Runs a downloaded installer with <see cref="UpdateOptions.InstallerArguments"/>. The app should close right
    /// after, so that the installer can replace its files.
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The installer could not be started.</exception>
    public void StartInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, Options.InstallerArguments) { UseShellExecute = true });

    /// <summary>Removes installers left by earlier updates (an installer can't delete itself). Call at startup.</summary>
    public void CleanUp()
    {
        if (!Directory.Exists(Options.DownloadFolder)) return;
        foreach (string file in Directory.EnumerateFiles(Options.DownloadFolder)) TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The state the server derives from a seed: the first 8 bytes of SHA-256 of the seed (little-endian), base64.</summary>
    public static string ExpectedState(int seed)
    {
        byte[] bytes = BitConverter.GetBytes(seed);
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return Convert.ToBase64String(SHA256.HashData(bytes)[..8]);
    }

    /// <summary>"1.2.3" or "v1.2.3" as a version, or null.</summary>
    public static Version? ParseVersion(string? text) =>
        Version.TryParse(text?.Trim().TrimStart('v', 'V'), out Version? v) ? Normalize(v) : null;

    /// <summary>
    /// Fills the missing parts with zeros, so that 1.0 and 1.0.0 compare equal (System.Version puts 1.0.0 below
    /// 1.0.0.0). A fourth part is kept only when it is set.
    /// </summary>
    public static Version Normalize(Version v) =>
        v.Revision > 0
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0), v.Revision)
            : new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
}
