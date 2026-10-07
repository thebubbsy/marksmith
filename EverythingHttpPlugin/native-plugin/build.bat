@echo off
setlocal
echo ===================================================
echo Building OmniSight Native Everything 1.5 Plugin DLL
echo ===================================================

where cmake >nul 2>&1
if %ERRORLEVEL% equ 0 (
    if not exist build mkdir build
    cd build
    cmake .. -G "Visual Studio 17 2022" -A x64
    cmake --build . --config Release
    cd ..
    goto done
)

where cl >nul 2>&1
if %ERRORLEVEL% equ 0 (
    cl.exe /O2 /LD /Fe:omnisight_http64.dll omnisight_plugin.c /link /DEF:omnisight_plugin.def user32.lib kernel32.lib ws2_32.lib
    goto done
)

where gcc >nul 2>&1
if %ERRORLEVEL% equ 0 (
    gcc -shared -O3 -o omnisight_http64.dll omnisight_plugin.c omnisight_plugin.def -lws2_32 -luser32 -lkernel32
    goto done
)

echo [Notice] To build native in-process DLL, install MSVC C++ tools, MinGW, or CMake.
echo [Notice] Standalone native EverythingHttpPlugin.exe operates immediately via IPC without C compilation!

:done
endlocal
