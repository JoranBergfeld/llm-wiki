# Install or update the `wiki` CLI from the rolling `latest` prerelease.
#
#   irm https://raw.githubusercontent.com/JoranBergfeld/llm-wiki/main/scripts/install.ps1 | iex
#
# Re-running it is the update path: `latest` is recreated at the newest green
# commit on main, so this always fetches that build.
#
# Environment:
#   WIKI_INSTALL_DIR  where to put the binary (default: %LOCALAPPDATA%\Programs\wiki)
#   WIKI_VERSION      release tag to install (default: latest)
#
# Windows PowerShell 5.1 and PowerShell 7+ both work.
$ErrorActionPreference = 'Stop'
# Invoke-WebRequest renders a progress bar that costs more time than the
# download does on 5.1.
$ProgressPreference = 'SilentlyContinue'

$repo = 'JoranBergfeld/llm-wiki'
$tag = if ($env:WIKI_VERSION) { $env:WIKI_VERSION } else { 'latest' }
$installDir = if ($env:WIKI_INSTALL_DIR) { $env:WIKI_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\wiki' }

# CI publishes win-x64 only. Windows on ARM runs x64 binaries under emulation,
# so that asset is the right answer on both architectures.
$asset = 'wiki-win-x64.zip'
$url = "https://github.com/$repo/releases/download/$tag/$asset"

# PowerShell 5.1 defaults to TLS 1.0, which github.com refuses.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("wiki-install-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Write-Host "Downloading $asset from the '$tag' release..."
    $zip = Join-Path $tmp $asset
    try {
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    } catch {
        throw "download failed: $url`n$($_.Exception.Message)"
    }

    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    $staged = Join-Path $tmp 'wiki.exe'
    if (-not (Test-Path $staged)) { throw "archive did not contain wiki.exe" }

    if (-not (Test-Path $installDir)) { New-Item -ItemType Directory -Path $installDir | Out-Null }
    $target = Join-Path $installDir 'wiki.exe'

    # Windows will not let you overwrite a running executable, but it will let
    # you rename one. Move the old binary aside first so an update succeeds
    # even while a `wiki` process is alive; the stale file is swept next run.
    $old = "$target.old"
    if (Test-Path $target) {
        Remove-Item $old -Force -ErrorAction SilentlyContinue
        Move-Item $target $old -Force
    }
    Move-Item $staged $target -Force
    Remove-Item $old -Force -ErrorAction SilentlyContinue

    $version = try { & $target --version } catch { 'unknown' }
    Write-Host "Installed wiki $version to $target"

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $onPath = ($userPath -split ';' | Where-Object { $_ -eq $installDir }).Count -gt 0
    if (-not $onPath) {
        Write-Host ''
        Write-Host "$installDir is not on your PATH. Add it:"
        Write-Host "  [Environment]::SetEnvironmentVariable('Path', `"$installDir;`" + [Environment]::GetEnvironmentVariable('Path','User'), 'User')"
        Write-Host 'Then open a new terminal.'
    }
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
