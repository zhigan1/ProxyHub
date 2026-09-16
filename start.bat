@echo off
title ProxyHub Gateway (127.0.0.1:8787)
cd /d "%~dp0"
echo Starting ProxyHub Gateway on http://127.0.0.1:8787 ...
dotnet run -c Release
pause