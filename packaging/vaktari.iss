; Inno Setup script for the Windows build.
;
; The Linux side ships a tarball plus install.sh, which suits a system where
; unpacking to /opt and dropping a .desktop file is a normal way to install
; something. Windows has no such convention: a bare zip leaves the user to pick
; a directory, make their own Start menu entry, and find out for themselves how
; to remove it. So this produces a single signed-shaped .exe that does the usual
; things and, more importantly, can be uninstalled from Settings like anything
; else.
;
; Compiled by ISCC, which is preinstalled on GitHub's windows runners:
;
;   iscc /DAppVersion=0.4.0 /DPayload=..\path\to\publish packaging\vaktari.iss
;
; AppVersion and Payload are required; the compile fails with a readable message
; below rather than producing an installer stamped 0.0.0 or holding nothing.

; Both a definedness AND a value test. `#ifndef` alone is not enough: ISCC
; accepts /DAppVersion= with nothing after the `=` and defines the symbol with a
; null value, so #ifndef is false, the #error below never fires, and the compile
; SUCCEEDS -- emitting vaktari--win-x64-setup.exe stamped 0.0.0.0, which is the
; exact outcome this guard exists to prevent.
#ifndef AppVersion
  #error AppVersion is required: pass /DAppVersion=x.y.z
#endif
#if AppVersion == ""
  #error AppVersion is empty: pass /DAppVersion=x.y.z
#endif

#ifndef Payload
  #error Payload is required: pass /DPayload=<the publish directory>
#endif
#if Payload == ""
  #error Payload is empty: pass /DPayload=<the publish directory>
#endif

; VersionInfoVersion is written into the PE VERSIONINFO resource and accepts
; only dot-separated numbers. AppVersion, AppVerName and OutputBaseFilename all
; tolerate a semver prerelease suffix, so a v0.5.0-rc1 tag would sail through
; every other directive and abort the compile on that one line -- after the full
; NativeAOT link, and only ever on a tag push. Strip from the first dash so the
; resource gets 0.5.0 while everything the user sees keeps the full string.
#if Pos("-", AppVersion) > 0
  #define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion) - 1)
#else
  #define NumericVersion AppVersion
#endif

#define AppName "Vaktari"
#define AppPublisher "dkflint723"
#define AppUrl "https://github.com/dkflint723/vaktari"
#define AppExe "Vaktari.Ui.exe"

; Kept as its own symbol so the test that compares it with the C# constant has
; one unambiguous thing to read, rather than parsing it out of a [Setup] line.
#define AppMutex "Vaktari.Ui.Running"

[Setup]
AppId={{8F3C1E42-6B7A-4D59-9E2C-A1F4B8D70C36}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#NumericVersion}

; Per-user by default, so installing needs no administrator and no UAC prompt.
; A file manager is a personal tool and there is nothing here that belongs to
; the machine -- no service, no driver, no shared component. `lowest` keeps the
; installer itself unelevated; a user who wants it for everyone can still choose
; that on the first page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; **Stop before replacing a running Vaktari.** Without this the installer began
; overwriting a 28 MB executable that might be open, and relied on Restart
; Manager noticing -- which is luck rather than design, and when it does not
; work the result is a half-written binary and a "close the application" dialog
; arriving after the damage.
;
; The name must match Program.InstanceMutexName; a test asserts they are equal,
; because the failure mode is silent. A renamed mutex on either side does not
; break anything visibly -- the installer just quietly stops noticing, which is
; indistinguishable from working until somebody upgrades with the app open.
AppMutex={#AppMutex}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; **A renamed application must not keep installing into its old folder.**
;
; AppId is deliberately unchanged across the rename, so setup recognises an
; existing Heimdall and replaces its entry in Installed apps rather than leaving
; two copies of the same program under two names. The cost of that is this
; setting: Inno defaults UsePreviousAppDir to yes, so it would reuse the
; directory recorded by that older install -- and Vaktari would install itself
; into a folder named Heimdall, indefinitely, on every machine that ever had the
; old build.
;
; Off, so DefaultDirName above decides and the folder matches the name on the
; window. Someone who chose a custom directory the first time will be offered
; the default again rather than their own choice; that is the smaller surprise
; of the two, and it only happens once.
UsePreviousAppDir=no

; The mark on the executable, the installer and the entry in Installed apps.
SetupIconFile=..\brand\icons\vaktari.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=vaktari-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; NativeAOT publishes x64 here, so refuse the architectures it will not run on
; rather than installing something that fails when double-clicked. ARM64 is
; included because Windows emulates x64 on it.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; **Clear out the folder the old name left behind.**
;
; With UsePreviousAppDir off, an upgrade from Heimdall installs into a Vaktari
; folder and simply abandons the old one -- a full copy of the previous version
; sitting on disk, with a Start menu shortcut that still works and launches it.
; Two working copies of the same program is worse than one.
;
; Only the DEFAULT old location. Somebody who installed Heimdall somewhere of
; their own keeps a stale copy, which is untidy; guessing at directories and
; deleting them is not untidy, it is dangerous. Leaving files behind is the
; recoverable mistake.
;
; Safe to run when it is absent -- Inno skips a Type that matches nothing -- and
; safe to run while the old copy exists, because AppMutex above refuses to start
; the install at all while any copy is running.
[InstallDelete]
Type: filesandordirs; Name: "{autopf}\Heimdall"
Type: filesandordirs; Name: "{group}\..\Heimdall"

[Files]
; The publish DIRECTORY is the deliverable, not the executable: libSkiaSharp.dll
; and libHarfBuzzSharp.dll are loaded from beside the binary, and shipping the
; .exe alone produces something that aborts before it draws anything. The Linux
; job checks for the .so for the same reason.
;
; PDBs are excluded deliberately -- they are five times the size of everything
; else here and are of no use on a user's machine.
Source: "{#Payload}\*"; DestDir: "{app}"; \
    Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

; MIT requires the notice to travel with "all copies or substantial portions",
; so it ships inside the installer rather than only in the repository.
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

; **Undo the folder-handler registration before the executable goes.**
;
; Becoming the default writes a shell verb pointing at {app}\{#AppExe}.
; Uninstalling removed the file and left the verb, so afterwards every
; double-clicked folder tried to launch a path that no longer existed — and the
; error Windows shows names a missing file, not the program that registered it.
; There is no way out of that except the registry, which is not somewhere a user
; should have to go because a program was removed.
;
; [UninstallRun] entries execute BEFORE files are deleted, which is the only
; window in which the application can still undo its own work.
;
; runascurrentuser because the registration is per-user under HKCU: an elevated
; uninstaller would otherwise look at the administrator's hive and find nothing.
; skipifdoesntexist so a partially removed install does not fail the uninstall,
; and RunOnceId so repeating it is harmless.
[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--restore-file-manager";     RunOnceId: "RestoreFileManager"; Flags: runhidden runascurrentuser skipifdoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent

; No [UninstallDelete]. Tabs, pinned places, window position, folder views,
; recents, settings, the tag index and the user's own scripts\ folder all live
; under %LOCALAPPDATA%\vaktari, not beside the binary, and an uninstall
; deliberately leaves them there -- reinstalling or upgrading should find your
; tabs where you left them, and silently destroying the tag index or scripts
; somebody wrote themselves is not something an uninstaller should decide on
; its own.
