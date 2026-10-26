@echo off
cd /d "%~dp0"
if not exist "publish\EverythingHttpPlugin.exe" (
    echo Building EverythingHttpPlugin.exe...
    dotnet publish EverythingHttpPlugin.csproj -c Release -o publish /p:PublishSingleFile=true /p:SelfContained=false
)
echo Starting OmniSight Native Plugin...
start http://127.0.0.1:8080/
publish\EverythingHttpPlugin.exe --port 8080
pause
