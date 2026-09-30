@echo off
REM Build FluentGpu.PlayReady.Native.dll - in-process Win32 DLL (not a UWP sidecar).
REM The ABI is FgPlayReady.h: a process-lifetime runtime with license + session handles, split over three
REM translation units - PrRuntime.cpp (MF, D3D11, CDM/PMP, the media engine, the runtime thread), PrLicense.cpp
REM (the KID-keyed license cache) and PrSession.cpp (sessions, the feeder, the demuxer gate). PrInternal.h,
REM SegmentStore.h and CencMediaSource.h are shared headers.
REM The FG_UWP / FG_WIN32_PMP / FG_DESKTOP_DLL defines are kept as the build's historical identity; no source file
REM selects on them any more.
REM
REM After the DLL link succeeds, tests\FeedTests.cpp (a dependency-free console exe over FeedPlan.h's plan::Next
REM and SegmentStore.h's sample-list algorithms - see tests\FeedTests.cpp's own header comment) is compiled and RUN;
REM a non-zero exit from either the compile or the run fails this build. Its objects land under %OUT%\tests\ so they
REM never collide with the DLL's own %OUT%\*.obj.
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

REM /utf-8: the sources are UTF-8 (box-drawing comments, a few non-ASCII log literals); without it MSVC reads them in the
REM machine's ANSI code page and a narrow literal's bytes depend on which box built the DLL.
cl /nologo /std:c++20 /utf-8 /EHsc /MD /O2 /DWIN32 /D_UNICODE /DUNICODE /DFG_UWP /DFG_WIN32_PMP /DFG_DESKTOP_DLL ^
   /I "%HERE%generated" ^
   /Fo"%OUT%\\" /Fe"%OUT%\FluentGpu.PlayReady.Native.dll" /LD ^
   PrRuntime.cpp PrLicense.cpp PrSession.cpp ^
   /link WindowsApp.lib
set RC=%errorlevel%

if not "%RC%"=="0" goto :done

REM FeedTests.exe - same language/warning flags as the DLL (minus /LD and the FG_* identity defines, which nothing
REM this test includes selects on) so it compiles against CencMediaSource.h/SegmentStore.h exactly as the DLL does.
if not exist %OUT%\tests mkdir %OUT%\tests
cl /nologo /std:c++20 /utf-8 /EHsc /MD /O2 /DWIN32 /D_UNICODE /DUNICODE ^
   /I "%HERE%generated" ^
   /Fo"%OUT%\tests\\" /Fe"%OUT%\FeedTests.exe" ^
   tests\FeedTests.cpp ^
   /link WindowsApp.lib
set RC=%errorlevel%
if not "%RC%"=="0" goto :done

REM An ARCH the host cannot execute (an arm64 exe on an x64 host, no emulation in that direction) skips the run
REM rather than failing the build; x64-on-arm64 runs fine under Windows' x64 emulation, so that pair still runs.
set HOSTARCH=%PROCESSOR_ARCHITECTURE%
if defined PROCESSOR_ARCHITEW6432 set HOSTARCH=%PROCESSOR_ARCHITEW6432%
set CANRUN=1
if /I "%ARCH%"=="arm64" if /I not "%HOSTARCH%"=="ARM64" set CANRUN=0

if "%CANRUN%"=="0" (
    echo Skipping FeedTests.exe run: %ARCH% binaries cannot execute on this %HOSTARCH% host.
    goto :done
)

"%OUT%\FeedTests.exe"
set RC=%errorlevel%

:done
popd
endlocal & exit /b %RC%
