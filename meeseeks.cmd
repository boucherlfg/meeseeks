@echo off
setlocal
set "ROOT=%~dp0"
set "EXE=%ROOT%src\Meeseeks\bin\Release\net10.0\Meeseeks.exe"
if exist "%EXE%" (
  "%EXE%" %*
  exit /b %ERRORLEVEL%
)
dotnet run --project "%ROOT%src\Meeseeks\Meeseeks.csproj" -c Release -- %*
exit /b %ERRORLEVEL%
