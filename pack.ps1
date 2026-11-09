<#
.SYNOPSIS
    Tests and packs both packages into artifacts\: Bridgewiz.AppUpdate and Bridgewiz.AppUpdate.Avalonia (.nupkg, plus
    .snupkg symbols). The version comes from Directory.Build.props.

.EXAMPLE
    .\pack.ps1
    dotnet nuget push artifacts\*.nupkg --source https://api.nuget.org/v3/index.json --api-key <key>
#>
param([switch] $AllowDirty)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$artifacts = Join-Path $root "artifacts"

if (-not $AllowDirty -and (git -C $root status --porcelain)) {
    throw "The working tree has uncommitted changes. Commit them first (the packages link to the commit), or pass -AllowDirty."
}

dotnet test --project (Join-Path $root "tests\Bridgewiz.AppUpdate.Tests")
if ($LASTEXITCODE -ne 0) { throw "The tests failed." }

if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
# ContinuousIntegrationBuild: deterministic paths in the symbols, so Source Link maps them to the GitHub commit.
dotnet pack (Join-Path $root "app-update-client.slnx") -c Release -o $artifacts -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed." }

Get-ChildItem $artifacts | ForEach-Object { Write-Host $_.Name }
