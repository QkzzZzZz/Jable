; Jable 安装包（Inno Setup 6）。
; 打包：先运行 installer\build.ps1（自包含发布到 publish\Jable，再调用 ISCC）。
; - 软件自带 .NET 运行时，新电脑不用另装 .NET
; - 没有 WebView2 运行时（少数 Windows 10）时，安装过程中自动装上（需要联网）
; - 装到当前用户目录，不需要管理员权限；开始菜单 + 桌面图标；可在「设置 → 应用」里卸载
; - 卸载不删除观看记录、片单等数据（%LOCALAPPDATA%\JableLens）

#define AppName "Jable"
#define AppVersion "1.0.7"
#define AppExe "Jable.exe"

[Setup]
AppId={{6B1F2C7E-4D3A-4E8B-9C21-7A5D0F3B8E42}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupIconFile=..\src\JableLens.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=..\release
OutputBaseFilename=Jable-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; 软件开着时安装 / 卸载：先请它退出（单实例互斥量）
#ifndef NoMutex
AppMutex=JableLens.SingleInstance
#endif
CloseApplications=yes

[Languages]
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "在桌面创建图标"; GroupDescription: "附加图标:"

[Files]
Source: "..\publish\Jable\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "正在安装 Microsoft WebView2 运行时…"; Check: NeedsWebView2; Flags: waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "启动 Jable"; Flags: nowait postinstall skipifsilent

[Code]
const
  WebView2Key = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function HasVersion(Root: Integer; Key: String): Boolean;
var
  V: String;
begin
  Result := RegQueryStringValue(Root, Key, 'pv', V) and (V <> '') and (V <> '0.0.0.0');
end;

{ WebView2 运行时装在机器级（64 位系统在 WOW6432Node 下）或当前用户下 }
function NeedsWebView2: Boolean;
begin
  Result := not (HasVersion(HKLM, 'SOFTWARE\WOW6432Node\' + WebView2Key)
              or HasVersion(HKLM, 'SOFTWARE\' + WebView2Key)
              or HasVersion(HKCU, 'Software\' + WebView2Key));
end;
