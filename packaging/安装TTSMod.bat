@echo off
setlocal
set "INSTALLER=%~dp0Install-TTSMod.ps1"
if not exist "%INSTALLER%" (
    echo Missing Install-TTSMod.ps1 next to this file.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%INSTALLER%" -GamePath "%~1"
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" echo Installation did not complete. Error code: %RESULT%
pause
exit /b %RESULT%
