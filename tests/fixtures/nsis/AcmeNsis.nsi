Unicode True
Name "Acme NSIS"
OutFile "output\AcmeNsisSetup.exe"
InstallDir "$PROGRAMFILES64\AcmeNsis"
RequestExecutionLevel admin
ShowInstDetails show

Page directory
Page instfiles

Section "MainSection" SEC01
  SetOutPath "$INSTDIR"
  File "payload\AcmeNsis.exe"
  WriteUninstaller "$INSTDIR\uninstall.exe"
  CreateShortcut "$SMPROGRAMS\Acme NSIS.lnk" "$INSTDIR\AcmeNsis.exe"
SectionEnd

Section "Uninstall"
  Delete "$INSTDIR\AcmeNsis.exe"
  Delete "$INSTDIR\uninstall.exe"
  Delete "$SMPROGRAMS\Acme NSIS.lnk"
  RMDir "$INSTDIR"
SectionEnd
