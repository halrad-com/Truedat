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

REM Report what this build actually IS, the way MBXHub's build script does. The version
REM suffix is the git commit, so this line answers "what source is in this binary?" - the
REM question that previously required counting self-test assertions to answer.
for /f "delims=" %%v in ('dist\truedat\truedat.exe --version') do set TRUEDAT_VER=%%v
echo Version: %TRUEDAT_VER%
echo %TRUEDAT_VER% | findstr /C:"+dirty" >nul
if not errorlevel 1 (
    echo.
    echo WARNING: built from a DIRTY tree - this binary contains uncommitted changes under
    echo          Truedat\ and is NOT described by commit %TRUEDAT_VER%. Do not ship or
    echo          commit it as a build of that commit; commit the source first, then rebuild.
)

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
echo Output: dist\truedat\truedat.exe
echo.
echo Copy dist\truedat\ contents to any folder and run:
echo   truedat.exe "iTunes Music Library.xml"
echo   truedat.exe "iTunes Music Library.xml" --fingerprint
echo.
