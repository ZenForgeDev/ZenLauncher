# Leser og setter versjonsnummeret for ZenTuneLauncher. Kalles fra build.bat.
#   -Get          skriver ut gjeldende versjon (f.eks. 0.1)
#   -Set 1.2.3    setter versjonen i Package.appxmanifest (4 tall) og installer\ZenTuneLauncher.iss
param([switch]$Get, [string]$Set = "")

$ErrorActionPreference = 'Stop'
$man = Join-Path $PSScriptRoot 'src\ZSS\Package.appxmanifest'
$iss = Join-Path $PSScriptRoot 'installer\ZenTuneLauncher.iss'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$rx = '(?s)(<Identity\b[^>]*?\bVersion\s*=\s*")([^"]*)(")'

if ($Get) {
    $m = [regex]::Match([IO.File]::ReadAllText($man), $rx)
    if (-not $m.Success) { Write-Output "?"; exit 0 }
    $p = @($m.Groups[2].Value.Split('.'))
    while ($p.Count -gt 2 -and $p[-1] -eq '0') { $p = $p[0..($p.Count - 2)] }
    Write-Output ($p -join '.')
    exit 0
}

if ($Set -ne "") {
    if ($Set -notmatch '^\d+(\.\d+){1,3}$') { Write-Host "Ugyldig versjon: $Set (bruk f.eks. 1.2 eller 1.2.3)"; exit 1 }
    $parts = @($Set.Split('.'))
    foreach ($x in $parts) { if ([int]$x -gt 65535) { Write-Host "Hvert tall maa vaere 65535 eller lavere."; exit 1 } }
    while ($parts.Count -lt 4) { $parts += '0' }
    $four = $parts -join '.'

    $t = [IO.File]::ReadAllText($man)
    if (-not [regex]::IsMatch($t, $rx)) { Write-Host "Fant ikke Identity Version i Package.appxmanifest."; exit 1 }
    $t = [regex]::Replace($t, $rx, { param($m) $m.Groups[1].Value + $four + $m.Groups[3].Value }, 1)
    [IO.File]::WriteAllText($man, $t, $utf8)

    if (Test-Path $iss) {
        $s = [IO.File]::ReadAllText($iss)
        $s = [regex]::Replace($s, '(?m)^AppVersion=.*$', "AppVersion=$Set")
        [IO.File]::WriteAllText($iss, $s, $utf8)
    }
    Write-Host "Versjon satt til $Set ($four)."
}
