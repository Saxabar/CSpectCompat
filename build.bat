@echo off
rem Builds bin\Compat.dll with the C# compiler included in Windows (.NET Framework 4).
rem Usage: build.bat "<CSpect folder>"   (the folder with CSpect.exe and Plugin.dll)
setlocal
set "CSPECT=%~1"
if "%CSPECT%"=="" (
    echo Usage: build.bat "CSpect folder"
    exit /b 1
)
if not exist "%CSPECT%\Plugin.dll" (
    echo Plugin.dll not found in "%CSPECT%"
    exit /b 1
)
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" -nologo -target:library -optimize -platform:anycpu -out:"%~dp0bin\Compat.dll" -r:"%CSPECT%\Plugin.dll" -r:System.Windows.Forms.dll -r:System.Drawing.dll "%~dp0CompatPlugin.cs"
if errorlevel 1 exit /b 1
echo Built bin\Compat.dll - copy it next to CSpect.exe
