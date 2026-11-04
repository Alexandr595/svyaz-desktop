Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"
!include "nsDialogs.nsh"
!include "TextFunc.nsh"

!ifndef APP_VERSION
  !define APP_VERSION "0.4.3"
!endif
!ifndef PAYLOAD
  !error "PAYLOAD must point to a self-contained publish directory."
!endif
!ifndef DELETE_MANIFEST
  !error "DELETE_MANIFEST must name the generated uninstall manifest."
!endif
!ifndef OUTPUT_FILE
  !define OUTPUT_FILE "..\artifacts\Svyaz-Setup-${APP_VERSION}-x64.exe"
!endif
!define PRODUCT_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\SvyazDesktop"
!define INSTALL_ID "9e2555bf-39c2-4d99-b42a-e14c9039b0d5"

Name "Связь Desktop"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\Svyaz"
InstallDirRegKey HKCU "${PRODUCT_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
BrandingText "Связь Desktop · ${APP_VERSION}"
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=1049 "ProductName" "Связь Desktop"
VIAddVersionKey /LANG=1049 "FileDescription" "Установка Связь Desktop"
VIAddVersionKey /LANG=1049 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1049 "LegalCopyright" "Svyaz contributors · MIT"

!define MUI_ICON "..\src\Svyaz.App\Assets\svyaz.ico"
!define MUI_UNICON "..\src\Svyaz.App\Assets\svyaz.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP "assets\welcome.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "assets\header.bmp"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Связь Desktop"
!define MUI_WELCOMEPAGE_TEXT "Приложения и ваш браузер — в одном центре подключений.$\r$\n$\r$\nУстановка выполняется для текущего пользователя. .NET уже включён в комплект.$\r$\n$\r$\nДля запуска сетевого движка приложение отдельно запросит права администратора."
!define MUI_FINISHPAGE_RUN "$INSTDIR\Svyaz.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Открыть Связь Desktop"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
Page custom ImportPage ImportPageLeave
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Russian"

Var TestMode
Var ImportData
Var ImportField
Var ImportDialog
Var UpdateMode

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "Нужна 64-разрядная Windows 10 или Windows 11."
    Abort
  ${EndIf}
  SetShellVarContext current
  SetRegView 64
  StrCpy $TestMode "0"
  StrCpy $UpdateMode "0"
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/TESTMODE" $1
  ${IfNot} ${Errors}
    StrCpy $TestMode "1"
  ${EndIf}
  ClearErrors
  ${GetOptions} $0 "/UPDATE" $1
  ${IfNot} ${Errors}
    StrCpy $UpdateMode "1"
    SetSilent silent
  ${EndIf}
  ClearErrors
  ${GetOptions} $0 "/IMPORTDATA=" $ImportData
FunctionEnd

Function .onInstSuccess
  ${If} $UpdateMode == "1"
  ${AndIf} $TestMode != "1"
    Exec '"$INSTDIR\Svyaz.exe" --handoff'
    ${If} ${Errors}
      MessageBox MB_ICONEXCLAMATION "Обновление установлено. Откройте «Связь» с ярлыка."
    ${EndIf}
  ${EndIf}
FunctionEnd

Function ImportPage
  nsDialogs::Create 1018
  Pop $ImportDialog
  ${If} $ImportDialog == error
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "Перенос настроек" "Необязательный шаг для переносимой версии"
  ${NSD_CreateLabel} 0 5u 100% 50u "Если вы уже пользовались переносимой «Связью», укажите её папку data.$\r$\nБудут перенесены настройки и локальный секрет Telegram. Журналы и резервная копия системного прокси не переносятся.$\r$\n$\r$\nМожно оставить поле пустым."
  Pop $0
  ${NSD_CreateDirRequest} 0 67u 82% 14u "$ImportData"
  Pop $ImportField
  ${NSD_CreateBrowseButton} 84% 66u 16% 16u "Обзор…"
  Pop $0
  ${NSD_OnClick} $0 BrowseImport
  ${NSD_CreateLabel} 0 96u 100% 32u "При обновлении установленной версии существующие настройки сохраняются автоматически.$\r$\nДля удаления программы используйте раздел «Установленные приложения» Windows."
  Pop $0
  nsDialogs::Show
FunctionEnd

Function BrowseImport
  nsDialogs::SelectFolderDialog "Выберите папку data прежней версии" "$ImportData"
  Pop $0
  ${If} $0 != error
    StrCpy $ImportData $0
    ${NSD_SetText} $ImportField $ImportData
  ${EndIf}
FunctionEnd

Function ImportPageLeave
  ${NSD_GetText} $ImportField $ImportData
  ${If} $ImportData != ""
    IfFileExists "$ImportData\settings.json" valid_import
    MessageBox MB_ICONEXCLAMATION "В этой папке не найден settings.json. Выберите папку data или очистите поле."
    Abort
    valid_import:
  ${EndIf}
