# ZenStationShell (ZSS)

Egen PS5-inspirert spill-hjemskjerm for Windows 11. Registrerer seg som **hjem-app** i Xbox mode (Innstillinger > Spill > Xbox mode > Choose home app), akkurat som OmniConsole og Winhanced.

> Status: **v0.2 (apper, meny, lyder, søk, favoritter) er skrevet uten kompilator.** Koden er skrevet uten kompilator. Forvent å fikse noen småfeil i første bygg. Send meg feilmeldingene, så rettes de.

## Hva som er med
- Hjem-app-registrering: manifest med `windows.gamingApp`, capability `gamingHome` og `CustomCapability.SCCD`.
- Fullskjerm hjemskjerm: hero-bakgrunn, spillrad med cover-art, klokke.
- Steam-skanning (installerte spill, sist spilt-sortering, cover/hero fra Steams cache, ellers Steams CDN).
- Kontroller: D-pad/stikke = bla, **A** = spill/åpne, **B** = tilbake, **Y** = favoritt, **LB/RB** = kategori (Alle / Favoritter / Sist spilt / Apper), **Menu** = meny.
- Tastatur: piler, Enter, Esc (meny/avbryt søk), **skriv for å søke**, F2 = favoritt, PageUp/PageDown = kategori.
- Apper: legg til nettlesere, kjente programmer, nettsider eller valgfri .exe (Meny > Legg til app). Fjernes via menyen.
- Meny: Åpne Windows-innstillinger (ms-settings:), flere snarveier (skjerm, lyd, Bluetooth, nettverk, Game Bar, Xbox mode, Update, Oppgavebehandling), UI-lyder på/av, **lydvolum** (◄ ► i 10 %-steg), avslutt.
- UI-lyder: egne WAV-filer i Assets\Sounds (move/select/back/open/launch), spilles med justerbart volum.
- Innstillinger/favoritter/apper lagres i %LOCALAPPDATA%\ZenStationShell\settings.json.
- Manifest slår av fil-/registervirtualisering (unvirtualizedResources) så nettlesere vi starter får ekte profiler.
- Sømløs spillstart: oppstartsskjerm i ZSS ligger øverst, Steam holdes i bakgrunnen og Steams vinduer minimeres, vi følger `RunningAppID` for å vite når spillet starter og avsluttes.

## Første test (på din PC)
1. Slå på **Utviklermodus**: Innstillinger > System > For utviklere. (Trengs kun for installasjon.)
2. Dobbeltklikk **`build.bat`** (krever .NET SDK 10). Velg 1 for å sjekke at det bygger, 2 for å installere som hjem-app uten å starte, eller 3 for å også starte den i fullskjerm (**Esc** avslutter).
   (Alternativ: `.\scripts\Run-Dev.ps1`.)
3. Åpne Innstillinger > Spill > Xbox mode. Sjekk at **ZenStationShell** står i «Choose home app»-listen.
4. Velg den. Test via Win+F11 eller «Enter Xbox mode on startup».

## Viktig før du avinstallerer
Sett hjem-app tilbake til Xbox/None **først**. Ellers kan Oppgavevisning slutte å virke på noen builds. `scripts\Uninstall.ps1` spør deg om dette.

## Kjente risikoer / ting som må testes
- **Bygg:** Hvis restore/bygg feiler, kjør `dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` og `dotnet new winui -n Test` i en tom mappe, og sammenlign `.csproj` med vår.
- **Sti med parenteser:** Mappen heter «ZenStationShell (ZSS)». Hvis MSBuild klager på stien, flytt prosjektet til f.eks. `C:\Dev\ZSS`.
- **Steam skjult:** Steam må kjøre i bakgrunnen for spill med Steamworks/DRM. Vi skjuler den, vi fjerner den ikke. Steam kan blinke et øyeblikk før den minimeres. Åpner Steam en dialog (oppdatering, skysynk) og spillet ikke starter innen 25 sek, slipper vi Steam fram.
- **Apper med kontroller:** nettlesere og Windows-innstillinger styres foreløpig med mus/tastatur. Kontroller-som-mus er et senere steg.
- **Manifest v0.2:** hvis registrering feiler etter denne versjonen, er det sannsynligvis `desktop6`-linjene i Package.appxmanifest. Kjør build.bat valg 5.
- **Game Bar-knappen:** Xbox/Guide-knappen styres av Game Bar. Om ZSS får den, vet vi ikke ennå.
- **Single instance:** Windows kan starte hjem-appen flere ganger (Game Bar «Home»). Enkel enkeltinstans-håndtering mangler.
- **Lisens:** OmniConsole er PolyForm Noncommercial og er kun brukt som referanse for hvordan registreringen fungerer (offentlige plattformfakta). All ZSS-kode er skrevet fra bunnen.

## Neste steg
1. Bekreft at ZSS vises som hjem-app og starter ved oppstart.
2. Enkeltinstans + «Home» fra Game Bar.
3. Meny (Menu-knapp): hjem-app-bytte (ZSS / Steam Big Picture / Xbox / Playnite), avslutt til skrivebord, strøm, volum.
4. PS5-design: tile-zoom, parallax, lyder, animasjoner.
5. Epic og andre bibliotek, spilletid, ZenTune-integrasjon.
