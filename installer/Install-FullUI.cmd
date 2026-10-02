@echo off
setlocal
title FullUI Installer
set "HERE=%~dp0"
if not exist "%HERE%install.ps1" goto missing
where powershell >nul 2>nul
if errorlevel 1 goto nops
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%install.ps1" -LogDir "%HERE:~0,-1%" %*
set "RC=%ERRORLEVEL%"
goto finish
:missing
echo.
echo This file needs "install.ps1" to sit next to it. Please download "FullUI-Installer.cmd"
echo from the Releases page instead - that one file contains everything.
set "RC=1"
goto finish
:nops
echo.
echo Windows PowerShell was not found on this computer, so the installer cannot run.
echo It is included with Windows 7 and newer; please ask for help (see docs\INSTALL.md).
set "RC=1"
:finish
echo.
echo %* | findstr /i "unattended" >nul
if errorlevel 1 (
  echo Press any key to close this window...
  pause >nul
)
exit /b %RC%
