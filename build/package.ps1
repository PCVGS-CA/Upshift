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
    [string]$Tag = "",
    # Local testing only: a different package id (e.g. "Upshift.App.Test"), so the test installer goes into
    # %LocalAppData%\<id> and leaves an installed Upshift alone. Never used for a release.
    [string]$TestPackId = ""
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Must match AppLocations.PackageId in src/Upshift.Core/Services/AppLocations.cs. It decides the install folder
# (%LocalAppData%\Upshift.App), which must never be the data folder (%LocalAppData%\Upshift).
$packId = 'Upshift.App'
$packTitle = 'Upshift'
$shortcuts = 'Desktop,StartMenuRoot'
if ($TestPackId) {
    if ($Tag) { throw "-TestPackId is for local testing only and can't be used with -Tag." }
    if ($TestPackId -notmatch '^Upshift\.App\.[A-Za-z0-9]+$') { throw "-TestPackId must look like 'Upshift.App.Test' (never 'Upshift', the data folder)." }
    $packId = $TestPackId
    # A test install must never touch the real install's Start menu shortcut ("Upshift.lnk"): it gets its own name and
    # no shortcuts at all. (On 2026-10-03 a test install replaced the user's shortcut, which then showed a blank icon.)
    $packTitle = 'Upshift (test build)'
    $shortcuts = 'None'
}

# ---- Icon checks: the release fails rather than ship an exe without Upshift's icon ----
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class UpshiftIconCheck {
  [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
  public static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
}
'@
function Assert-ExeIcon([string]$exe, [string]$what) {
    if (-not (Test-Path $exe)) { throw "Icon check: $what ($exe) is missing." }
    $count = [UpshiftIconCheck]::ExtractIconEx($exe, -1, $null, $null, 0)
    if ($count -lt 1) { throw "Icon check: $what ($exe) has no embedded icon." }
    Write-Host "  icon ok: $what"
}
function Assert-File([string]$path, [string]$what) {
    if (-not (Test-Path $path)) { throw "Icon check: $what is missing ($path)." }
    Write-Host "  icon ok: $what"
}

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
    # The exe carries the icon (Explorer, the taskbar fallback); the window loads Assets\Upshift.ico next to it.
    Assert-ExeIcon (Join-Path $PublishDir 'Upshift.exe') 'published Upshift.exe'
    Assert-File (Join-Path $PublishDir 'Assets\Upshift.ico') 'published Assets\Upshift.ico'

    # ---- Pack with Velopack ----
    Invoke-Checked 'dotnet tool restore' { dotnet tool restore }
    Invoke-Checked 'vpk pack' {
        dotnet vpk pack --packId $packId --packVersion $version --packDir $PublishDir --mainExe Upshift.exe `
            --packTitle $packTitle --packAuthors PCVGS --icon src/Upshift.App/Assets/Upshift.ico `
            --releaseNotes $notes --shortcuts $shortcuts --runtime win-x64 --outputDir $OutDir
    }

    # Friendlier names for the two files people download. The update feed doesn't refer to either.
    Move-Item -Force (Join-Path $OutDir "$packId-win-Setup.exe") (Join-Path $OutDir 'Upshift-Setup-x64.exe')
    Move-Item -Force (Join-Path $OutDir "$packId-win-Portable.zip") (Join-Path $OutDir 'Upshift-Portable-x64.zip')

    # The installer and both exes in the portable zip (Velopack's launcher and the app) have the icon, and the zip and
    # the update package carry Assets\Upshift.ico for the window.
    Assert-ExeIcon (Join-Path $OutDir 'Upshift-Setup-x64.exe') 'Upshift-Setup-x64.exe'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $unzip = Join-Path ([IO.Path]::GetTempPath()) ("upshift-iconcheck-" + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $OutDir 'Upshift-Portable-x64.zip'), $unzip)
        # Velopack names the portable launcher after the title (Upshift.exe for a release).
        Assert-ExeIcon (Join-Path $unzip "$packTitle.exe") "portable zip: $packTitle.exe (launcher)"
        Assert-ExeIcon (Join-Path $unzip 'current\Upshift.exe') 'portable zip: current\Upshift.exe'
        Assert-File (Join-Path $unzip 'current\Assets\Upshift.ico') 'portable zip: current\Assets\Upshift.ico'
    }
    finally {
        if (Test-Path $unzip) { Remove-Item -Recurse -Force $unzip }
    }
    $nupkg = Get-ChildItem $OutDir -Filter "$packId-$version-full.nupkg" | Select-Object -First 1
    if (-not $nupkg) { throw "Icon check: $packId-$version-full.nupkg is missing." }
    $zip = [IO.Compression.ZipFile]::OpenRead($nupkg.FullName)
    try {
        if (-not ($zip.Entries | Where-Object { $_.FullName -match '(^|/)Assets/Upshift\.ico$' })) { throw "Icon check: Assets/Upshift.ico is missing from $($nupkg.Name)." }
        Write-Host "  icon ok: $($nupkg.Name) has Assets/Upshift.ico"
    }
    finally { $zip.Dispose() }

    Write-Host "Release files in ${OutDir}:"
    Get-ChildItem $OutDir -File | ForEach-Object { Write-Host ("  {0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
}
finally {
    Pop-Location
}
