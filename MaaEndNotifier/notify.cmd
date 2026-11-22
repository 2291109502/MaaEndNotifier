@echo off
setlocal
"%~dp0MaaEndNotifyCli.exe" %*
exit /b %ERRORLEVEL%
