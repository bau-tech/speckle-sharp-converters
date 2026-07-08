; Inno Setup script for the Speckle Revit connector.
;
; Expects the repo's zipped build output to already exist at
; ..\..\output\revit\Speckle.Connectors.Revit{year}\ for each supported year
; (produced by `./build.ps1 zip` from the repo root, which builds each
; connector in Release and copies bin\Release\{TargetFramework}\* there -
; see Build/Program.cs's ZIP target and Build/Consts.cs's "revit" project group).
;
; Build locally with the Inno Setup Compiler (ISCC.exe), or via CI - see
; .github/workflows/release.yml.
;
; Pass /DMyAppVersion=x.y.z on the ISCC command line to stamp a real version;
; defaults to 0.0.0 for ad-hoc local builds.
;
; NOTE: each [Files]/[Components] entry below must stay on a single physical
; line - Inno Setup's declarative sections don't support line continuation.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "Speckle Converter for Revit"
#define MyAppPublisher "Eugen Chladny"
#define OutputDir "..\..\output\installers"
#define SourceRoot "..\..\output\revit"

[Setup]
AppId={{B9C1E9C4-6C7E-4C6B-9C4E-5C7F1B0C7A21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\SpeckleConverterRevit
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=no
; Deploy target is the user's own %AppData%, so no elevation is required.
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=SpeckleConverterRevit-Setup
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
Name: "full"; Description: "Install for all detected Revit versions"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "revit2023"; Description: "Revit 2023"; Types: full custom; Check: IsRevitYearInstalled('2023')
Name: "revit2024"; Description: "Revit 2024"; Types: full custom; Check: IsRevitYearInstalled('2024')
Name: "revit2025"; Description: "Revit 2025"; Types: full custom; Check: IsRevitYearInstalled('2025')
Name: "revit2026"; Description: "Revit 2026"; Types: full custom; Check: IsRevitYearInstalled('2026')
Name: "revit2027"; Description: "Revit 2027"; Types: full custom; Check: IsRevitYearInstalled('2027')

; If none of the standard Autodesk install paths were found (e.g. a non-standard
; install location), fall back to showing every version so the user can still
; pick manually instead of silently installing nothing.
[Code]
function AnyRevitDetected(): Boolean;
begin
  Result := DirExists(ExpandConstant('{pf}\Autodesk\Revit 2023'))
    or DirExists(ExpandConstant('{pf}\Autodesk\Revit 2024'))
    or DirExists(ExpandConstant('{pf}\Autodesk\Revit 2025'))
    or DirExists(ExpandConstant('{pf}\Autodesk\Revit 2026'))
    or DirExists(ExpandConstant('{pf}\Autodesk\Revit 2027'));
end;

function IsRevitYearInstalled(Year: String): Boolean;
begin
  if AnyRevitDetected() then
    Result := DirExists(ExpandConstant('{pf}\Autodesk\Revit ' + Year))
  else
    Result := True; // nothing detected - offer every version, let the user choose
end;

[Files]
Source: "speckle-converter.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceRoot}\Speckle.Connectors.Revit2023\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2023\SpeckleConverter.Revit2023"; Excludes: "Plugin\SpeckleConverter.Revit2023.addin"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: revit2023
Source: "{#SourceRoot}\Speckle.Connectors.Revit2023\Plugin\SpeckleConverter.Revit2023.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2023"; Flags: ignoreversion; Components: revit2023

Source: "{#SourceRoot}\Speckle.Connectors.Revit2024\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024\SpeckleConverter.Revit2024"; Excludes: "Plugin\SpeckleConverter.Revit2024.addin"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: revit2024
Source: "{#SourceRoot}\Speckle.Connectors.Revit2024\Plugin\SpeckleConverter.Revit2024.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2024"; Flags: ignoreversion; Components: revit2024

Source: "{#SourceRoot}\Speckle.Connectors.Revit2025\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025\SpeckleConverter.Revit2025"; Excludes: "Plugin\SpeckleConverter.Revit2025.addin"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: revit2025
Source: "{#SourceRoot}\Speckle.Connectors.Revit2025\Plugin\SpeckleConverter.Revit2025.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Flags: ignoreversion; Components: revit2025

Source: "{#SourceRoot}\Speckle.Connectors.Revit2026\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026\SpeckleConverter.Revit2026"; Excludes: "Plugin\SpeckleConverter.Revit2026.addin"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: revit2026
Source: "{#SourceRoot}\Speckle.Connectors.Revit2026\Plugin\SpeckleConverter.Revit2026.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Flags: ignoreversion; Components: revit2026

Source: "{#SourceRoot}\Speckle.Connectors.Revit2027\*"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2027\SpeckleConverter.Revit2027"; Excludes: "Plugin\SpeckleConverter.Revit2027.addin"; Flags: recursesubdirs createallsubdirs ignoreversion; Components: revit2027
Source: "{#SourceRoot}\Speckle.Connectors.Revit2027\Plugin\SpeckleConverter.Revit2027.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\2027"; Flags: ignoreversion; Components: revit2027

; Suppresses the DUI3 panel's "Update available" banner, which otherwise points users at the
; official specklesystems releases - not applicable to this fork. Read by
; GlobalConfigResolver.GetIsUpdateNotificationDisabled() (checks HKLM then HKCU); written to HKCU
; since this installer deliberately runs without admin elevation.
[Registry]
Root: HKCU; Subkey: "Software\Speckle\Connector Config\Global"; ValueType: string; ValueName: "SPECKLE_IS_UPDATE_NOTIFICATION_DISABLED"; ValueData: "true"; Flags: uninsdeletevalue
