@echo off
REM Build the §5.0 ORT verification spike — net48 + DML + ILRepack test scaffold.
REM Output: tools\ort-spike\bin\Release\net48\ortspike.exe
REM         tools\ort-spike\bin\Release\net48\ilrepacked-ortspike.exe
REM         + 3 native sibling DLLs (onnxruntime.dll, DirectML.dll, onnxruntime_providers_shared.dll)
REM
REM Run on a fresh box to re-execute the spike (e.g., AMD Ryzen AI to validate NPU dispatch).

setlocal
cd /d %~dp0

echo ========================================
echo Building ORT Spike (§5.0)
echo ========================================
echo.

REM Check for .NET SDK
where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET SDK not found in PATH
    echo Install with: winget install Microsoft.DotNet.SDK.8
    exit /b 1
)

echo Building OrtSpike.csproj...
dotnet clean tools\ort-spike\OrtSpike.csproj -c Release -f net48 >nul 2>&1
dotnet build tools\ort-spike\OrtSpike.csproj -c Release -f net48
if errorlevel 1 (
    echo ERROR: Build failed
    pause
    exit /b 1
)

echo.
echo ========================================
echo Build complete!
echo ========================================
echo.
echo To run the spike:
echo   tools\ort-spike\bin\Release\net48\ortspike.exe
echo.
echo Output captures: console output to a file via:
echo   tools\ort-spike\bin\Release\net48\ortspike.exe ^> spike-output.txt 2^>^&1
echo.
echo The spike checks: net48 ORT load, x64 binding, CPU EP inference,
echo DirectML GPU dispatch, NPU enumeration, ILRepack merge, determinism,
echo and per-EP performance. Verdict GREEN / RED / YELLOW prints at the top.
echo.
pause
