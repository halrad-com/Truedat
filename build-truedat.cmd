@echo off
REM Build Truedat - C# scanner with ILRepack
REM Output: dist/truedat/truedat.exe

setlocal
cd /d %~dp0

echo ========================================
echo Building Truedat
echo ========================================
echo.

REM Check for .NET SDK
where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET SDK not found in PATH
    echo Install with: winget install Microsoft.DotNet.SDK.8
    pause
    exit /b 1
)

REM Build C# console app (ILRepack merges System.Text.Json)
echo Building C# scanner...
if not exist dist\truedat mkdir dist\truedat
dotnet clean Truedat/Truedat.csproj -c Release -f net48 >nul 2>&1
dotnet build Truedat/Truedat.csproj -c Release -f net48
if errorlevel 1 (
    echo ERROR: Build failed
    pause
    exit /b 1
)
copy /Y Truedat\bin\Release\net48\truedat.exe dist\truedat\
echo Done: truedat.exe

REM Native onnxruntime.dll sibling (~17 MB). The managed wrapper + BCL
REM polyfills are merged INTO truedat.exe by ILRepack; the native cannot
REM be merged into a managed exe and ships beside it — same shape as
REM essentia_streaming_extractor_music.exe / ffmpeg.exe / fpcalc.exe.
REM VAM uses CPU EP only (see project memory vam-models-no-gpu-benefit):
REM no DirectML.dll, no onnxruntime_providers_shared.dll, no NPU.
set "ORTBIN=Truedat\bin\Release\net48"
echo Copying onnxruntime.dll sibling...
copy /Y "%ORTBIN%\onnxruntime.dll" "dist\truedat\" >nul
if errorlevel 1 (
    echo ERROR: onnxruntime.dll not produced by build
    pause
    exit /b 1
)
echo Done: onnxruntime.dll

REM Ship README.md alongside the binary so the distributed bundle is self-documenting.
echo Copying README.md...
copy /Y README.md "dist\truedat\" >nul
if errorlevel 1 (
    echo ERROR: README.md copy failed
    pause
    exit /b 1
)
echo Done: README.md

echo.
echo ========================================
echo Build complete!
echo ========================================
echo.
echo Output: dist\truedat\truedat.exe (+ onnxruntime.dll sibling)
echo.
echo Copy dist\truedat\ contents to any folder and run:
echo   truedat.exe "iTunes Music Library.xml"
echo   truedat.exe "iTunes Music Library.xml" --fingerprint
echo.
