@echo off
rem Builds bin\WindowsShow.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find csc.exe from .NET Framework 4.x.
  exit /b 1
)

cd /d "%~dp0"
if not exist bin mkdir bin

"%CSC%" /nologo /target:winexe /optimize+ /out:bin\WindowsShow.exe ^
  /win32manifest:src\app.manifest ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xml.dll ^
  src\*.cs
if errorlevel 1 exit /b 1
echo Built bin\WindowsShow.exe
