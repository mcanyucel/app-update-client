# app-update-client

Update checks for desktop apps released through [app-update-server](https://github.com/mcanyucel/app-update-server):
the app asks the server whether a newer version exists, downloads its installer, checks the installer's SHA-256, and
runs it silently; the installer closes the app, replaces it and starts it again.

| Package | |
|---|---|
| `Bridgewiz.AppUpdate` | The client: check, download and verify, run the installer. No UI. |
| `Bridgewiz.AppUpdate.Avalonia` | For Avalonia apps: the update dialog (release notes, progress, skip this version) and the check-at-startup and check-now flows. |

## Use it

```csharp
using Bridgewiz.AppUpdate;
using Bridgewiz.AppUpdate.Avalonia;

var options = new UpdateOptions
{
    AppName = "Section Importer",                          // as the update server knows the app
    CurrentVersion = typeof(App).Assembly.GetName().Version!,
    Server = new Uri("https://updates.example.com/"),
};
var client = new UpdateClient(UpdateClient.CreateHttpClient(options), options);
var prompt = new UpdatePrompt(client, settings)            // settings: the app's own IUpdateSettings
{
    BeforeInstall = () => viewModel.ConfirmDiscardAsync(),  // optional: save or discard open work first
};

// When the main window has opened (Release builds only, so development builds don't offer the released version):
if (await prompt.CheckOnStartupAsync(mainWindow)) mainWindow.Close();

// Help ▸ Check for updates:
if (await prompt.CheckNowAsync(mainWindow)) mainWindow.Close();
```

Every flow returns true when the installer has started; the app then closes, so that the installer can replace its
files. `CheckOnStartupAsync` stays quiet when there is no update or the server can't be reached, and doesn't offer a
version the user skipped. `IUpdateSettings` keeps the user's choices (check at startup, the skipped version) in the
app's own settings.

Without Avalonia, use `UpdateClient` directly: `CheckAsync`, `DownloadAsync`, `StartInstaller`, and `CleanUp` at
startup to remove the installers of earlier updates.

**No API key in the app.** Checking and downloading are open on the server; its API key is only for uploading
releases. A key shipped in an app can be read out of it, and with it anyone could upload an "update" that every
installed copy would run. Keep it on the machine that uploads releases.

## What the installer must do

The defaults suit an Inno Setup installer that:

- installs per user (`PrivilegesRequired=lowest`), so the silent update needs no elevation prompt;
- has `CloseApplications=yes`, so the Restart Manager closes the app if it is still running;
- starts the app again from `[Run]` with `Flags: nowait postinstall` (in a silent install that always runs).

It is run with `/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS`; set `UpdateOptions.InstallerArguments` for
another installer.

## The protocol

`POST {server}/api/update` with `{ "appIdentity": "{ Name=<app>, Version=1.2.3, ProcessorArchitecture=X64 }",
"stateSeed": <random int> }`. The answer carries `version`, `downloadUrl` (a path on the same server), `releaseNotes`,
`sha256Hash` and `state`, which must equal base64 of the first 8 bytes of SHA-256 over the seed's 4 little-endian
bytes; an answer that fails this, points the download at another site, or has no valid checksum is refused. Only a
version higher than the running one is an update, so a rollback on the server never downgrades anyone.

## Build and release

```
dotnet test --project tests/Bridgewiz.AppUpdate.Tests
.\pack.ps1                                   # artifacts\*.nupkg and *.snupkg
dotnet nuget push artifacts\*.nupkg --source https://api.nuget.org/v3/index.json --api-key <nuget.org key>
```

Raise `<Version>` in `Directory.Build.props` first; both packages share it.

## License

MIT, see [LICENSE](LICENSE).
