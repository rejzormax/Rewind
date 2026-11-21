@echo off
cd /d "%~dp0"
if exist "dist\Rewind\auto-updater.exe" (
  start "" "%~dp0dist\Rewind\Rewind.exe"
) else if exist "dist\Rewind-0.3.1\Rewind.exe" (
  start "" "%~dp0dist\Rewind-0.3.1\Rewind.exe"
) else if exist "dist\Rewind-0.3\Rewind.exe" (
  start "" "%~dp0dist\Rewind-0.3\Rewind.exe"
) else if exist "dist\Rewind-0.2\Rewind.exe" (
  start "" "%~dp0dist\Rewind-0.2\Rewind.exe"
) else if exist "dist\Rewind\Rewind.exe" (
  start "" "%~dp0dist\Rewind\Rewind.exe"
) else (
  echo Rewind has not been built. Run scripts\setup.ps1 then scripts\build.ps1.
  pause
)
