# ZenTuneLauncher

Egen versjon av ZenStationShell (ZSS) med ZenTune innebygd. Originalene (`C:\Dev\ZSS` og `C:\Dev\ZenTune-Kildekode`) er urørt.

- `src\ZSS` : launcheren (pakkenavn `ZenForge.ZenTuneLauncher`, egen instans, kan installeres ved siden av ZSS)
- `src\ZenTune.Core` : kopi av ZenTunes logikk (GPL-3.0)

## Bygg
```powershell
cd C:\Dev\ZenTuneLauncher
.\build.ps1
```
Velg deretter «ZenTuneLauncher» under Xbox mode > Choose home app (eller start den fra Start-menyen).

## Status
- v0.1: «Optimaliser med ZenTune» i menyen på Steam-spill (Meny / Y-knapp-meny): presets, forhåndsvisning av endringer, automatisk backup, gjenoppretting.
