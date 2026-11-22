@echo off
setlocal
pushd "%~dp0" || exit /b 1
set "COMPILER=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%COMPILER%" set "COMPILER=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%COMPILER%" (
  echo .NET Framework 4.x compiler was not found.
  popd
  exit /b 1
)
set "SHARED=/nologo /codepage:65001 /platform:anycpu /optimize+ /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Security.dll"
"%COMPILER%" %SHARED% /target:winexe /main:MaaEndNotify.Program /out:MaaEndNotifier.exe src\*.cs
if errorlevel 1 (
  popd
  exit /b 1
)
"%COMPILER%" %SHARED% /target:exe /main:MaaEndNotify.Cli /out:MaaEndNotifyCli.exe src\*.cs
set "BUILD_STATUS=%ERRORLEVEL%"
if "%BUILD_STATUS%"=="0" echo Built MaaEndNotifier.exe and MaaEndNotifyCli.exe
popd
exit /b %BUILD_STATUS%
