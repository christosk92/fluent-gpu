@ECHO OFF
SETLOCAL
REM NativeAOT publish for Wavee. Usage: publish-aot.cmd [rid]   (default rid: win-arm64)
REM Output: app\Wavee\bin\Release\net10.0\<rid>\publish\Wavee.exe
REM
REM Self-heals the two environment quirks that break the ILC native-link step (error MSB3073 / exit 123
REM with a linker path polluted by "'vswhere.exe' is not recognized"):
REM  1) ProgramFiles(x86) can be missing when the build is launched from an MSYS/Git-Bash-ancestry process
REM     (POSIX env names can't contain parens) -> ilcompiler's findvcvarsall.bat then can't locate vswhere.
REM  2) VS 2026 (18.x preview) vcvarsall.bat internally invokes a BARE `vswhere.exe` (PATH lookup); when the
REM     VS Installer dir is not on PATH its stderr leaks into MSBuild's ConsoleToMSBuild capture and corrupts
REM     the parsed CppLinker path -> prepend the Installer dir so the bare lookup succeeds and stderr stays clean.
IF "%ProgramFiles(x86)%"=="" SET "ProgramFiles(x86)=%SystemDrive%\Program Files (x86)"
SET "PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%"

SET "RID=%~1"
IF "%RID%"=="" SET "RID=win-arm64"

dotnet publish "%~dp0Wavee" -c Release -r %RID%
EXIT /B %ERRORLEVEL%
