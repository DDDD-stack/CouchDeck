<#
    Builds CouchDeck as one self-contained .exe (no .NET install needed), installs it for the
    current user, adds a desktop shortcut and registers it to start with Windows in the
    background (tray only, waiting for the Xbox button).

        powershell -ExecutionPolicy Bypass -File tools\install.ps1            # build + install + start in background
        powershell -ExecutionPolicy Bypass -File tools\install.ps1 -NoLaunch  # don't start it now

    Installs to   %LocalAppData%\Programs\CouchDeck\CouchDeck.exe
    Settings stay in %AppData%\CouchDeck (config, covers, sounds), so reinstalling keeps everything.
    Uninstall: run with -Uninstall.
#>
param(
    [switch]$NoLaunch,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\CouchDeck'
$exe = Join-Path $installDir 'CouchDeck.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'CouchDeck.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

# A running copy holds the file open (and the single-instance lock): stop it first.
function Stop-CouchDeck {
    Get-Process -Name CouchDeck -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "Stopping running CouchDeck ($($_.Path))"
        $_ | Stop-Process -Force
        $_.WaitForExit(5000) | Out-Null
    }
}

if ($Uninstall) {
    Stop-CouchDeck
    Remove-ItemProperty -Path $runKey -Name CouchDeck -ErrorAction SilentlyContinue
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'CouchDeck uninstalled (settings in %AppData%\CouchDeck were kept).'
    return
}

$publishDir = Join-Path $root 'publish'
Write-Host 'Building single-file CouchDeck.exe...'
# Not compressed on purpose: compression saves ~85 MB of disk but costs ~75 MB of RAM for as long
# as CouchDeck sits in the tray (assemblies get unpacked into memory instead of mapped from disk).
dotnet publish (Join-Path $root 'CouchDeck.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Stop-CouchDeck
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item (Join-Path $publishDir 'CouchDeck.exe') $exe -Force
Write-Host "Installed $exe"

# Desktop shortcut (opens the overlay; if CouchDeck is already running it just shows it)
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $installDir
$link.IconLocation = "$exe,0"
$link.Description = 'CouchDeck - press the Xbox button'
$link.Save()
Write-Host "Shortcut  $shortcut"

# Start with Windows, in the background. (CouchDeck also re-registers itself on every start
# while "Start with Windows" is on in Customize > Controller.)
Set-ItemProperty -Path $runKey -Name CouchDeck -Value "`"$exe`" --background"
Write-Host 'Starts with Windows in the background'

if (-not $NoLaunch) {
    Start-Process -FilePath $exe -ArgumentList '--background' -WorkingDirectory $installDir
    Write-Host 'CouchDeck is running in the tray. Press the Xbox / PS button.'
}
