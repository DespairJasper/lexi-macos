Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"

!ifndef VERSION
  !define VERSION "1.2.4"
!endif

!ifndef OUTDIR
  !define OUTDIR "..\..\Lexi-${VERSION}-Windows-x64"
!endif

Name "Lexi ${VERSION}"
OutFile "${OUTDIR}\Lexi-${VERSION}-x64-setup.exe"
!ifdef TEST_ROOT
  InstallDir "${TEST_ROOT}\app"
  !define LOCK_ROOT "${TEST_ROOT}\data"
!else
  InstallDir "$LOCALAPPDATA\Programs\LexiNative"
  InstallDirRegKey HKCU "Software\LexiNative" "InstallDir"
  !define LOCK_ROOT "$LOCALAPPDATA\Lexi"
!endif
RequestExecutionLevel user
; Chapter MP3s are already compressed. Per-file zlib avoids long solid archive
; rebuilds and lets the installer extract without decompressing the full corpus.
SetCompressor zlib
Icon "..\Assets\icon.ico"
UninstallIcon "..\Assets\icon.ico"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"
Var InstallLockHandle

Function AcquireInstallLock
  CreateDirectory "${LOCK_ROOT}"
  System::Call 'kernel32::CreateFileW(w "${LOCK_ROOT}\install.lock", i 0xC0000000, i 0, p 0, i 4, i 0x80, p 0) p .r0'
  StrCpy $InstallLockHandle $0
  ${If} $InstallLockHandle == -1
    MessageBox MB_OK|MB_ICONEXCLAMATION "Lexi 正在运行或另一安装正在进行。请从托盘退出软件后重试；词库不会被改动。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd

Function .onInstSuccess
  System::Call 'kernel32::CloseHandle(p $InstallLockHandle)'
  StrCpy $InstallLockHandle 0
FunctionEnd

Function .onGUIEnd
  ${If} $InstallLockHandle != 0
  ${AndIf} $InstallLockHandle != -1
    System::Call 'kernel32::CloseHandle(p $InstallLockHandle)'
  ${EndIf}
FunctionEnd

Function .onInit
  Call AcquireInstallLock
  FindWindow $0 "" "Lexi · 记住每一次遇见"
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "请先从 Lexi 新版托盘菜单退出程序，再安装更新。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd

Function un.onInit
  CreateDirectory "${LOCK_ROOT}"
  System::Call 'kernel32::CreateFileW(w "${LOCK_ROOT}\install.lock", i 0xC0000000, i 0, p 0, i 4, i 0x80, p 0) p .r0'
  StrCpy $InstallLockHandle $0
  ${If} $InstallLockHandle == -1
    MessageBox MB_OK|MB_ICONEXCLAMATION "请先退出正在运行的 Lexi 或等待安装完成，再卸载。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  FindWindow $0 "" "Lexi · 记住每一次遇见"
  ${If} $0 != 0
    MessageBox MB_OK|MB_ICONEXCLAMATION "请先从 Lexi 新版托盘菜单退出程序，再卸载。生词数据会保留。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd

Function un.onUninstSuccess
  System::Call 'kernel32::CloseHandle(p $InstallLockHandle)'
  StrCpy $InstallLockHandle 0
FunctionEnd

Function un.onGUIEnd
  ${If} $InstallLockHandle != 0
  ${AndIf} $InstallLockHandle != -1
    System::Call 'kernel32::CloseHandle(p $InstallLockHandle)'
  ${EndIf}
FunctionEnd

Section "Lexi" SEC01
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  File /r /x "*.pdb" "..\publish\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
!ifndef TEST_ROOT
  CreateShortcut "$DESKTOP\Lexi 生词本（新版）.lnk" "$INSTDIR\Lexi.exe"
  CreateDirectory "$SMPROGRAMS\Lexi"
  CreateShortcut "$SMPROGRAMS\Lexi\Lexi.lnk" "$INSTDIR\Lexi.exe"
  CreateShortcut "$SMPROGRAMS\Lexi\卸载.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\LexiNative" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LexiNative" "DisplayName" "Lexi ${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LexiNative" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LexiNative" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LexiNative" "DisplayIcon" "$INSTDIR\Lexi.exe"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LexiNative" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LexiNative" "NoRepair" 1
!endif
SectionEnd

Section "Uninstall"
  SetShellVarContext current
!ifndef TEST_ROOT
  Delete "$DESKTOP\Lexi 生词本（新版）.lnk"
  Delete "$SMPROGRAMS\Lexi\Lexi.lnk"
  Delete "$SMPROGRAMS\Lexi\卸载.lnk"
  RMDir "$SMPROGRAMS\Lexi"
!endif
  ; Remove only packaged files; never recursively delete a user-selected directory.
  !include "lexi-uninstall-files.nsh"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
!ifndef TEST_ROOT
  DeleteRegKey HKCU "Software\LexiNative"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\LexiNative"
!endif
SectionEnd

