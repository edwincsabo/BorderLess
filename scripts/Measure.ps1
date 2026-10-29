param([string]$Label = 'current')
$ErrorActionPreference = 'Stop'
$borderRoot = Split-Path $PSScriptRoot -Parent
if ($Label -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Usa una etiqueta de letras, números, guion o guion bajo.' }
$env:DOTNET_CLI_HOME = Join-Path $borderRoot '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $borderRoot
try {
    & dotnet build tests/BorderLess.Benchmarks/BorderLess.Benchmarks.csproj -c Release -m:1 -p:UseSharedCompilation=false --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar el banco de medidas.' }
    & '.\tests\BorderLess.Benchmarks\bin\Release\net10.0\BorderLess.Benchmarks.exe' $Label (Join-Path 'artifacts\performance' ($Label+'.json'))
    if ($LASTEXITCODE -ne 0) { throw 'La medición falló.' }
} finally { Pop-Location }
