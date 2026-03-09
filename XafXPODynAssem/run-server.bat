@echo off
:loop
echo [run-server] Starting XafXPODynAssem.Blazor.Server...
dotnet run --project XafXPODynAssem\XafXPODynAssem.Blazor.Server
if %ERRORLEVEL% == 42 (
    echo [run-server] Exit code 42 detected. Restarting...
    timeout /t 2 /nobreak >nul
    goto loop
)
echo [run-server] Server exited with code %ERRORLEVEL%.
