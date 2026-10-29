$ErrorActionPreference = 'Stop'
$borderRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $borderRoot '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $borderRoot
try {
    & dotnet build BorderLess.slnx -c Release -m:1 -p:UseSharedCompilation=false --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Compilación fallida.' }
    & dotnet run --project tests/BorderLess.Tests/BorderLess.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas de protocolo/red.' }
    & dotnet run --project tests/BorderLess.Windows.Tests/BorderLess.Windows.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas de integración Windows.' }
    & dotnet run --project tests/BorderLess.Windows.Tests/BorderLess.Windows.Tests.csproj -c Release --no-build -- --ui-lifecycle
    if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas de interfaz, tema o bandeja.' }
    & dotnet run --project tests/BorderLess.Windows.Tests/BorderLess.Windows.Tests.csproj -c Release --no-build -- --clipboard-pipeline
    if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas del trabajador de portapapeles.' }
    & dotnet run --project tests/BorderLess.Windows.Tests/BorderLess.Windows.Tests.csproj -c Release --no-build -- --cursor-artwork
    if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas de formas de cursor.' }
} finally { Pop-Location }
