param([switch]$SkipPortableBuild)
$ErrorActionPreference = 'Stop'
$borderRoot = Split-Path $PSScriptRoot -Parent
[xml]$borderProject = Get-Content -Raw -LiteralPath (Join-Path $borderRoot 'src\BorderLess.App\BorderLess.App.csproj')
$borderVersion = [string]$borderProject.Project.PropertyGroup.Version
if ($borderVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid application version.' }
if (!$SkipPortableBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -Portable }
$borderPayload = Join-Path $borderRoot ('dist\BorderLess-' + $borderVersion)
if (!(Test-Path -LiteralPath (Join-Path $borderPayload 'BorderLess.exe'))) { throw 'Build the portable payload first.' }
if (Test-Path -LiteralPath (Join-Path $borderPayload 'data')) { throw 'Installer payload contains user data.' }
$borderCompiler = Join-Path $borderRoot 'tools\InnoSetup\ISCC.exe'
if (!(Test-Path -LiteralPath $borderCompiler)) { throw 'Install the official Inno Setup compiler in tools\InnoSetup or update this script to its location.' }
& $borderCompiler /Q ('/DAppVersion=' + $borderVersion) ('/DPayloadDir=' + $borderPayload) ('/DOutputDir=' + (Join-Path $borderRoot 'dist')) (Join-Path $borderRoot 'installer\BorderLess.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$borderSetup = Join-Path $borderRoot ('dist\BorderLess-' + $borderVersion + '-setup-win-x64.exe')
('{0}  {1}' -f (Get-FileHash -LiteralPath $borderSetup -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $borderSetup -Leaf)) | Set-Content -LiteralPath (Join-Path $borderRoot ('dist\SHA256-installer-' + $borderVersion + '.txt')) -Encoding utf8
Write-Output ('Installer: ' + $borderSetup)
