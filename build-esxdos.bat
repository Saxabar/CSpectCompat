@echo off
rem Builds bin\esxDOS.dll (CSpect's esxDOS plugin with the DISK_FILEMAP fix) with the C# compiler
rem included in Windows (.NET Framework 4).
rem Usage: build-esxdos.bat "<CSpect folder>"   (the folder with CSpect.exe and Plugin.dll)
setlocal
set "CSPECT=%~1"
if "%CSPECT%"=="" (
    echo Usage: build-esxdos.bat "CSpect folder"
    exit /b 1
)
if not exist "%CSPECT%\Plugin.dll" (
    echo Plugin.dll not found in "%CSPECT%"
    exit /b 1
)
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%~dp0bin" mkdir "%~dp0bin"
set "SRC=%~dp0esxDOS"
"%CSC%" -nologo -target:library -optimize -platform:anycpu -nowarn:414 -out:"%~dp0bin\esxDOS.dll" -r:"%CSPECT%\Plugin.dll" -r:System.Core.dll "%SRC%\CMD_Packet.cs" "%SRC%\CRST8.cs" "%SRC%\FileHandle.cs" "%SRC%\HDFileSystem.cs" "%SRC%\Properties\AssemblyInfo.cs"
if errorlevel 1 exit /b 1
echo Built bin\esxDOS.dll - rename CSpect's esxDOS.dll (e.g. esxDOS.dll.orig) and copy this one next to CSpect.exe
