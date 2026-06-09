@echo off
REM Build the NPU MCDM verification spike — IDXGIFactory6 + D3D12 + DML EP test scaffold.
REM Output: dist\ortspike-npu\ortspike-npu.exe (ILRepack-merged) + 3 native sibling DLLs.
REM
REM Run on a target box (Intel AI Boost or AMD Ryzen AI) to investigate
REM whether the NPU surfaces via the MCDM-aware DXGI API. Subcommand:
REM   ortspike-npu.exe list-adapters

setlocal
cd /d %~dp0

echo ========================================
echo Building ORT NPU Spike (MCDM)
echo ========================================
echo.

REM Check for .NET SDK
where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET SDK not found in PATH
    echo Install with: winget install Microsoft.DotNet.SDK.8
    exit /b 1
)

echo Building OrtSpikeNpu.csproj...
dotnet clean tools\ort-spike-npu\OrtSpikeNpu.csproj -c Release -f net48 >nul 2>&1
dotnet build tools\ort-spike-npu\OrtSpikeNpu.csproj -c Release -f net48
if errorlevel 1 (
    echo ERROR: Build failed
    pause
    exit /b 1
)

REM Publish to dist\ortspike-npu\ — same pattern as build-ortspike.cmd.
echo.
echo Publishing to dist\ortspike-npu\...
if not exist dist\ortspike-npu mkdir dist\ortspike-npu

set "BIN=tools\ort-spike-npu\bin\Release\net48"
copy /Y "%BIN%\ilrepacked-ortspike-npu.exe" "dist\ortspike-npu\ortspike-npu.exe" >nul
copy /Y "%BIN%\ilrepacked-ortspike-npu.exe.config" "dist\ortspike-npu\ortspike-npu.exe.config" >nul 2>&1
copy /Y "%BIN%\onnxruntime.dll" "dist\ortspike-npu\" >nul
copy /Y "%BIN%\DirectML.dll" "dist\ortspike-npu\" >nul
copy /Y "%BIN%\onnxruntime_providers_shared.dll" "dist\ortspike-npu\" >nul

if not exist "dist\ortspike-npu\ortspike-npu.exe" (
    echo ERROR: ortspike-npu.exe not produced
    exit /b 1
)
if not exist "dist\ortspike-npu\onnxruntime.dll" (
    echo ERROR: onnxruntime.dll not copied
    exit /b 1
)
if not exist "dist\ortspike-npu\DirectML.dll" (
    echo ERROR: DirectML.dll not copied
    exit /b 1
)

echo Done: dist\ortspike-npu\

echo.
echo ========================================
echo Build complete!
echo ========================================
echo.
echo Files published:
dir /b dist\ortspike-npu\
echo.
echo To list adapters via the MCDM-aware DXGI API:
echo   dist\ortspike-npu\ortspike-npu.exe list-adapters ^> npu-adapters.txt 2^>^&1
echo.
echo Compare adapter enumeration against the classic-API result from the
echo §5.0 ort-spike to decide whether the NPU surfaces on this hardware.
echo.
pause
