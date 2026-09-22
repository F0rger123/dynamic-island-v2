; Per-user Inno Setup installer. Build with ISCC.exe on Windows.
#define AppName "Dynamic Island"
#define AppVersion "2.0.0"
#define AppPublisher "F0rger123"
#define AppExeName "DynamicIslandBridge.exe"
[Setup]
AppId={{D5B6D2D6-7E3A-4EE7-9E07-3A65B3F4E1A1}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\DynamicIsland
PrivilegesRequired=lowest
OutputBaseFilename=DynamicIslandSetup
Compression=lzma
SolidCompression=yes
Uninstallable=yes
[Files]
Source: "artifacts\DynamicIslandBridge\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
[Icons]
Name: "{userprograms}\Dynamic Island\Dynamic Island Setup"; Filename: "{app}\Setup.exe"
Name: "{userprograms}\Dynamic Island\Dynamic Island Bridge"; Filename: "{app}\DynamicIslandBridge.exe"
[Run]
Filename: "{app}\Setup.exe"; Description: "Open Dynamic Island Setup"; Flags: postinstall nowait skipifsilent
