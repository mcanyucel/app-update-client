using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Bridgewiz.AppUpdate.Avalonia;

namespace Bridgewiz.AppUpdate.Tests;

public sealed class UpdateClientTests : IDisposable
{
    private static readonly Uri Server = new("https://updates.example.com/");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] Installer = "pretend installer"u8.ToArray();
    private static readonly string InstallerHash = Convert.ToHexStringLower(SHA256.HashData(Installer));

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "app-update-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Answers like app-update-server, with what the test asks for.</summary>
    private sealed class FakeServer(Func<HttpRequestMessage, JsonElement?, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, JsonElement? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            JsonElement? body = request.Content is null ? null : await request.Content.ReadFromJsonAsync<JsonElement>(cancel);
            Requests.Add((request, body));
            return answer(request, body);
        }
    }

    /// <summary>A check answer naming <paramref name="version"/>, with the state worked out from the request's seed.</summary>
    private static Func<HttpRequestMessage, JsonElement?, HttpResponseMessage> Latest(string version, string? downloadUrl = null,
        string? state = null, string hash = "") =>
        (request, body) =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) };
            int seed = body!.Value.GetProperty("stateSeed").GetInt32();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    version,
                    downloadUrl = downloadUrl ?? $"/api/update/download/section-analyzer/{version}",
                    releaseNotes = "Faster M-M contours",
                    sha256Hash = hash == "" ? InstallerHash.ToUpperInvariant() : hash,
                    state = state ?? UpdateClient.ExpectedState(seed),
                }),
            };
        };

    private (UpdateClient Client, FakeServer Server) Make(Func<HttpRequestMessage, JsonElement?, HttpResponseMessage> answer,
        Version? current = null)
    {
        var server = new FakeServer(answer);
        var options = new UpdateOptions
        {
            AppName = "Section Analyzer", CurrentVersion = current ?? new Version(1, 0, 0), Server = Server, DownloadFolder = _folder,
        };
        return (new UpdateClient(new HttpClient(server), options), server);
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sends_the_app_identity_and_a_seed()
    {
        var (client, server) = Make(Latest("1.0.0"));
        await client.CheckAsync(Timeout, Cancel);

        var (request, body) = Assert.Single(server.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://updates.example.com/api/update"), request.RequestUri);
        string identity = body!.Value.GetProperty("appIdentity").GetString()!;
        Assert.StartsWith("{ Name=Section Analyzer, Version=1.0.0, ProcessorArchitecture=", identity);
        Assert.Equal(JsonValueKind.Number, body.Value.GetProperty("stateSeed").ValueKind);
    }

    [Fact]
    public void State_matches_the_server()
    {
        // The server: base64 of the first 8 bytes of SHA-256 over the seed's 4 little-endian bytes.
        Assert.Equal("6KSy7n7eeaM=", UpdateClient.ExpectedState(42));
    }

    [Fact]
    public async Task A_newer_version_is_an_update_with_a_full_download_address()
    {
        var (client, _) = Make(Latest("1.2.0"));
        UpdateInfo? update = await client.CheckAsync(Timeout, Cancel);

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 2, 0), update.Version);
        Assert.Equal(new Uri("https://updates.example.com/api/update/download/section-analyzer/1.2.0"), update.DownloadUrl);
        Assert.Equal("Faster M-M contours", update.ReleaseNotes);
        Assert.Equal(InstallerHash, update.Sha256);    // lower-cased
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0")]          // the same version written shorter
    [InlineData("0.9.5")]        // after a rollback the server names an older upload: never a downgrade
    public async Task The_same_or_an_older_version_is_no_update(string latest)
    {
        var (client, _) = Make(Latest(latest), current: new Version(1, 0, 0, 0));
        Assert.Null(await client.CheckAsync(Timeout, Cancel));
    }

    [Fact]
    public async Task A_stale_or_forged_answer_is_refused()
    {
        var (client, _) = Make(Latest("2.0.0", state: "AAAAAAAAAAA="));
        var e = await Assert.ThrowsAsync<UpdateException>(() => client.CheckAsync(Timeout, Cancel));
        Assert.Contains("freshness", e.Message);
    }

    [Fact]
    public async Task A_download_address_on_another_site_is_refused()
    {
        var (client, _) = Make(Latest("2.0.0", downloadUrl: "https://elsewhere.example.com/setup.exe"));
        await Assert.ThrowsAsync<UpdateException>(() => client.CheckAsync(Timeout, Cancel));
    }

    [Theory]
    [InlineData("2.0.0", "abc")]                 // no proper checksum
    [InlineData("two", null)]                    // not a version number
    public async Task A_malformed_answer_is_refused(string version, string? hash)
    {
        var (client, _) = Make(Latest(version, hash: hash ?? ""));
        await Assert.ThrowsAsync<UpdateException>(() => client.CheckAsync(Timeout, Cancel));
    }

    [Fact]
    public async Task An_app_unknown_to_the_server_says_so()
    {
        var (client, _) = Make((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var e = await Assert.ThrowsAsync<UpdateException>(() => client.CheckAsync(Timeout, Cancel));
        Assert.Contains("Section Analyzer is not on the update server", e.Message);
    }

    [Fact]
    public async Task An_unreachable_server_is_an_update_error()
    {
        var (client, _) = Make((_, _) => throw new HttpRequestException("No such host is known."));
        var e = await Assert.ThrowsAsync<UpdateException>(() => client.CheckAsync(Timeout, Cancel));
        Assert.Contains("No such host", e.Message);
    }

    [Fact]
    public async Task Download_keeps_an_installer_that_matches_its_checksum()
    {
        var (client, _) = Make(Latest("1.2.0"));
        UpdateInfo update = (await client.CheckAsync(Timeout, Cancel))!;

        string path = await client.DownloadAsync(update, cancel: Cancel);

        Assert.Equal(Path.Combine(_folder, "SectionAnalyzer-1.2.0-setup.exe"), path);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(path, Cancel));
        Assert.Single(Directory.GetFiles(_folder));      // no .part left behind

        client.CleanUp();
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Download_deletes_an_installer_that_does_not_match()
    {
        var (client, _) = Make(Latest("1.2.0"));
        UpdateInfo update = (await client.CheckAsync(Timeout, Cancel))! with { Sha256 = new string('0', 64) };

        await Assert.ThrowsAsync<UpdateException>(() => client.DownloadAsync(update, cancel: Cancel));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2", "1.2.0")]
    [InlineData(" 2.0.0.4 ", "2.0.0.4")]
    public void Versions_are_read_and_padded(string text, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateClient.ParseVersion(text));

    [Fact]
    public void Options_default_to_an_inno_setup_installer_in_temp()
    {
        var options = new UpdateOptions { AppName = "Section Importer", CurrentVersion = new Version(0, 1), Server = Server };

        Assert.Equal(Path.Combine(Path.GetTempPath(), "SectionImporter", "updates"), options.DownloadFolder);
        Assert.Equal("/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS", options.InstallerArguments);
        using HttpClient http = UpdateClient.CreateHttpClient(options);
        Assert.Equal("SectionImporter/0.1.0", http.DefaultRequestHeaders.UserAgent.ToString());
        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, http.Timeout);
    }

    [Theory]
    [InlineData("1.2.0", true)]
    [InlineData("1.2", true)]
    [InlineData("1.1.0", false)]
    [InlineData(null, false)]
    [InlineData("rubbish", false)]
    public void Only_the_skipped_version_is_skipped(string? skipped, bool expected)
    {
        var update = new UpdateInfo(new Version(1, 2, 0), Server, "", new string('0', 64));
        Assert.Equal(expected, UpdatePrompt.IsSkipped(update, skipped));
    }
}
