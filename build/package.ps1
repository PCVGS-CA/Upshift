<#
.SYNOPSIS
    Builds Upshift's release files: the installer, the portable zip and the self-update packages.

.DESCRIPTION
    Publishes Release x64 (self-contained) and packs it with Velopack (vpk, pinned in dotnet-tools.json).
    The release workflow (.github/workflows/release.yml) runs this; it can also be run locally for testing:

        powershell -ExecutionPolicy Bypass -File build\package.ps1 -OutDir <folder>

    The version comes from Directory.Build.props and the release notes from its "## [x.y.z]" section in
    CHANGELOG.md. In -OutDir it leaves:
        Upshift-Setup-x64.exe          the installer (per user, no admin, Start menu shortcut)
        Upshift-Portable-x64.zip       the portable version (no installer; can still update itself)
        Upshift.App-<version>-full.nupkg (and -delta.nupkg when an older release was downloaded into -OutDir first)
        releases.win.json              the update feed the app reads
        release-notes.md               the notes for the GitHub release
#>
param(
    [string]$OutDir = "releases",
    [string]$PublishDir = "publish",
    # When given (e.g. "v1.0.0"), it must match the version, or nothing is built.
    [string]$Tag = ""
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Must match AppLocations.PackageId in src/Upshift.Core/Services/AppLocations.cs. It decides the install folder
# (%LocalAppData%\Upshift.App), which must never be the data folder (%LocalAppData%\Upshift).
$packId = 'Upshift.App'

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    # Relative to the repo; absolute paths are kept as they are.
    $OutDir = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $OutDir))
    $PublishDir = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $PublishDir))
    $utf8 = New-Object Text.UTF8Encoding $false

    # ---- Version (Directory.Build.props) and tag ----
    $version = (dotnet msbuild src/Upshift.App/Upshift.App.csproj -nologo -getProperty:Version -p:Platform=x64 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "Couldn't read the version from Directory.Build.props (got '$version')."
    }
    if ($Tag -and $Tag -ne "v$version") {
        throw "The tag '$Tag' doesn't match the version $version in Directory.Build.props. Tag the release 'v$version'."
    }
    Write-Host "Upshift $version"

    # ---- Release notes (CHANGELOG.md) ----
    $changelog = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.md'), $utf8)
    $pattern = '(?ms)^## \[' + [regex]::Escape($version) + '\][^\n]*\n(.*?)(?=^## \[|\z)'
    $match = [regex]::Match($changelog, $pattern)
    if (-not $match.Success -or -not $match.Groups[1].Value.Trim()) {
        throw "CHANGELOG.md has no '## [$version]' section with notes."
    }
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $notes = Join-Path $OutDir 'release-notes.md'
    [IO.File]::WriteAllText($notes, $match.Groups[1].Value.Trim() + "`n", $utf8)

    # ---- Publish Release x64, self-contained ----
    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
    Invoke-Checked 'dotnet publish' {
        dotnet publish src/Upshift.App/Upshift.App.csproj -nologo -c Release -p:Platform=x64 -r win-x64 --self-contained -o $PublishDir
    }

    # ---- Pack with Velopack ----
    Invoke-Checked 'dotnet tool restore' { dotnet tool restore }
    Invoke-Checked 'vpk pack' {
        dotnet vpk pack --packId $packId --packVersion $version --packDir $PublishDir --mainExe Upshift.exe `
            --packTitle Upshift --packAuthors PCVGS --icon src/Upshift.App/Assets/Upshift.ico `
            --releaseNotes $notes --shortcuts StartMenuRoot --runtime win-x64 --outputDir $OutDir
    }

    # Friendlier names for the two files people download. The update feed doesn't refer to either.
    Move-Item -Force (Join-Path $OutDir "$packId-win-Setup.exe") (Join-Path $OutDir 'Upshift-Setup-x64.exe')
    Move-Item -Force (Join-Path $OutDir "$packId-win-Portable.zip") (Join-Path $OutDir 'Upshift-Portable-x64.zip')

    Write-Host "Release files in ${OutDir}:"
    Get-ChildItem $OutDir -File | ForEach-Object { Write-Host ("  {0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
}
finally {
    Pop-Location
}
