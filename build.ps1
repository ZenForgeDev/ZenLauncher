# Bygger og registrerer ZenTuneLauncher som pakket app (loose layout), uten å starte den.
# Kjør fra PowerShell:  .\build.ps1
# Krever Utviklermodus. Avslutt ZenTuneLauncher først hvis den kjører.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot 'src\ZSS')
Write-Host ".NET SDK: $(dotnet --version)"
dotnet run -c Release -p:Platform=x64 -p:WinAppRunNoLaunch=true
Get-AppxPackage ZenForge.ZenTuneLauncher | Format-List Name, Status, InstallLocation
