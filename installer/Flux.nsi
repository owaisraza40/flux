Unicode true
!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"
; MakeNSIS changes its working directory to the script directory by default.
!define ROOT ".."
!define PRODUCT "Flux"
!define VERSION "1.0.0"
Name "Flux ${VERSION}"
OutFile "${ROOT}\artifacts\release\Flux-Setup-${VERSION}-win-x64.exe"
InstallDir "$LOCALAPPDATA\Programs\Flux"
InstallDirRegKey HKCU "Software\Flux" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "Flux — open-source download manager"
VIProductVersion "1.0.0.0"
VIAddVersionKey /LANG=1033 "ProductName" "Flux"
VIAddVersionKey /LANG=1033 "FileDescription" "Flux per-user Windows installer"
VIAddVersionKey /LANG=1033 "FileVersion" "1.0.0"
VIAddVersionKey /LANG=1033 "ProductVersion" "1.0.0"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright 2026 Flux contributors"
!define MUI_ABORTWARNING
!define MUI_ICON "${ROOT}\assets\flux.ico"
!define MUI_UNICON "${ROOT}\assets\flux.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\Flux.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Open Flux"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${ROOT}\LICENSE"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Flux requires 64-bit Windows 10 or Windows 11."
    Abort
  ${EndIf}
  SetShellVarContext current
  SetRegView 64
FunctionEnd

Section "Flux application (required)" MainSection
  SectionIn RO
  ; Never partially overwrite a running installation. Silent upgrades fail safely.
  IfFileExists "$INSTDIR\Flux.exe" 0 flux_files_ready
  flux_retry_lock:
  System::Call 'kernel32::CreateFileW(w "$INSTDIR\Flux.exe", i 0x40000000, i 0, p 0, i 3, i 0, p 0) p.r0'
  StrCmp $0 -1 flux_file_locked
  System::Call 'kernel32::CloseHandle(p r0)'
  Goto flux_files_ready
  flux_file_locked:
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "Close Flux using Exit Flux in its tray menu, then retry. Your download checkpoints are retained." /SD IDCANCEL IDRETRY flux_retry_lock
  SetErrorLevel 2
  Quit
  flux_files_ready:
  SetOutPath "$INSTDIR"
  File /r "${ROOT}\artifacts\publish\*"
  File "${ROOT}\LICENSE"
  File "${ROOT}\README.md"
  File "${ROOT}\THIRD-PARTY-NOTICES.md"
  SetOutPath "$INSTDIR\docs"
  File "${ROOT}\docs\USER-GUIDE.md"
  File "${ROOT}\docs\BROWSER-INTEGRATION.md"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Flux"
  CreateShortcut "$SMPROGRAMS\Flux\Flux.lnk" "$INSTDIR\Flux.exe"
  CreateShortcut "$SMPROGRAMS\Flux\Uninstall Flux.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Flux" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Flux" "DisplayName" "Flux"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Flux" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Flux" "Publisher" "Flux contributors"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Flux" "URLInfoAbout" "https://github.com/owaisraza40/flux"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Flux" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Flux" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Flux" "DisplayIcon" "$INSTDIR\Flux.exe"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Flux" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Flux" "NoRepair" 1
SectionEnd

Section /o "Desktop shortcut"
  CreateShortcut "$DESKTOP\Flux.lnk" "$INSTDIR\Flux.exe"
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  Delete "$DESKTOP\Flux.lnk"
  Delete "$SMPROGRAMS\Flux\Flux.lnk"
  Delete "$SMPROGRAMS\Flux\Uninstall Flux.lnk"
  RMDir "$SMPROGRAMS\Flux"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Flux"
  DeleteRegKey HKCU "Software\Google\Chrome\NativeMessagingHosts\com.flux.downloads"
  DeleteRegKey HKCU "Software\Microsoft\Edge\NativeMessagingHosts\com.flux.downloads"
  DeleteRegKey HKCU "Software\Mozilla\NativeMessagingHosts\com.flux.downloads"
  DeleteRegKey HKCU "Software\Google\Chrome\NativeMessagingHosts\com.flux.bridge"
  DeleteRegKey HKCU "Software\Microsoft\Edge\NativeMessagingHosts\com.flux.bridge"
  DeleteRegKey HKCU "Software\Mozilla\NativeMessagingHosts\com.flux.bridge"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Flux"
  DeleteRegValue HKCU "Software\Flux" "InstallDir"
  Delete "$INSTDIR\Flux.exe"
  Delete "$INSTDIR\Flux.NativeHost.exe"
  Delete "$INSTDIR\Flux.Core.dll"
  Delete "$INSTDIR\Flux.Core.pdb"
  Delete "$INSTDIR\licenses\dotnet-LICENSE.txt"
  Delete "$INSTDIR\licenses\dotnet-THIRD-PARTY-NOTICES.txt"
  Delete "$INSTDIR\licenses\Inter-OFL.txt"
  Delete "$INSTDIR\licenses\JetBrainsMono-OFL.txt"
  RMDir "$INSTDIR\licenses"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\README.md"
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.md"
  Delete "$INSTDIR\docs\USER-GUIDE.md"
  Delete "$INSTDIR\docs\BROWSER-INTEGRATION.md"
  Delete "$INSTDIR\docs\ARCHITECTURE.md"
  Delete "$INSTDIR\docs\CORE-TESTING.md"
  Delete "$INSTDIR\docs\IDM-REFERENCE.md"
  Delete "$INSTDIR\docs\RELEASE-NOTES.md"
  Delete "$INSTDIR\docs\RELEASING.md"
  Delete "$INSTDIR\docs\VALIDATION.md"
  Delete "$INSTDIR\docs\images\flux.png"
  Delete "$INSTDIR\docs\images\flux-add-popup.png"
  RMDir "$INSTDIR\docs\images"
  RMDir "$INSTDIR\docs"
  Delete "$INSTDIR\browser-extension\chromium\*.mjs"
  Delete "$INSTDIR\browser-extension\chromium\*.js"
  Delete "$INSTDIR\browser-extension\chromium\*.html"
  Delete "$INSTDIR\browser-extension\chromium\*.css"
  Delete "$INSTDIR\browser-extension\chromium\manifest.json"
  Delete "$INSTDIR\browser-extension\chromium\icons\*.png"
  RMDir "$INSTDIR\browser-extension\chromium\icons"
  RMDir "$INSTDIR\browser-extension\chromium"
  Delete "$INSTDIR\browser-extension\firefox\*.mjs"
  Delete "$INSTDIR\browser-extension\firefox\*.js"
  Delete "$INSTDIR\browser-extension\firefox\*.html"
  Delete "$INSTDIR\browser-extension\firefox\*.css"
  Delete "$INSTDIR\browser-extension\firefox\manifest.json"
  Delete "$INSTDIR\browser-extension\firefox\icons\*.png"
  RMDir "$INSTDIR\browser-extension\firefox\icons"
  RMDir "$INSTDIR\browser-extension\firefox"
  RMDir "$INSTDIR\browser-extension"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  ; User downloads, checkpoints, history, and locally installed media tools are retained.
SectionEnd
