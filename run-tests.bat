@echo off
echo =========================================
echo   GameTranslate - Executando Testes
echo =========================================
cd /d "%~dp0"
dotnet test tests\GameTranslate.Tests\GameTranslate.Tests.csproj -c Release --verbosity normal
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo FALHA em um ou mais testes!
    exit /b %ERRORLEVEL%
)
echo.
echo TODOS OS TESTES PASSARAM!
