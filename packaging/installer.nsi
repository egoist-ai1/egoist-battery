# -*- coding: utf-8 -*-
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "WinVer.nsh"
!include "x64.nsh"
!ifndef PRODUCT_VERSION
!error "Передайте PRODUCT_VERSION, SOURCE_DIR и OUTPUT_FILE из build.ps1"
!endif
!define PRODUCT_NAME "Egoist Battery"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\EgoistBattery"
Name "${PRODUCT_NAME}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\EgoistBattery"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
ManifestDPIAware true
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=1049 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1049 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1049 "FileVersion" "${PRODUCT_VERSION}.0"
VIAddVersionKey /LANG=1049 "FileDescription" "Установка Egoist Battery"
VIAddVersionKey /LANG=1049 "LegalCopyright" "Copyright © 2026 Egoist AI"
!define MUI_ICON "..\src\EgoistBattery\Assets\app.ico"
!define MUI_UNICON "..\src\EgoistBattery\Assets\app.ico"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "header.bmp"
!define MUI_WELCOMEFINISHPAGE_BITMAP "sidebar.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "sidebar.bmp"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Egoist Battery"
!define MUI_WELCOMEPAGE_TEXT "Заряд подключённых устройств — прямо в трее.$\r$\n$\r$\nПрограмма устанавливается для текущего пользователя. Отдельная установка .NET не требуется."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION "LaunchApplication"
!define MUI_FINISHPAGE_RUN_TEXT "Запустить Egoist Battery в трее"
!insertmacro MUI_PAGE_FINISH
!define MUI_UNCONFIRMPAGE_TEXT_TOP "Программа и её ярлыки будут удалены. Пользовательские настройки и данные сохранятся."
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "Russian"
Var TestMode

Function .onInit
  SetShellVarContext current
  SetRegView 64
  StrCpy $TestMode "0"
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/TESTMODE" $1
  ${IfNot} ${Errors}
    StrCpy $TestMode "1"
  ${EndIf}
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "Для этой версии нужна 64-разрядная Windows." /SD IDOK
    SetErrorLevel 1
    Quit
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Нужна Windows 10 или Windows 11." /SD IDOK
    SetErrorLevel 1
    Quit
  ${EndIf}
  ${WinVerGetBuild} $0
  ${If} $0 < 19041
    MessageBox MB_OK|MB_ICONSTOP "Нужна Windows 10 build 19041 или новее." /SD IDOK
    SetErrorLevel 1
    Quit
  ${EndIf}
FunctionEnd

Section "Egoist Battery" SecMain
  SectionIn RO
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=InstallerLifecycle.ps1 "${SOURCE_DIR}\InstallerLifecycle.ps1"
  StrCpy $2 ""
  ${If} $TestMode == "1"
    StrCpy $2 " -TestMode"
  ${EndIf}
  nsExec::ExecToStack /TIMEOUT=35000 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\InstallerLifecycle.ps1" -InstallDirectory "$INSTDIR"$2'
  Pop $0
  Pop $1
  ${If} $0 != "0"
    DetailPrint "$1"
    MessageBox MB_OK|MB_ICONSTOP "Не удалось завершить программу. Установка остановлена." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetOutPath "$INSTDIR"
  ClearErrors
  SetOverwrite on
  File "${SOURCE_DIR}\EgoistBattery.exe"
  File "${SOURCE_DIR}\LICENSE"
  File "${SOURCE_DIR}\README-install.md"
  File "${SOURCE_DIR}\NOTICE.txt"
  File "${SOURCE_DIR}\COMMUNITYTOOLKIT-LICENSE.txt"
  File "${SOURCE_DIR}\NSIS-LICENSE.txt"
  File "${SOURCE_DIR}\DOTNET-LICENSE.txt"
  File "${SOURCE_DIR}\DOTNET-THIRD-PARTY-NOTICES.txt"
  File "${SOURCE_DIR}\WINDOWS-DESKTOP-LICENSE.txt"
  File "${SOURCE_DIR}\InstallerLifecycle.ps1"
  ${If} ${Errors}
    SetErrorLevel 1
    Abort
  ${EndIf}
  WriteINIStr "$INSTDIR\installation.ini" "Install" "TestMode" "$TestMode"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  ${If} $TestMode == "0"
    CreateShortcut "$SMPROGRAMS\Egoist Battery.lnk" "$INSTDIR\EgoistBattery.exe"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Egoist AI"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\EgoistBattery.exe,0"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
    WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/egoist-ai1/egoist-battery"
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${EndIf}
SectionEnd

Section "Автозапуск при входе в Windows" SecStartup
  ${If} $TestMode == "0"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "EgoistBattery" '"$INSTDIR\EgoistBattery.exe" --tray'
  ${EndIf}
SectionEnd

Section /o "Ярлык на рабочем столе" SecDesktop
  ${If} $TestMode == "0"
    CreateShortcut "$DESKTOP\Egoist Battery.lnk" "$INSTDIR\EgoistBattery.exe"
  ${EndIf}
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} "Программа, встроенный runtime и удаление через настройки Windows."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecStartup} "Запуск фонового монитора в трее при входе в учётную запись."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "Ярлык для открытия окна программы."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function LaunchApplication
  Exec '"$INSTDIR\EgoistBattery.exe" --tray'
FunctionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ReadINIStr $TestMode "$INSTDIR\installation.ini" "Install" "TestMode"
FunctionEnd

Section "Uninstall"
  StrCpy $2 ""
  ${If} $TestMode == "1"
    StrCpy $2 " -TestMode"
  ${EndIf}
  nsExec::ExecToStack /TIMEOUT=35000 '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$INSTDIR\InstallerLifecycle.ps1" -InstallDirectory "$INSTDIR"$2'
  Pop $0
  Pop $1
  ${If} $0 != "0"
    DetailPrint "$1"
    MessageBox MB_OK|MB_ICONSTOP "Не удалось завершить программу. Удаление остановлено." /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  ${If} $TestMode != "1"
    ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "EgoistBattery"
    ${If} $0 == '"$INSTDIR\EgoistBattery.exe" --tray'
      DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "EgoistBattery"
    ${EndIf}
    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
    ${If} $0 == "$INSTDIR"
      DeleteRegKey HKCU "${UNINSTALL_KEY}"
      Delete "$SMPROGRAMS\Egoist Battery.lnk"
      Delete "$DESKTOP\Egoist Battery.lnk"
    ${EndIf}
  ${EndIf}
  ; Только известные файлы приложения. Данные и посторонние файлы не удаляем.
  Delete "$INSTDIR\EgoistBattery.exe"
  Delete "$INSTDIR\LICENSE"
  Delete "$INSTDIR\README-install.md"
  Delete "$INSTDIR\NOTICE.txt"
  Delete "$INSTDIR\COMMUNITYTOOLKIT-LICENSE.txt"
  Delete "$INSTDIR\NSIS-LICENSE.txt"
  Delete "$INSTDIR\DOTNET-LICENSE.txt"
  Delete "$INSTDIR\DOTNET-THIRD-PARTY-NOTICES.txt"
  Delete "$INSTDIR\WINDOWS-DESKTOP-LICENSE.txt"
  Delete "$INSTDIR\InstallerLifecycle.ps1"
  Delete "$INSTDIR\installation.ini"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
