@echo off
setlocal
cd /d "%~dp0"
title ZenLauncher - build

rem Bruk:  build.bat      (meny)
rem        build.bat 1    (bygg og registrer, som vanlig)
rem        build.bat 2    (bygg setup.exe)
rem        build.bat 1 1.2.3   (med versjon, hopper over spoersmaalet)

set "ARGVER=%~2"
set "APPDIR=%~dp0src\ZSS"
set "LAYOUT=%APPDIR%\bin\x64\Release\net10.0-windows10.0.26100.0\AppX"
set "DIST=%~dp0dist"

where dotnet >nul 2>&1
if errorlevel 1 goto :nodotnet

echo.
echo  ZenLauncher
echo  -----------
echo  .NET SDK:
dotnet --version
echo.

if "%~1"=="1" goto :build
if "%~1"=="2" goto :setup

echo  1  Bygg  (bygger og registrerer appen, som vanlig)
echo  2  Bygg setup.exe  (installer du kan kjore paa en annen PC)
echo.
choice /c 12 /n /m "Velg 1 eller 2: "
if errorlevel 2 goto :setup
goto :build

:build
call :askver
if errorlevel 1 goto :failed
cd /d "%APPDIR%"
echo Bygger og registrerer ZenLauncher uten aa starte den...
echo (Avslutt appen foerst hvis den kjoerer.)
dotnet run -c Release -p:Platform=x64 -p:WinAppRunNoLaunch=true
if errorlevel 1 goto :failed
echo.
echo Ferdig.
goto :end

:setup
call :askver
if errorlevel 1 goto :failed
echo [1/5] Bygger appen (selvstendig, med .NET inkludert)...
cd /d "%APPDIR%"
dotnet run -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:WinAppRunNoLaunch=true
if errorlevel 1 goto :failed
cd /d "%~dp0"
if exist "%APPDIR%\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\AppX\AppxManifest.xml" set "LAYOUT=%APPDIR%\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\AppX"
if not exist "%LAYOUT%\AppxManifest.xml" goto :nolayout
echo   Pakker fra: %LAYOUT%

echo.
echo [2/5] Finner verktoey...
set "MAKEAPPX="
set "SIGNTOOL="
call :findtool makeappx.exe MAKEAPPX
call :findtool signtool.exe SIGNTOOL
if not defined MAKEAPPX goto :notool
if not defined SIGNTOOL goto :notool
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" goto :noinno
echo   makeappx: %MAKEAPPX%
echo   signtool: %SIGNTOOL%
echo   Inno:     %ISCC%

if not exist "%DIST%" mkdir "%DIST%"

echo.
echo [3/5] Sertifikat...
if exist "%DIST%\zenforge.pfx" goto :havecert
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p = ConvertTo-SecureString 'zenforge' -AsPlainText -Force; $c = New-SelfSignedCertificate -Type Custom -Subject 'CN=ZenForge' -KeyUsage DigitalSignature -FriendlyName 'ZenForge dev' -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(10) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}'); Export-PfxCertificate -Cert $c -FilePath '%DIST%\zenforge.pfx' -Password $p | Out-Null; Export-Certificate -Cert $c -FilePath '%DIST%\zenforge.cer' | Out-Null"
if errorlevel 1 goto :failed
echo   Laget nytt sertifikat (CN=ZenForge).
goto :pack
:havecert
echo   Bruker eksisterende sertifikat i dist.

:pack
echo.
echo [4/5] Pakker og signerer MSIX...
if exist "%DIST%\ZenTuneLauncher.msix" del "%DIST%\ZenTuneLauncher.msix"
"%MAKEAPPX%" pack /d "%LAYOUT%" /p "%DIST%\ZenTuneLauncher.msix" /nv /o
if errorlevel 1 goto :failed
"%SIGNTOOL%" sign /fd SHA256 /f "%DIST%\zenforge.pfx" /p zenforge "%DIST%\ZenTuneLauncher.msix"
if errorlevel 1 goto :failed

echo.
echo [5/5] Lager setup.exe...
set "SETUPVER=?"
for /f "delims=" %%V in ('powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-version.ps1" -Get') do set "SETUPVER=%%V"
"%ISCC%" /Qp /DAppVer=%SETUPVER% "%~dp0installer\ZenTuneLauncher.iss"
if errorlevel 1 goto :failed

echo.
echo Ferdig! Setup ligger her:
echo   %DIST%\ZenLauncher-Setup-%SETUPVER%.exe
echo.
echo Merk: Utviklermodus maa vaere paa i Windows paa PC-en der den installeres.
start "" "%DIST%"
goto :end

:askver
rem Viser gjeldende versjon og lar deg skrive inn en ny (Enter = behold). Versjon kan ogsaa gis som 2. parameter.
set "CURVER=?"
for /f "delims=" %%V in ('powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-version.ps1" -Get') do set "CURVER=%%V"
set "NEWVER=%ARGVER%"
if defined NEWVER goto :setver
echo.
set /p "NEWVER=Versjon [%CURVER%] (skriv f.eks. 1.2.3, eller trykk Enter for aa beholde): "
:setver
if not defined NEWVER (
  echo Bruker versjon %CURVER%.
  exit /b 0
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-version.ps1" -Set "%NEWVER%"
exit /b %errorlevel%

:findtool
rem %1 = filnavn, %2 = variabel som settes. Soeker i NuGet-cachen og i Windows SDK.
set "PKGS=%USERPROFILE%\.nuget\packages"
if defined NUGET_PACKAGES set "PKGS=%NUGET_PACKAGES%"
for /f "delims=" %%F in ('dir /s /b "%PKGS%\microsoft.windows.sdk.buildtools\%~1" 2^>nul ^| findstr /i "\\x64\\"') do set "%~2=%%F"
if defined %~2 exit /b 0
for /f "delims=" %%F in ('dir /s /b "%ProgramFiles(x86)%\Windows Kits\10\bin\%~1" 2^>nul ^| findstr /i "\\x64\\"') do set "%~2=%%F"
exit /b 0

:nolayout
echo.
echo Fant ikke den pakkede mappen:
echo   %LAYOUT%
echo Bygget gikk, men AppX-mappen mangler. Send denne meldingen til Claude.
goto :end

:notool
echo.
echo Fant ikke makeappx.exe / signtool.exe (kommer med Windows SDK / NuGet-pakken Microsoft.Windows.SDK.BuildTools).
echo Kjoer valg 1 en gang for aa laste ned pakkene, og proev igjen.
goto :end

:noinno
echo.
echo Fant ikke Inno Setup 6. Installer det fra https://jrsoftware.org/isdl.php og kjoer paa nytt.
goto :end

:nodotnet
echo.
echo Fant ikke .NET SDK. Installer .NET SDK 10 fra https://dotnet.microsoft.com/download og kjoer build.bat paa nytt.
goto :end

:failed
echo.
echo Noe feilet. Kopier feilmeldingene over og send dem til Claude.
echo (Sjekk spesielt linjer med "error".)
goto :end

:end
echo.
pause
endlocal