FunctionEnd

Function StopInstalledApp
  IfFileExists "$INSTDIR\Svyaz.exe" 0 app_stopped
  ExecWait '"$INSTDIR\Svyaz.exe" --stop' $0
  StrCpy $1 0
  check_app_closed:
    ClearErrors
    FileOpen $0 "$INSTDIR\Svyaz.exe" a
    ${IfNot} ${Errors}
      FileClose $0
      Goto app_stopped
    ${EndIf}
    Sleep 100
    IntOp $1 $1 + 1
    ${If} $1 < 80
      Goto check_app_closed
    ${EndIf}
    MessageBox MB_ICONSTOP "Закройте установленную «Связь» и повторите установку."
    Abort
  app_stopped:
FunctionEnd

Section "Связь Desktop" CoreSection
  Call StopInstalledApp
  SetOutPath "$INSTDIR"
  File /r "${PAYLOAD}\*.*"
  ${If} $ImportData != ""
    IfFileExists "$INSTDIR\data\settings.json" skip_import
    CreateDirectory "$INSTDIR\data"
    CopyFiles /SILENT "$ImportData\settings.json" "$INSTDIR\data\settings.json"
    IfFileExists "$ImportData\telegram-local-secret.txt" 0 skip_import
    CopyFiles /SILENT "$ImportData\telegram-local-secret.txt" "$INSTDIR\data\telegram-local-secret.txt"
    skip_import:
  ${EndIf}
  FileOpen $0 "$INSTDIR\installation.id" w
  FileWrite $0 "${INSTALL_ID}$\r$\n$TestMode$\r$\n"
  FileClose $0
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  ${If} $TestMode != "1"
    ; Remove only the exact names emitted by the UTF-8/CP1251 bug in 0.4.0.
    Delete "$DESKTOP\РЎРІСЏР·СЊ Desktop.lnk"
    Delete "$SMPROGRAMS\РЎРІСЏР·СЊ\РЎРІСЏР·СЊ Desktop.lnk"
    Delete "$SMPROGRAMS\РЎРІСЏР·СЊ\РЈРґР°Р»РёС‚СЊ.lnk"
    RMDir "$SMPROGRAMS\РЎРІСЏР·СЊ"
    CreateDirectory "$SMPROGRAMS\Связь"
    CreateShortcut "$SMPROGRAMS\Связь\Связь Desktop.lnk" "$INSTDIR\Svyaz.exe" "" "$INSTDIR\Svyaz.exe" 0
    CreateShortcut "$SMPROGRAMS\Связь\Удалить.lnk" "$INSTDIR\Uninstall.exe"
    CreateShortcut "$DESKTOP\Связь Desktop.lnk" "$INSTDIR\Svyaz.exe" "" "$INSTDIR\Svyaz.exe" 0
    WriteRegStr HKCU "${PRODUCT_KEY}" "DisplayName" "Связь Desktop"
    WriteRegStr HKCU "${PRODUCT_KEY}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKCU "${PRODUCT_KEY}" "Publisher" "Svyaz contributors"
    WriteRegStr HKCU "${PRODUCT_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${PRODUCT_KEY}" "DisplayIcon" "$INSTDIR\Svyaz.exe"
    WriteRegStr HKCU "${PRODUCT_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegStr HKCU "${PRODUCT_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
    WriteRegDWORD HKCU "${PRODUCT_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${PRODUCT_KEY}" "NoRepair" 1
  ${EndIf}
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  ClearErrors
  FileOpen $0 "$INSTDIR\installation.id" r
  ${If} ${Errors}
    Abort
  ${EndIf}
  FileRead $0 $1
  ${TrimNewLines} $1 $1
  ${If} $1 != "${INSTALL_ID}"
    FileClose $0
    Abort
  ${EndIf}
  FileRead $0 $TestMode
  ${TrimNewLines} $TestMode $TestMode
  FileClose $0
FunctionEnd

Section "Uninstall"
  ExecWait '"$INSTDIR\Svyaz.exe" --stop' $0
  Sleep 1500
  !include "${DELETE_MANIFEST}"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$INSTDIR\installation.id"
  ${If} $TestMode != "1"
    Delete "$DESKTOP\Связь Desktop.lnk"
    Delete "$SMPROGRAMS\Связь\Связь Desktop.lnk"
    Delete "$SMPROGRAMS\Связь\Удалить.lnk"
    RMDir "$SMPROGRAMS\Связь"
    DeleteRegKey HKCU "${PRODUCT_KEY}"
  ${EndIf}
  ; data is deliberately retained; never recursively delete the install root.
  RMDir "$INSTDIR"
SectionEnd
