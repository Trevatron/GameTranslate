@echo off
echo =========================================
echo   GameTranslate - Build EXE Unico
echo =========================================
cd /d "%~dp0"
if exist dist rmdir /s /q dist
dotnet publish GameTranslate.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o dist
if %ERRORLEVEL% NEQ 0 (
    echo ERRO ao compilar!
    exit /b %ERRORLEVEL%
)
echo.
echo SUCESSO! Executavel unico gerado em: %~dp0dist\GameTranslate.exe
dir dist\GameTranslate.exe
