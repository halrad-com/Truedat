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

REM Native ORT / DirectML siblings (~35 MB). The managed wrapper +
REM BCL polyfills are merged INTO truedat.exe by ILRepack; the natives
REM cannot be merged into a managed exe and ship beside it — same shape
REM as essentia_streaming_extractor_music.exe / ffmpeg.exe / fpcalc.exe.
REM DirectML.Debug.dll is intentionally NOT shipped (dev-time tracing only).
set "ORTBIN=Truedat\bin\Release\net48"
echo Copying ORT/DirectML native siblings...
copy /Y "%ORTBIN%\onnxruntime.dll" "dist\truedat\" >nul
if errorlevel 1 (
    echo ERROR: onnxruntime.dll not produced by build
    exit /b 1
)
copy /Y "%ORTBIN%\onnxruntime_providers_shared.dll" "dist\truedat\" >nul
if errorlevel 1 (
    echo ERROR: onnxruntime_providers_shared.dll not produced by build
    exit /b 1
)
copy /Y "%ORTBIN%\DirectML.dll" "dist\truedat\" >nul
if errorlevel 1 (
    echo ERROR: DirectML.dll not produced by build
    exit /b 1
)
echo Done: onnxruntime.dll, onnxruntime_providers_shared.dll, DirectML.dll

echo.
echo ========================================
echo Build complete!
echo ========================================
echo.
echo Output: dist\truedat\truedat.exe (+ native ORT/DirectML siblings)
echo.
echo Copy dist\truedat\ contents to any folder and run:
echo   truedat.exe "iTunes Music Library.xml"
echo   truedat.exe "iTunes Music Library.xml" --fingerprint
echo.
pause
