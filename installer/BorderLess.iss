#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef OutputBase
  #define OutputBase "BorderLess-" + AppVersion + "-setup-win-x64"
#endif
#ifndef AppIdentity
  #define AppIdentity "BorderLess.Desktop"
#endif
#ifndef ProductName
  #define ProductName "BorderLess"
#endif

[Setup]
AppId={#AppIdentity}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher=BorderLess
DefaultDirName={autopf}\BorderLess
DefaultGroupName={#ProductName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBase}
SetupIconFile=..\src\BorderLess.App\Assets\BorderLess.ico
UninstallDisplayIcon={app}\BorderLess.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
InfoBeforeFile=INSTALL_NOTES.txt
LicenseFile=..\LICENSE

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
spanish.DesktopIcon=Crear un acceso directo en el escritorio
english.DesktopIcon=Create a desktop shortcut
spanish.Launch=Abrir BorderLess
english.Launch=Open BorderLess
spanish.ImportTitle=Configuración existente
english.ImportTitle=Existing configuration
spanish.ImportDescription=Conservar los vínculos de una versión portable
english.ImportDescription=Keep pairings from a portable version
spanish.ImportInfo=Elige una instalación nueva o importa la carpeta data de este mismo PC y usuario. Los archivos recibidos no se mueven.
english.ImportInfo=Start fresh or import the data folder from this same PC and user. Received files will not be moved.
spanish.Fresh=Instalación nueva
english.Fresh=New installation
spanish.Import=Importar configuración portable de este PC
english.Import=Import this PC's portable configuration
spanish.ImportFolder=Selecciona la carpeta data de la versión portable
english.ImportFolder=Select the portable version's data folder
spanish.ImportBad=La carpeta debe contener settings.json. No selecciones la carpeta de otro PC.
english.ImportBad=The folder must contain settings.json. Do not select another PC's folder.
spanish.ImportFailed=No se pudo importar la configuración. Los originales se conservaron. Consulta import-error.log en la carpeta de datos instalada antes de abrir BorderLess.
english.ImportFailed=Configuration could not be imported. Originals were kept. Read import-error.log in the installed data folder before opening BorderLess.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "data\*,*.pdb,manifest.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installed-mode.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\BorderLess.exe"; Parameters: "{code:LaunchParameters}"; AppUserModelID: "BorderLess.Desktop"
Name: "{autodesktop}\{#ProductName}"; Filename: "{app}\BorderLess.exe"; Parameters: "{code:LaunchParameters}"; Tasks: desktopicon; AppUserModelID: "BorderLess.Desktop"

[Run]
Filename: "{app}\BorderLess.exe"; Parameters: "{code:LaunchParameters}"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent unchecked; Check: CanLaunch

[Code]
var
  ImportChoice: TInputOptionWizardPage;
  ImportFolder: TInputDirWizardPage;
  ImportFailed: Boolean;

function DataDirectory: String;
begin
#ifdef ProfileOverride
  Result := '{#ProfileOverride}';
#else
  Result := ExpandConstant('{localappdata}\BorderLess\data');
#endif
end;

function LaunchParameters(Param: String): String;
begin
#ifdef ProfileOverride
  Result := '--data "' + DataDirectory + '"';
#else
  Result := '';
#endif
end;

procedure InitializeWizard;
begin
  ImportChoice := CreateInputOptionPage(wpSelectDir, CustomMessage('ImportTitle'), CustomMessage('ImportDescription'), CustomMessage('ImportInfo'), True, False);
  ImportChoice.Add(CustomMessage('Fresh'));
  ImportChoice.Add(CustomMessage('Import'));
  ImportChoice.SelectedValueIndex := 0;
  ImportFolder := CreateInputDirPage(ImportChoice.ID, CustomMessage('ImportTitle'), CustomMessage('ImportDescription'), CustomMessage('ImportFolder'), False, '');
  ImportFolder.Add('data:');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PageID = ImportChoice.ID) and FileExists(DataDirectory + '\settings.json')) or
            ((PageID = ImportFolder.ID) and ((ImportChoice.SelectedValueIndex <> 1) or FileExists(DataDirectory + '\settings.json')));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ImportFolder.ID) and not FileExists(AddBackslash(ImportFolder.Values[0]) + 'settings.json') then begin
    MsgBox(CustomMessage('ImportBad'), mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var ExitCode: Integer;
begin
  if (CurStep = ssPostInstall) and (ImportChoice.SelectedValueIndex = 1) and not FileExists(DataDirectory + '\settings.json') then begin
    if not Exec(ExpandConstant('{app}\BorderLess.exe'), '--data "' + DataDirectory + '" --import-profile "' + ImportFolder.Values[0] + '"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      ImportFailed := True
    else ImportFailed := ExitCode <> 0;
    if ImportFailed then MsgBox(CustomMessage('ImportFailed'), mbError, MB_OK);
  end;
end;

function CanLaunch: Boolean;
begin
  Result := not ImportFailed;
end;
