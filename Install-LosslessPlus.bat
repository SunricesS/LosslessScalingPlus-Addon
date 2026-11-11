@echo off
chcp 65001 >nul
title Lossless Scaling Plus — Addon & Local Shader Optimizer
echo ============================================================================
echo   Lossless Scaling Plus (LSFG 3.1 Ultimate) — Addon ^& Shader Optimizer
echo   BYOB (Bring Your Own Binary) Clean-Room Open-Source Distribution
echo ============================================================================
echo.

if exist "%~dp0bin\LosslessPlusCompanion.exe" (
    echo [*] Companion Addon Arayuzu baslatiliyor...
    start "" "%~dp0bin\LosslessPlusCompanion.exe"
) else (
    echo [*] Companion derleniyor...
    dotnet build "%~dp0src\CompanionApp\LosslessPlusCompanion.csproj" -c Release -o "%~dp0bin"
    start "" "%~dp0bin\LosslessPlusCompanion.exe"
)
exit /b 0
