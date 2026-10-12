@echo off
setlocal EnableExtensions DisableDelayedExpansion
pushd "%~dp0"
if errorlevel 1 exit /b 1

if not "%~2"=="" goto usage
if "%~1"=="" goto arguments_ok
if /i "%~1"=="test" goto arguments_ok
goto usage

:arguments_ok
set "TRUECACHE_TESTS=%~1"
set "TRUECACHE_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%TRUECACHE_VSWHERE%" (
    echo Visual Studio Installer vswhere.exe was not found.
    goto failed
)
set "TRUECACHE_VS="
for /f "usebackq delims=" %%I in (`"%TRUECACHE_VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "TRUECACHE_VS=%%I"
if not defined TRUECACHE_VS (
    echo Install the Visual Studio C++ desktop tools and Windows SDK.
    goto failed
)
if not exist "%TRUECACHE_VS%\Common7\Tools\VsDevCmd.bat" (
    echo The Visual Studio developer command script was not found.
    goto failed
)

if not exist out\probe mkdir out\probe
if not exist out\tests mkdir out\tests
if not exist out\probe goto failed
if not exist out\tests goto failed
call "%TRUECACHE_VS%\Common7\Tools\VsDevCmd.bat" -no_logo -arch=x64 -host_arch=x64 >out\toolchain.log 2>&1
if errorlevel 1 (
    echo Toolchain setup failed. See TrueCache\out\toolchain.log.
    goto failed
)

set "TRUECACHE_CXX=/nologo /std:c++20 /EHsc /W4 /WX /permissive- /Zc:__cplusplus /utf-8 /MT /O2 /Z7 /DNDEBUG /DWIN32_LEAN_AND_MEAN /DNOMINMAX /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 /DWINVER=0x0A00 /guard:cf"
set "TRUECACHE_LINK=/INCREMENTAL:NO /DEBUG:FULL /OPT:REF /OPT:ICF /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /GUARD:CF /MACHINE:X64"
cl %TRUECACHE_CXX% /Foout\probe\ /Feout\truecache-probe.exe probe\ProbeMain.cpp Diagnostics.cpp /link %TRUECACHE_LINK% /MANIFEST:EMBED /MANIFESTINPUT:probe\TrueCacheProbe.manifest /PDB:out\truecache-probe.pdb Advapi32.lib Bcrypt.lib Version.lib Wintrust.lib Setupapi.lib >out\build.log 2>&1
if errorlevel 1 (
    echo Probe build failed. See TrueCache\out\build.log.
    goto failed
)
echo Built TrueCache\out\truecache-probe.exe ^(Release x64^).
if /i not "%TRUECACHE_TESTS%"=="test" goto succeeded

cl %TRUECACHE_CXX% /Foout\tests\ /Feout\truecache-probe-tests.exe tests\ProbePolicyTests.cpp /link %TRUECACHE_LINK% /PDB:out\truecache-probe-tests.pdb >out\test-build.log 2>&1
if errorlevel 1 (
    echo Test build failed. See TrueCache\out\test-build.log.
    goto failed
)
out\truecache-probe-tests.exe >out\policy-tests.log 2>&1
if errorlevel 1 (
    echo Policy checks failed. See TrueCache\out\policy-tests.log.
    goto failed
)
powershell.exe -NoProfile -File tests\Test-Inspect.ps1 -ProbePath "%CD%\out\truecache-probe.exe" >out\inspect-tests.log 2>&1
if errorlevel 1 (
    echo Inspection checks failed. See TrueCache\out\inspect-tests.log.
    goto failed
)
echo Policy and read-only inspection checks passed. Logs are in TrueCache\out.

:succeeded
popd
exit /b 0

:usage
echo Usage: TrueCache\build.cmd [test]
echo Builds only the TrueCache probe in Release x64. test also runs its checks.
:failed
popd
exit /b 1
