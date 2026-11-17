@echo off
chcp 65001 >nul
title Lossless Scaling Plus V3.0 — Sürükle-Bırak Kurulum & Doğrulama Aracı
echo ============================================================================
echo   Lossless Scaling Plus V3.0 — Sürükle-Bırak Addon ^& Shader Optimizer
echo   Python Gerektirmez — Yerleşik C# D3DCompiler_47 Motoru
echo ============================================================================
echo.

if exist "%~dp0bin\LosslessPlusCompanion.exe" (
    echo [*] LosslessPlusCompanion baslatiliyor...
    if "%~1"=="" (
        start "" "%~dp0bin\LosslessPlusCompanion.exe"
    ) else (
        start "" "%~dp0bin\LosslessPlusCompanion.exe" "%~1"
    )
) else (
    echo [*] Companion derleniyor...
    dotnet build "%~dp0src\CompanionApp\LosslessPlusCompanion.csproj" -c Release -o "%~dp0bin"
    if "%~1"=="" (
        start "" "%~dp0bin\LosslessPlusCompanion.exe"
    ) else (
        start "" "%~dp0bin\LosslessPlusCompanion.exe" "%~1"
    )
)
exit /b 0
