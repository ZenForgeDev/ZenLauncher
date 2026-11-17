# ZenLauncher Dev – endringsliste (versjon 0.2.0.0)

OBS: Alt er ukompilert og utestet. Bygg med build.bat (valg 1, appen helt lukket) og send feilmeldinger.

## Nytt
- Oppdateringssjekk mot GitHub (releases/latest), «Installer nå» med sha256-sjekk
- Versjonsnummer i innstillinger, bygg-spørsmål om versjon i build.bat
- Wake-on-LAN for streaming-verter (auto-vekk, «Vekk»-knapp, MAC-felt, MAC slås opp via arp)
- Begrens kvalitet over Tailscale (valgfritt)
- «Fortsett sist»-knapp for streaming
- Kopier adresse / brukernavn / passord for verter
- Ny side «Verktøy og info»: Hva er nytt, feilsøkingsinfo, åpne loggmappe, automatisk sikkerhetskopi av innstillinger (maks 10, uten passord/API-nøkler) og gjenoppretting, statistikk (topp 5, 7/30 dager, streak), smarte lister (aldri spilt, >6 mnd, <2 t), duplikater mellom butikker, lagringsbruk
- Toast ved første oppstart etter oppdatering
- Pad-mus pauses under streaming

## Ikke bygget (og hvorfor)
- Steam-venner (krever personlig API-nøkkel – skal ikke lagres i kode)
- Discord-deling, pris-/ønskelistevarsler, IGDB, anonym krasjrapport (eksterne tjenester/nøkler)
- Foreldrekontroll med PIN, temaer/språk/skjermsparer, PlayStation/Switch-glyfer, Xbox-knapp-meny, kontrollerbatteri-varsler, mobil-fjernkontroll, per-spill startargumenter/admin, per-spill volum, kø-lagring, synk desktop↔Ally, multi-monitor-valg, streaming-statistikk-overlay (Moonlight-flagg ikke verifisert)
  → store/uverifiserte funksjoner; best tatt én og én etter at 0.2 kompilerer.

## Filer endret/nye
SettingsStore.cs, WolService.cs (ny), ToolsService.cs (ny), UpdateService.cs, MainWindow.Tools.cs (ny), MainWindow.Updates.cs, MainWindow.Settings.cs, MainWindow.xaml.cs, MainWindow.Streaming.cs, Package.appxmanifest (0.2.0.0), installer .iss (0.2)
