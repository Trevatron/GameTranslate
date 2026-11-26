$ErrorActionPreference = "Stop"

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$distDir = Join-Path $projectDir "dist"

Write-Host "=========================================" -ForegroundColor Yellow
Write-Host "  GameTranslate - Build EXE Unico        " -ForegroundColor Yellow
Write-Host "=========================================" -ForegroundColor Yellow

if (Test-Path $distDir) {
    Remove-Item $distDir -Recurse -Force
}

dotnet publish (Join-Path $projectDir "GameTranslate.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $distDir

$exePath = Join-Path $distDir "GameTranslate.exe"
if (Test-Path $exePath) {
    $sizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
    Write-Host ""
    Write-Host "SUCESSO! Executavel unico gerado em:" -ForegroundColor Green
    Write-Host "  $exePath ($sizeMb MB)" -ForegroundColor Cyan
    Write-Host "Pode copiar este unico .exe para qualquer Windows 10/11 (64-bit) sem instalar nada!" -ForegroundColor Green
} else {
    throw "Falha ao localizar GameTranslate.exe apos publish."
}
