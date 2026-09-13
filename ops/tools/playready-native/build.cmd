@echo off
REM Build FluentGpu.PlayReady.Native.dll - in-process Win32 DLL (not a UWP sidecar).
REM The ABI is FgPlayReady.h: a process-lifetime runtime with license + session handles, split over three
REM translation units - PrRuntime.cpp (MF, D3D11, CDM/PMP, the media engine, the runtime thread), PrLicense.cpp
REM (the KID-keyed license cache) and PrSession.cpp (sessions, the feeder, the demuxer gate). PrInternal.h,
REM SegmentStore.h and CencMediaSource.h are shared headers.
REM The FG_UWP / FG_WIN32_PMP / FG_DESKTOP_DLL defines are kept as the build's historical identity; no source file
REM selects on them any more.
setlocal
set VCVARS="C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat"
set HERE=%~dp0
set ARCH=%~1
if "%ARCH%"=="" set ARCH=arm64
set OUT=out\%ARCH%

call %VCVARS% %ARCH% >nul
if errorlevel 1 ( echo vcvarsall failed & exit /b 1 )

pushd "%HERE%"
if not exist %OUT% mkdir %OUT%
if not exist %OUT%\linktmp mkdir %OUT%\linktmp
set TEMP=%HERE%%OUT%\linktmp
set TMP=%TEMP%

cl /nologo /std:c++20 /EHsc /MD /O2 /DWIN32 /D_UNICODE /DUNICODE /DFG_UWP /DFG_WIN32_PMP /DFG_DESKTOP_DLL ^
   /I "%HERE%generated" ^
   /Fo"%OUT%\\" /Fe"%OUT%\FluentGpu.PlayReady.Native.dll" /LD ^
   PrRuntime.cpp PrLicense.cpp PrSession.cpp ^
   /link WindowsApp.lib
set RC=%errorlevel%
popd
endlocal & exit /b %RC%
