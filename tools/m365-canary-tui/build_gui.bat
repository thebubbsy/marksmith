@echo off
setlocal
cd /d "%~dp0"

set "PYINSTALLER=pyinstaller"
if exist "%~dp0.venv\Scripts\pyinstaller.exe" (
    set "PYINSTALLER=%~dp0.venv\Scripts\pyinstaller.exe"
)

echo [*] Building standalone Windows Desktop GUI executable (M365CanaryGUI.exe)...
"%PYINSTALLER%" ^
  --noconfirm ^
  --onefile ^
  --windowed ^
  --name "M365CanaryGUI" ^
  --add-data "m365_canary_tui.py;." ^
  --hidden-import=webview ^
  --hidden-import=webview.platforms.edgechromium ^
  --hidden-import=webview.platforms.winforms ^
  --collect-all=rich ^
  m365_canary_tui.py

echo.
if exist "%~dp0dist\M365CanaryGUI.exe" (
    echo [+] Build complete: "%~dp0dist\M365CanaryGUI.exe"
) else (
    echo [-] Build failed. Check output above.
)
endlocal
