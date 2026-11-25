; Loopscape installer (Inno Setup 6). Per-user: no administrator prompt (except Rainmeter's own installer,
; if the user ticks it). Build: .\build\stage.ps1, then  iscc /DAppVersion=1.0.0 installer\Loopscape.iss
;
; What it changes (README "What it changes on your PC"):
;   %LOCALAPPDATA%\Programs\Loopscape     the program (Loopscape.exe, mpv, styles.json)
;   %USERPROFILE%\Loopscape                 videos, stills, Loopscape.ini (kept on upgrade)
;   Documents\Rainmeter\Skins\LaptopInfo        the widgets (Settings.inc kept on upgrade)
;   HKCU\...\CurrentVersion\Run "Loopscape" only with "Start with Windows" (also a tray menu toggle)
;   Settings > Apps entry with an uninstaller

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define Stage "..\build\stage"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
AppId={{6B3E2F1C-8A4D-4E57-9C1B-2D7F4A9E0C53}
AppName=Loopscape
AppVersion={#AppVersion}
AppVerName=Loopscape {#AppVersion}
AppPublisher=choco-mazapan
AppPublisherURL=https://github.com/choco-mazapan/loopscape
AppSupportURL=https://github.com/choco-mazapan/loopscape/issues
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={autopf}\Loopscape
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
OutputDir=..\build
OutputBaseFilename=Loopscape-Setup-{#AppVersion}
SetupIconFile=..\rainmeter\Loopscape.ico
UninstallDisplayIcon={app}\Loopscape.exe
UninstallDisplayName=Loopscape
WizardStyle=modern
; the videos are already compressed
Compression=lzma2/fast
SolidCompression=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Loopscape is asked to quit (Loopscape.exe --quit) instead of Restart Manager closing it
CloseApplications=no

[Messages]
WizardSelectComponents=Wallpaper styles
SelectComponentsDesc=Which other styles should Loopscape download?
SelectComponentsLabel2=Riso, Shore, Pixel clouds and Aurora are included. Loopscape can download the others in the background after setup. Untick any you don't want; you can still pick them later and they download then.

[Types]
Name: all; Description: "All styles: download the other 15 after setup (about 250 MB)"
Name: included; Description: "Only the 4 included styles (others download when you pick them)"
Name: custom; Description: "Choose which styles to download"; Flags: iscustom

; Riso, Shore, Pixel clouds and Aurora come with the installer. These are downloaded by Loopscape after setup, in the
; background, from this project's GitHub release (each checked against styles.json). Unticked ones can still be
; picked later; they download then. Sizes match installer\styles.json.
[Components]
Name: dl; Description: "Download these styles after setup"; Types: all
Name: dl\spectrum;    Description: "Spectrum";       ExtraDiskSpaceRequired: 18436846; Types: all
Name: dl\halftone;    Description: "Halftone";       ExtraDiskSpaceRequired: 10207919; Types: all
Name: dl\bluesky;     Description: "Blue sky";       ExtraDiskSpaceRequired: 11762283; Types: all
Name: dl\sunset;      Description: "Sunset strata";  ExtraDiskSpaceRequired: 24502708; Types: all
Name: dl\dunes;       Description: "Dunes";          ExtraDiskSpaceRequired: 11056934; Types: all
Name: dl\lake;        Description: "Lake";           ExtraDiskSpaceRequired: 12409947; Types: all
Name: dl\pastel;      Description: "Pastel aurora";  ExtraDiskSpaceRequired: 24144228; Types: all
Name: dl\crt;         Description: "CRT test card";  ExtraDiskSpaceRequired: 6917047; Types: all
Name: dl\nightclouds; Description: "Night clouds (night)";  ExtraDiskSpaceRequired: 8785132;  Types: all
Name: dl\nebula;      Description: "Nebula (night)";        ExtraDiskSpaceRequired: 23030997; Types: all
Name: dl\moon;        Description: "Halftone moon (night)"; ExtraDiskSpaceRequired: 15479207; Types: all
Name: dl\embers;      Description: "Embers (night)";        ExtraDiskSpaceRequired: 27144401; Types: all
Name: dl\deepsea;     Description: "Deep sea (night)";      ExtraDiskSpaceRequired: 16241883; Types: all
Name: dl\rain;        Description: "Rainy window (night)";  ExtraDiskSpaceRequired: 38429511; Types: all
Name: dl\bounce;      Description: "Bouncing logo (night)"; ExtraDiskSpaceRequired: 13732567; Types: all

[Tasks]
Name: autostart; Description: "Start Loopscape when I sign in to Windows"
#ifexist Stage + "\redist\Rainmeter-setup.exe"
Name: rainmeter; Description: "Install Rainmeter (free and open source; runs the clock and Wi-Fi widgets). Windows will ask for permission."; Check: not RainmeterInstalled
#endif

[Files]
; program
Source: "{#Stage}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; data: settings only if missing; videos and stills are left in place on uninstall unless the user says otherwise
Source: "{#Stage}\data\Loopscape.ini"; DestDir: "{%USERPROFILE}\Loopscape"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#Stage}\data\*"; Excludes: "Loopscape.ini"; DestDir: "{%USERPROFILE}\Loopscape"; Flags: ignoreversion recursesubdirs createallsubdirs uninsneveruninstall
; widgets: Settings.inc only if missing (it holds the user's choices); fonts only if missing (Rainmeter locks them)
Source: "{#Stage}\skin\@Resources\Settings.inc"; DestDir: "{userdocs}\Rainmeter\Skins\LaptopInfo\@Resources"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#Stage}\skin\@Resources\Fonts\*"; DestDir: "{userdocs}\Rainmeter\Skins\LaptopInfo\@Resources\Fonts"; Flags: onlyifdoesntexist
Source: "{#Stage}\skin\*"; Excludes: "\@Resources\Settings.inc,\@Resources\Fonts\*"; DestDir: "{userdocs}\Rainmeter\Skins\LaptopInfo"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifexist Stage + "\redist\Rainmeter-setup.exe"
Source: "{#Stage}\redist\Rainmeter-setup.exe"; Flags: dontcopy
#endif

[UninstallDelete]
; files the program writes next to itself
Type: files; Name: "{app}\*.log"

[Code]
var
  CityPage: TInputQueryWizardPage;
  UseWindowsLocation: TNewCheckBox;

function SettingsInc: String;
begin
  Result := ExpandConstant('{userdocs}\Rainmeter\Skins\LaptopInfo\@Resources\Settings.inc');
end;

function RainmeterExe: String;
var
  dir: String;
begin
  Result := '';
  if RegQueryStringValue(HKLM64, 'SOFTWARE\Rainmeter', '', dir) and FileExists(AddBackslash(dir) + 'Rainmeter.exe') then
    Result := AddBackslash(dir) + 'Rainmeter.exe'
  else if FileExists(ExpandConstant('{commonpf64}\Rainmeter\Rainmeter.exe')) then
    Result := ExpandConstant('{commonpf64}\Rainmeter\Rainmeter.exe')
  else if FileExists(ExpandConstant('{commonpf32}\Rainmeter\Rainmeter.exe')) then
    Result := ExpandConstant('{commonpf32}\Rainmeter\Rainmeter.exe');
end;

function RainmeterInstalled: Boolean;
begin
  Result := RainmeterExe <> '';
end;

function RainmeterRunning: Boolean;
begin
  Result := FindWindowByClassName('DummyRainWClass') <> 0;
end;

// Asks a running Loopscape to stop, then makes sure it's gone
procedure StopLoopscape;
var
  rc: Integer;
begin
  if FileExists(ExpandConstant('{app}\Loopscape.exe')) then
    Exec(ExpandConstant('{app}\Loopscape.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, rc);
  // in case it didn't exit
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Loopscape.exe /F', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

procedure InitializeWizard;
var
  city: String;
begin
  CityPage := CreateInputQueryPage(wpSelectTasks, 'Weather', 'Which city should the weather widget show?',
    'Type your city, for example "Paris" or "Paris, France". The widget gets the weather for it from Open-Meteo ' +
    '(free, no account). You can change it later: right-click a widget > Settings... > Widgets.');
  CityPage.Add('City:', False);
  city := '';
  if FileExists(SettingsInc) then city := GetIniString('Variables', 'WeatherCity', '', SettingsInc);
  CityPage.Values[0] := city;

  UseWindowsLocation := TNewCheckBox.Create(CityPage);
  UseWindowsLocation.Parent := CityPage.Surface;
  UseWindowsLocation.Top := CityPage.Edits[0].Top + CityPage.Edits[0].Height + ScaleY(16);
  UseWindowsLocation.Width := CityPage.SurfaceWidth;
  UseWindowsLocation.Height := ScaleY(17);
  UseWindowsLocation.Caption := 'Use my Windows location instead (Windows asks for permission)';
  UseWindowsLocation.Checked := FileExists(SettingsInc) and
    (CompareText(GetIniString('Variables', 'WeatherLocation', 'city', SettingsInc), 'windows') = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopLoopscape;
  Result := '';
end;

// Rainmeter.ini: the two widgets active. Written before Rainmeter first runs, so a fresh Rainmeter starts with
// these widgets instead of its sample skins.
procedure ActivateWidgetsInIni;
var
  ini: String;
begin
  ini := ExpandConstant('{userappdata}\Rainmeter\Rainmeter.ini');
  if not FileExists(ini) then
  begin
    ForceDirectories(ExtractFileDir(ini));
    SaveStringToFile(ini, '[Rainmeter]' + #13#10 + #13#10, False);
  end;
  SetIniString('LaptopInfo\Clock', 'Active', '1', ini);
  SetIniString('LaptopInfo\WiFi', 'Active', '1', ini);
end;

procedure WriteDownloadList;
var
  names: TArrayOfString;
  i: Integer;
  list, path: String;
begin
  names := ['spectrum', 'halftone', 'bluesky', 'sunset', 'dunes', 'lake', 'pastel', 'crt',
            'nightclouds', 'nebula', 'moon', 'embers', 'deepsea', 'rain', 'bounce'];
  list := '';
  for i := 0 to GetArrayLength(names) - 1 do
    if WizardIsComponentSelected('dl\' + names[i]) then list := list + names[i] + #13#10;
  path := ExpandConstant('{%USERPROFILE}\Loopscape\download-styles.txt');
  if list <> '' then SaveStringToFile(path, list, False)
  else DeleteFile(path);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  rc: Integer;
  city, rm: String;
begin
  if CurStep <> ssPostInstall then Exit;

  // weather city (letters, digits, spaces and , . ' - are all WeatherFetch uses)
  city := Trim(CityPage.Values[0]);
  if city <> '' then SetIniString('Variables', 'WeatherCity', city, SettingsInc);
  if UseWindowsLocation.Checked then SetIniString('Variables', 'WeatherLocation', 'windows', SettingsInc)
  else SetIniString('Variables', 'WeatherLocation', 'city', SettingsInc);

  // start with Windows
  if WizardIsTaskSelected('autostart') then
    RegWriteStringValue(HKCU, '{#RunKey}', 'Loopscape', '"' + ExpandConstant('{app}\Loopscape.exe') + '"')
  else
    RegDeleteValue(HKCU, '{#RunKey}', 'Loopscape');

  // widgets
  if not RainmeterRunning then ActivateWidgetsInIni;
#ifexist Stage + "\redist\Rainmeter-setup.exe"
  if WizardIsTaskSelected('rainmeter') then
  begin
    WizardForm.StatusLabel.Caption := 'Installing Rainmeter...';
    ExtractTemporaryFile('Rainmeter-setup.exe');
    // Rainmeter's official installer, silent, starting with Windows; it asks for administrator permission itself
    if not ShellExec('', ExpandConstant('{tmp}\Rainmeter-setup.exe'), '/S /AUTOSTARTUP=1', '', SW_SHOW, ewWaitUntilTerminated, rc) then
      MsgBox('Rainmeter could not be installed (' + SysErrorMessage(rc) + '). The wallpaper works without it; ' +
             'get Rainmeter from rainmeter.net for the widgets.', mbInformation, MB_OK);
  end;
#endif
  rm := RainmeterExe;
  if rm <> '' then
  begin
    if RainmeterRunning then
    begin
      Exec(rm, '!ActivateConfig "LaptopInfo\Clock" "Clock.ini"', '', SW_HIDE, ewWaitUntilTerminated, rc);
      Exec(rm, '!ActivateConfig "LaptopInfo\WiFi" "WiFi.ini"', '', SW_HIDE, ewWaitUntilTerminated, rc);
      Exec(rm, '!Refresh "LaptopInfo\Clock"', '', SW_HIDE, ewWaitUntilTerminated, rc);
    end
    else
    begin
      ActivateWidgetsInIni;   // again: Rainmeter's installer may have written its own
      ExecAsOriginalUser(rm, '', '', SW_SHOWNORMAL, ewNoWait, rc);
    end;
  end;

  // styles to download (Loopscape reads download-styles.txt when it starts and deletes it once they're all on disk)
  WriteDownloadList;

  // the wallpaper
  ExecAsOriginalUser(ExpandConstant('{app}\Loopscape.exe'), '', '', SW_SHOWNORMAL, ewNoWait, rc);
end;

function InitializeUninstall: Boolean;
var
  rc: Integer;
  rm, ini: String;
begin
  StopLoopscape;
  rm := RainmeterExe;
  if (rm <> '') and RainmeterRunning then
  begin
    Exec(rm, '!DeactivateConfig "LaptopInfo\Clock"', '', SW_HIDE, ewWaitUntilTerminated, rc);
    Exec(rm, '!DeactivateConfig "LaptopInfo\WiFi"', '', SW_HIDE, ewWaitUntilTerminated, rc);
    Exec(rm, '!DeactivateConfig "LaptopInfo\Settings"', '', SW_HIDE, ewWaitUntilTerminated, rc);
    Sleep(1000);   // let Rainmeter release the skin's fonts
  end
  else
  begin
    ini := ExpandConstant('{userappdata}\Rainmeter\Rainmeter.ini');
    if FileExists(ini) then
    begin
      SetIniString('LaptopInfo\Clock', 'Active', '0', ini);
      SetIniString('LaptopInfo\WiFi', 'Active', '0', ini);
      SetIniString('LaptopInfo\Settings', 'Active', '0', ini);
    end;
  end;
  RegDeleteValue(HKCU, '{#RunKey}', 'Loopscape');
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  if SuppressibleMsgBox('Also delete your wallpaper videos and widget settings?' + #13#10 + #13#10 +
       ExpandConstant('{%USERPROFILE}\Loopscape') + #13#10 + ExpandConstant('{userdocs}\Rainmeter\Skins\LaptopInfo') + #13#10 + #13#10 +
       'Choose No to keep them for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
  begin
    DelTree(ExpandConstant('{%USERPROFILE}\Loopscape'), True, True, True);
    DelTree(ExpandConstant('{userdocs}\Rainmeter\Skins\LaptopInfo'), True, True, True);
  end;
  // Rainmeter itself stays installed (it may run other skins); remove it from Settings > Apps if you like
end;
