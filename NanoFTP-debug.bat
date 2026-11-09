@echo off
setlocal
cd /d "%~dp0"
title NanoFTP - Debug
dotnet run -c Debug --project src\NanoFTP\NanoFTP.csproj
echo.
echo NanoFTP s'est arrete.
pause
