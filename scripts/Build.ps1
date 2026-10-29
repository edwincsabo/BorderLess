param([switch]$Portable)
$ErrorActionPreference = 'Stop'
$borderRoot = Split-Path $PSScriptRoot -Parent
[xml]$borderProject = Get-Content -Raw -LiteralPath (Join-Path $borderRoot 'src\BorderLess.App\BorderLess.App.csproj')
$borderAppVersion = [string]$borderProject.Project.PropertyGroup.Version
if ($borderAppVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'La versión del proyecto no es válida.' }
$env:DOTNET_CLI_HOME = Join-Path $borderRoot '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $borderRoot
try {
    & dotnet build BorderLess.slnx -c Release -m:1 -p:UseSharedCompilation=false --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'La compilación falló.' }
    if (!$Portable) { return }
    $borderStage = Join-Path $borderRoot ('artifacts\package-' + [guid]::NewGuid().ToString('N'))
    $borderApp = Join-Path $borderStage ('BorderLess-' + $borderAppVersion)
    $borderRelay = Join-Path $borderApp 'relay'
    New-Item -ItemType Directory -Path $borderApp,$borderRelay -Force | Out-Null
    & dotnet publish src/BorderLess.App/BorderLess.App.csproj -c Release -r win-x64 --self-contained false -m:1 -p:UseSharedCompilation=false -p:AppHostDotNetSearch=AppRelative -p:AppHostRelativeDotNet=runtime --nologo -v minimal -o $borderApp
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo publicar la aplicación.' }
    & dotnet publish src/BorderLess.Relay/BorderLess.Relay.csproj -c Release -r win-x64 --self-contained false -m:1 -p:UseSharedCompilation=false -p:AppHostDotNetSearch=AppRelative -p:AppHostRelativeDotNet=../runtime --nologo -v minimal -o $borderRelay
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo publicar el relay.' }
    $borderDotnet = Split-Path (Get-Command dotnet).Source -Parent
    $borderRuntime = Join-Path $borderApp 'runtime'
    $borderVersion = Get-ChildItem (Join-Path $borderDotnet 'shared\Microsoft.WindowsDesktop.App') -Directory | Where-Object { $_.Name -match '^10\.0\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
    if (!$borderVersion) { throw 'Se necesita el runtime Windows Desktop .NET 10 instalado para preparar el paquete portable.' }
    foreach ($borderFramework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
        $borderSource = Join-Path $borderDotnet "shared\$borderFramework\$borderVersion"
        if (!(Test-Path -LiteralPath $borderSource)) { throw "No existe la versión coincidente: $borderSource" }
        $borderDestination = Join-Path $borderRuntime "shared\$borderFramework"
        New-Item -ItemType Directory -Path $borderDestination -Force | Out-Null
        Copy-Item -LiteralPath $borderSource -Destination $borderDestination -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $borderRuntime 'host\fxr') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $borderDotnet "host\fxr\$borderVersion") -Destination (Join-Path $borderRuntime 'host\fxr') -Recurse -Force
    foreach ($borderName in @('dotnet.exe','LICENSE.txt','ThirdPartyNotices.txt')) { Copy-Item -LiteralPath (Join-Path $borderDotnet $borderName) -Destination $borderRuntime -Force }
    Copy-Item -LiteralPath (Join-Path $borderRoot 'COMO_USAR.md') -Destination $borderApp -Force
    Copy-Item -LiteralPath (Join-Path $borderRoot 'INSTALAR.md') -Destination $borderApp -Force
    foreach ($borderNotice in @('LICENSE','THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $borderRoot $borderNotice) -Destination $borderApp -Force }
    if (Test-Path -LiteralPath (Join-Path $borderRoot 'ACTUALIZAR.md')) { Copy-Item -LiteralPath (Join-Path $borderRoot 'ACTUALIZAR.md') -Destination $borderApp -Force }
    $borderManifest = [ordered]@{version=$borderAppVersion;platform='Windows 11 x64';runtime=$borderVersion;packaged=(Get-Date).ToString('O');files=@(Get-ChildItem $borderApp -File -Recurse | ForEach-Object { [ordered]@{path=$_.FullName.Substring($borderApp.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower()} })}
    $borderManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $borderApp 'manifest.json') -Encoding utf8
    $borderDist = Join-Path $borderRoot 'dist'
    New-Item -ItemType Directory -Path $borderDist -Force | Out-Null
    # The stage is always new: no local identity, data folder or pairing key enters the ZIP.
    $borderFullZip = Join-Path $borderDist ('BorderLess-' + $borderAppVersion + '-win-x64.zip')
    Compress-Archive -LiteralPath $borderApp -DestinationPath $borderFullZip -CompressionLevel Optimal -Force
    Copy-Item -LiteralPath $borderApp -Destination $borderDist -Recurse -Force
    $borderUpdate = Join-Path $borderStage ('Copiar_en_BorderLess-' + $borderAppVersion)
    New-Item -ItemType Directory -Path $borderUpdate -Force | Out-Null
    Get-ChildItem -LiteralPath $borderApp -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $borderUpdate -Force }
    # A desktop-only update must describe only the files it replaces, not claim
    # that the untouched relay/runtime already match a new full distribution.
    $borderUpdateManifest = [ordered]@{version=$borderAppVersion;packageKind='desktop-update';requires='BorderLess 0.1 con runtime .NET 10';files=@(Get-ChildItem -LiteralPath $borderUpdate -File | Where-Object Name -ne 'manifest.json' | ForEach-Object { [ordered]@{path=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower()} })}
    $borderUpdateManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $borderUpdate 'manifest.json') -Encoding utf8
    $borderUpdateZip = Join-Path $borderDist ('BorderLess-' + $borderAppVersion + '-actualizacion-win-x64.zip')
    Compress-Archive -LiteralPath $borderUpdate -DestinationPath $borderUpdateZip -CompressionLevel Optimal -Force
    @($borderFullZip,$borderUpdateZip) | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLower(), (Split-Path $_ -Leaf) } | Set-Content -LiteralPath (Join-Path $borderDist ('SHA256-' + $borderAppVersion + '.txt')) -Encoding utf8
    Write-Output ('Portable: ' + (Join-Path $borderDist ('BorderLess-' + $borderAppVersion + '\BorderLess.exe')))
    Write-Output ('Actualización sin datos: ' + $borderUpdateZip)
} finally { Pop-Location }
