@echo off
rem Builds the window version (steam-audio-relay.exe) and the console version (steam-audio-relay-cli.exe)
rem with the C# compiler that ships with Windows (.NET Framework 4). No SDK needed.
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Couldn't find csc.exe at %CSC%
  exit /b 1
)
set REFS=/r:System.Windows.Forms.dll /r:System.Drawing.dll
rem icon.ico: file icon of the exe (/win32icon) and the window/tray icon at runtime (/resource)
set ICON=/win32icon:icon.ico /resource:icon.ico,icon.ico
"%CSC%" /nologo /target:winexe %REFS% %ICON% /out:steam-audio-relay.exe relay.cs || exit /b 1
"%CSC%" /nologo /target:exe %REFS% %ICON% /out:steam-audio-relay-cli.exe relay.cs || exit /b 1
