@echo off
REM Sign dist\truedat\truedat.exe with the Halrad LLC certificate.
REM Run after build-truedat.cmd, before make-zips.cmd - the zips must carry the signed bytes.
REM
REM Modelled on mbxspout\sign.cmd (same certificate, same timestamp authority, RFC3161
REM /tr + /td sha256). Only truedat.exe is signed: essentia, ffmpeg and ffprobe beside it
REM are third-party binaries and are shipped as their publishers built them.
REM
REM Signing rewrites the exe, so it also changes the file committed in the build commit -
REM sign first, then commit "build: truedat.exe <version>".

setlocal
set "EXE=%~dp0truedat\truedat.exe"

call "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\Tools\VsDevCmd.bat" >nul || (
  echo VsDevCmd not found - signtool needs it. & exit /b 1
)

if not exist "%EXE%" (
  echo Nothing to sign: "%EXE%" does not exist.
  echo Run build-truedat.cmd first.
  exit /b 1
)

echo.
echo Version:
"%EXE%" --version

echo.
echo Signing %EXE% ...
signtool.exe sign /a /d "truedat - music analysis for MBXHub" /n "Halrad LLC" /du "https://halrad.com/truedat/" /fd sha256 /tr "http://timestamp.sectigo.com" /td sha256 "%EXE%" || (
  echo. & echo SIGNING FAILED. & exit /b 1
)

echo.
echo Verifying ...
signtool.exe verify /pa /v "%EXE%" || (echo. & echo VERIFY FAILED. & exit /b 1)

echo.
echo Hashing the signed bytes ...
certutil -hashfile "%EXE%" SHA256 || (echo. & echo HASHING FAILED. & exit /b 1)

for /f "usebackq delims=" %%t in (`powershell -NoProfile -Command "(Get-AuthenticodeSignature '%EXE%').SignerCertificate.Thumbprint"`) do echo signer:  %%t

echo.
echo Signed. Next: make-zips.cmd, then commit the build.
exit /b 0
