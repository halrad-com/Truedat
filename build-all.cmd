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
dotnet build Truedat/Truedat.csproj -c Release -f net48 --no-incremental
if errorlevel 1 (
    echo ERROR: Build failed
    pause
    exit /b 1
)
copy /Y Truedat\bin\Release\net48\truedat.exe dist\truedat\
echo Done: truedat.exe

echo.
echo ========================================
echo Build complete!
echo ========================================
echo.
echo Output: dist\truedat\truedat.exe
echo.
echo Copy dist\truedat\ contents to any folder and run:
echo   truedat.exe "iTunes Music Library.xml"
echo   truedat.exe "iTunes Music Library.xml" --fingerprint
echo.
pause
