; Inno Setup script for the Speckle Tekla Structures connector.
;
; Expects the repo's zipped build output to already exist at
; ..\..\output\teklastructures\Speckle.Connector.Tekla{year}\ for each supported
; year (produced by `./build.ps1 zip` from the repo root - see Build/Program.cs's
; ZIP target and Build/Consts.cs's "teklastructures" project group).
;
; Mirrors the file layout that Connectors/Tekla/Directory.Build.targets already
; deploys for local dev builds: extension DLLs/resources under
; ...\Environments\common\extensions\SpeckleConverterTeklaStructures\, the ribbon
; XML + icon under ...\Environments\common\system\Ribbons\CustomTabs\Modeling\,
; and the toolbar bitmap under ...\Bitmaps\. Only targets the %ProgramData%
; install layout (Directory.Build.targets also falls back to a direct
; C:\TeklaStructures\{year}.0 path for very old-style installs - not covered here).
;
; Runs without elevation - Tekla's own installer already grants standard
; users write access to this %ProgramData% tree, since its environment
; customization system has to work without admin rights.
;
; Pass /DMyAppVersion=x.y.z on the ISCC command line to stamp a real version;
; defaults to 0.0.0 for ad-hoc local builds.
;
; NOTE: each [Files]/[Components] entry below must stay on a single physical
; line - Inno Setup's declarative sections don't support line continuation.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "Speckle Converter for Tekla Structures"
#define MyAppPublisher "Eugen Chladny"
#define OutputDir "..\..\output\installers"
#define SourceRoot "..\..\output\teklastructures"

[Setup]
AppId={{4F2C8E90-9C61-4E2A-8C36-2F7B6A0B9E44}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\SpeckleConverterTekla
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=no
; Deploy target is %ProgramData%\Trimble\Tekla Structures\..., but Tekla's own
; installer already grants standard users write access to that tree (it has
; to - most Tekla users aren't local admins, and Tekla's environment
; customization system is designed to work without elevation). Confirmed
; empirically: a non-elevated `dotnet build` earlier successfully wrote into
; this exact path via Directory.Build.targets' AfterBuildTekla target.
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=SpeckleConverterTekla-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=speckle-converter.ico
UninstallDisplayIcon={app}\speckle-converter.ico
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nCreated by Eugen Chladny, built with Claude Code.%n%nIt is recommended that you close all other applications before continuing.

[Types]
Name: "full"; Description: "Install for all detected Tekla Structures versions"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "tekla2023"; Description: "Tekla Structures 2023"; Types: full custom; Check: IsTeklaYearInstalled('2023')
Name: "tekla2024"; Description: "Tekla Structures 2024"; Types: full custom; Check: IsTeklaYearInstalled('2024')
Name: "tekla2025"; Description: "Tekla Structures 2025"; Types: full custom; Check: IsTeklaYearInstalled('2025')

[Code]
function AnyTeklaDetected(): Boolean;
begin
  Result := DirExists(ExpandConstant('{commonappdata}\Trimble\Tekla Structures\2023.0'))
    or DirExists(ExpandConstant('{commonappdata}\Trimble\Tekla Structures\2024.0'))
    or DirExists(ExpandConstant('{commonappdata}\Trimble\Tekla Structures\2025.0'));
end;

function IsTeklaYearInstalled(Year: String): Boolean;
begin
  if AnyTeklaDetected() then
    Result := DirExists(ExpandConstant('{commonappdata}\Trimble\Tekla Structures\' + Year + '.0'))
  else
    Result := True; // nothing detected - offer every version, let the user choose
end;

[Files]
Source: "speckle-converter.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\Speckle.Connector.Tekla2023\*"; DestDir: "{commonappdata}\Trimble\Tekla Structures\2023.0\Environments\common\extensions\SpeckleConverterTeklaStructures"; Excludes: "*.bmp"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: tekla2023
Source: "{#SourceRoot}\Speckle.Connector.Tekla2023\Resources\et_element_SpeckleConverter.bmp"; DestDir: "{commonappdata}\Trimble\Tekla Structures\2023.0\Bitmaps"; Flags: ignoreversion; Components: tekla2023
Source: "{#SourceRoot}\Speckle.Connector.Tekla2023\Resources\SpeckleConverter-Ribbon.xml"; DestDir: "{commonappdata}\Trimble\Tekla Structures\2023.0\Environments\common\system\Ribbons\CustomTabs\Modeling"; Flags: ignoreversion; Components: tekla2023
Source: "{#SourceRoot}\Speckle.Connector.Tekla2023\Resources\speckle-converter.svg"; DestDir: "{commonappdata}\Trimble\Tekla Structures\2023.0\Environments\common\system\Ribbons\CustomTabs\Modeling"; Flags: ignoreversion; Components: tekla2023

Source: "{#SourceRoot}\Speckle.Connector.Tekla2024\*"; DestDir: "{commonappdata}\Trimble\Tekla Structures\2024.0\Environments\common\extensions\SpeckleConverterTeklaStructures"; Excludes: "*.bmp"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: tekla2024
Source: "{#SourceRoot}\Speckle.Connector.Tekla2024\Resources\et_element_SpeckleConverter.bmp"; DestDir: "{commonappdata}\Trimble\Tekla Structures\2024.0\Bitmaps"; Flags: ignoreversion; Components: tekla2024
Source: "{#SourceRoot}\Speckle.Connector.Tekla2024\Resources\SpeckleConverter-Ribbon.xml"; DestDir: "{commonappdata}\Trimble\Tekla Structures\2024.0\Environments\common\system\Ribbons\CustomTabs\Modeling"; Flags: ignoreversion; Components: tekla2024
Source: "{#SourceRoot}\Speckle.Connector.Tekla2024\Resources\speckle-converter.svg"; DestDir: "{commonappdata}\Trimble\Tekla Structures\2024.0\Environments\common\system\Ribbons\CustomTabs\Modeling"; Flags: ignoreversion; Components: tekla2024

Source: "{#SourceRoot}\Speckle.Connector.Tekla2025\*"; DestDir: "{commonappdata}\Trimble\Tekla Structures\2025.0\Environments\common\extensions\SpeckleConverterTeklaStructures"; Excludes: "*.bmp"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: tekla2025
Source: "{#SourceRoot}\Speckle.Connector.Tekla2025\Resources\et_element_SpeckleConverter.bmp"; DestDir: "{commonappdata}\Trimble\Tekla Structures\2025.0\Bitmaps"; Flags: ignoreversion; Components: tekla2025
Source: "{#SourceRoot}\Speckle.Connector.Tekla2025\Resources\SpeckleConverter-Ribbon.xml"; DestDir: "{commonappdata}\Trimble\Tekla Structures\2025.0\Environments\common\system\Ribbons\CustomTabs\Modeling"; Flags: ignoreversion; Components: tekla2025
Source: "{#SourceRoot}\Speckle.Connector.Tekla2025\Resources\speckle-converter.svg"; DestDir: "{commonappdata}\Trimble\Tekla Structures\2025.0\Environments\common\system\Ribbons\CustomTabs\Modeling"; Flags: ignoreversion; Components: tekla2025

; Suppresses the DUI3 panel's "Update available" banner, which otherwise points users at the
; official specklesystems releases - not applicable to this fork. Read by
; GlobalConfigResolver.GetIsUpdateNotificationDisabled() (checks HKLM then HKCU); written to HKCU
; since this installer deliberately runs without admin elevation.
[Registry]
Root: HKCU; Subkey: "Software\Speckle\Connector Config\Global"; ValueType: string; ValueName: "SPECKLE_IS_UPDATE_NOTIFICATION_DISABLED"; ValueData: "true"; Flags: uninsdeletevalue
