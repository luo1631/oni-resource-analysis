@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
set "setup_exit_code=%errorlevel%"
echo(
pause
exit /b %setup_exit_code%
