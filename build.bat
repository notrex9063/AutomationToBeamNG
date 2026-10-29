@echo off
rem Builds AutomationToBeamNG.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem The first build has no file icon yet; it then draws src\app.ico and builds again with it.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0"
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll /r:System.Xml.dll
set ICON=
if exist src\app.ico set ICON=/win32icon:src\app.ico
"%CSC%" /nologo /target:winexe /optimize+ /utf8output /codepage:65001 /out:AutomationToBeamNG.exe %REFS% %ICON% src\*.cs
if errorlevel 1 (echo. & echo Build failed. & exit /b 1)
if not exist src\app.ico "%~dp0AutomationToBeamNG.exe" --make-icon "%~dp0src\app.ico"
if "%ICON%"=="" if exist src\app.ico (
  "%CSC%" /nologo /target:winexe /optimize+ /utf8output /codepage:65001 /out:AutomationToBeamNG.exe %REFS% /win32icon:src\app.ico src\*.cs
  if errorlevel 1 (echo. & echo Build failed. & exit /b 1)
)
echo Built %~dp0AutomationToBeamNG.exe
