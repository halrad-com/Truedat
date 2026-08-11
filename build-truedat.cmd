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

REM Report what we are ABOUT to build, up front like MBXHub's script. Read straight from git
REM rather than from the exe, because the exe does not exist yet - and this is the same state
REM the csproj stamps into the binary, so what you see here is what --version will report.
REM Up front matters for the dirty warning especially: knowing the tree is modified is worth
REM more before you spend the build than after it has scrolled past.
set TD_DESC=unknown
set TD_BRANCH=unknown
set TD_DIRTY=
for /f "delims=" %%d in ('git describe --tags --always 2^>nul') do set TD_DESC=%%d
for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD 2^>nul') do set TD_BRANCH=%%b
for /f "delims=" %%s in ('git status --porcelain --untracked-files=no -- Truedat 2^>nul') do set TD_DIRTY=1
echo Source:  %TD_DESC%  (branch %TD_BRANCH%)
if defined TD_DIRTY (
    echo.
    echo *** DIRTY TREE - uncommitted changes under Truedat\ ***
    echo     This binary will NOT be described by commit %TD_DESC%; --version will say +dirty.
    echo     Commit the source first if you intend to ship or commit this build.
    echo.
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

REM Ship the docs alongside the binary so the distributed bundle is self-documenting.
REM The ROOT copy is the master; dist\truedat\ is GENERATED from it on every build. Never
REM edit dist\truedat\README.md or dist\truedat\SBOM.md by hand — the next build overwrites
REM them. This direction is deliberate and was learned the hard way: the two copies drifted,
REM the corrected text ended up only in the dist copy, and a routine build would have
REM silently replaced it with the stale root version.
echo Copying README.md...
copy /Y README.md "dist\truedat\" >nul
if errorlevel 1 (
    echo ERROR: README.md copy failed
    pause
    exit /b 1
)
echo Done: README.md

echo Copying SBOM.md...
copy /Y SBOM.md "dist\truedat\" >nul
if errorlevel 1 (
    echo ERROR: SBOM.md copy failed
    pause
    exit /b 1
)
echo Done: SBOM.md

REM mbxmoods-schema.json — the field-emission policy the writer + --fixup read at runtime
REM (beside the exe). Absent it, truedat falls back to a built-in default that still cuts
REM bpmHistogram, so shipping it is what lets the policy be edited without a rebuild.
echo Copying mbxmoods-schema.json...
copy /Y mbxmoods-schema.json "dist\truedat\" >nul
if errorlevel 1 (
    echo ERROR: mbxmoods-schema.json copy failed
    pause
    exit /b 1
)
echo Done: mbxmoods-schema.json

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
