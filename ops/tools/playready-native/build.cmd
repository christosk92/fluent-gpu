@echo off
REM Build FluentGpu.PlayReady.Native.dll - in-process Win32 DLL (not a UWP sidecar).
REM The ABI is FgPlayReady.h: a process-lifetime runtime with license + session handles, split over three
REM translation units - PrRuntime.cpp (MF, D3D11, CDM/PMP, the media engine, the runtime thread), PrLicense.cpp
REM (the KID-keyed license cache) and PrSession.cpp (sessions, the feeder, the demuxer gate). PrInternal.h,
REM SegmentStore.h and CencMediaSource.h are shared headers.
REM The FG_UWP / FG_WIN32_PMP / FG_DESKTOP_DLL defines are kept as the build's historical identity; no source file
REM selects on them any more.
setlocal
set HERE=%~dp0
set ARCH=%~1
if "%ARCH%"=="" set ARCH=arm64
set OUT=out\%ARCH%

REM vcvarsall.bat: FG_VCVARSALL if set, else the newest Visual Studio (any edition, previews included) that vswhere
REM reports, else the owner's VS 18 Community path. vswhere always lives in the Installer folder, so this finds VS 2022
REM Enterprise on a GitHub windows-latest runner as well as a local VS 18 install. Labels, not if-blocks: the
REM parentheses in %ProgramFiles(x86)% end a parenthesised block early.
set VCVARS=
if defined FG_VCVARSALL set "VCVARS=%FG_VCVARSALL%"
if defined VCVARS goto :havevcvars
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto :fallbackvcvars
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -prerelease -products * -property installationPath`) do set "VCVARS=%%i\VC\Auxiliary\Build\vcvarsall.bat"
if defined VCVARS if exist "%VCVARS%" goto :havevcvars
:fallbackvcvars
set "VCVARS=C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat"
:havevcvars
if not exist "%VCVARS%" ( echo vcvarsall.bat not found - install the MSVC C++ build tools or set FG_VCVARSALL & exit /b 1 )

call "%VCVARS%" %ARCH% >nul
if errorlevel 1 ( echo vcvarsall failed: "%VCVARS%" %ARCH% & exit /b 1 )

pushd "%HERE%"
if not exist %OUT% mkdir %OUT%
if not exist %OUT%\linktmp mkdir %OUT%\linktmp
set TEMP=%HERE%%OUT%\linktmp
set TMP=%TEMP%

REM /utf-8: the sources are UTF-8 (box-drawing comments, a few non-ASCII log literals); without it MSVC reads them in the
REM machine's ANSI code page and a narrow literal's bytes depend on which box built the DLL.
cl /nologo /std:c++20 /utf-8 /EHsc /MD /O2 /DWIN32 /D_UNICODE /DUNICODE /DFG_UWP /DFG_WIN32_PMP /DFG_DESKTOP_DLL ^
   /I "%HERE%generated" ^
   /Fo"%OUT%\\" /Fe"%OUT%\FluentGpu.PlayReady.Native.dll" /LD ^
   PrRuntime.cpp PrLicense.cpp PrSession.cpp ^
   /link WindowsApp.lib
set RC=%errorlevel%

:done
popd
endlocal & exit /b %RC%
