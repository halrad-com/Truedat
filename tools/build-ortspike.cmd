@echo off
REM Build the §5.0 ORT verification spike — net48 + DML + ILRepack test scaffold.
REM Output: dist\ortspike\ortspike.exe (ILRepack-merged) + 3 native sibling DLLs.
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

REM Publish to dist\ortspike\ — same pattern as build-truedat.cmd.
REM Ship the ILRepack-merged exe (managed parts folded together) plus the
REM 3 native sibling DLLs that ORT/DirectML require. Drops DirectML.Debug.dll
REM (dev-time tracing only) and the non-merged ortspike.exe + polyfills
REM (redundant once we have the merged variant).
echo.
echo Publishing to dist\ortspike\...
if not exist dist\ortspike mkdir dist\ortspike

set "BIN=tools\ort-spike\bin\Release\net48"
copy /Y "%BIN%\ilrepacked-ortspike.exe" "dist\ortspike\ortspike.exe" >nul
copy /Y "%BIN%\ilrepacked-ortspike.exe.config" "dist\ortspike\ortspike.exe.config" >nul 2>&1
copy /Y "%BIN%\onnxruntime.dll" "dist\ortspike\" >nul
copy /Y "%BIN%\DirectML.dll" "dist\ortspike\" >nul
copy /Y "%BIN%\onnxruntime_providers_shared.dll" "dist\ortspike\" >nul

if not exist "dist\ortspike\ortspike.exe" (
    echo ERROR: ortspike.exe not produced
    exit /b 1
)
if not exist "dist\ortspike\onnxruntime.dll" (
    echo ERROR: onnxruntime.dll not copied
    exit /b 1
)
if not exist "dist\ortspike\DirectML.dll" (
    echo ERROR: DirectML.dll not copied
    exit /b 1
)

echo Done: dist\ortspike\

echo.
echo ========================================
echo Build complete!
echo ========================================
echo.
echo Files published:
dir /b dist\ortspike\
echo.
echo To run the spike (capture output to a file):
echo   dist\ortspike\ortspike.exe ^> spike-output.txt 2^>^&1
echo.
echo The spike prints GREEN / YELLOW / RED at the top of its output and
echo enumerates DXGI adapters in step 5 — share spike-output.txt to
echo decide whether NPU dispatch works on this box.
echo.
pause
