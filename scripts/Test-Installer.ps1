param([Parameter(Mandatory=$true)][string]$PortableTestProfile)
$ErrorActionPreference = 'Stop'
$borderRoot = Split-Path $PSScriptRoot -Parent
[xml]$borderProject = Get-Content -Raw -LiteralPath (Join-Path $borderRoot 'src\BorderLess.App\BorderLess.App.csproj')
$borderVersion = [string]$borderProject.Project.PropertyGroup.Version
$borderId = [guid]::NewGuid().ToString('N')
$borderQa = Join-Path $borderRoot ('artifacts\installer-test-' + $borderId)
$borderProgram = Join-Path $borderQa 'program'
$borderData = Join-Path $borderQa 'profile'
$borderAppId = 'BorderLess.QA.' + $borderId
$borderRegistry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $borderAppId + '_is1'
New-Item -ItemType Directory -Path $borderQa -Force | Out-Null
function Assert-Installer([bool]$Value, [string]$Message) { if (!$Value) { throw $Message }; Write-Output ('PASS ' + $Message) }
function Invoke-InstallerProcess([string]$Exe, [string]$Arguments) {
    $borderProcess = Start-Process -FilePath $Exe -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (!$borderProcess.WaitForExit(120000)) { throw 'QA helper timed out; inspect its log before cleaning up.' }
    if ($borderProcess.ExitCode -ne 0) { throw ('QA process failed with exit code ' + $borderProcess.ExitCode + ': ' + $Exe) }
}
# A separate AppId, product name and data profile isolate QA from any real installation.
& (Join-Path $borderRoot 'tools\InnoSetup\ISCC.exe') /Q ('/DAppVersion=' + $borderVersion) ('/DPayloadDir=' + (Join-Path $borderRoot ('dist\BorderLess-' + $borderVersion))) ('/DOutputDir=' + $borderQa) '/DOutputBase=BorderLess-QA-setup' ('/DAppIdentity=' + $borderAppId) '/DProductName=BorderLess QA' ('/DProfileOverride=' + $borderData) (Join-Path $borderRoot 'installer\BorderLess.iss')
if ($LASTEXITCODE -ne 0) { throw 'QA installer compilation failed.' }
$borderSetup = Join-Path $borderQa 'BorderLess-QA-setup.exe'
$borderInstallArgs = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /DIR="' + $borderProgram + '" /LOG="' + (Join-Path $borderQa 'install.log') + '"'
Invoke-InstallerProcess $borderSetup $borderInstallArgs
Assert-Installer ((Test-Path -LiteralPath (Join-Path $borderProgram 'BorderLess.exe')) -and (Test-Path -LiteralPath (Join-Path $borderProgram 'installed-mode.txt'))) 'Instalador coloca el programa y el marcador de perfil instalado'
Assert-Installer ((Get-ItemProperty -LiteralPath $borderRegistry).InstallLocation.TrimEnd('\') -eq $borderProgram) 'Instalación por usuario registra su desinstalación aislada'
$borderPayload = Join-Path $borderRoot ('dist\BorderLess-' + $borderVersion)
$borderHostVersion = Get-ChildItem -LiteralPath (Join-Path $borderPayload 'runtime\host\fxr') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
foreach ($borderRelative in @('BorderLess.exe','BorderLess.dll','BorderLess.Core.dll','BouncyCastle.Cryptography.dll','runtime\dotnet.exe',('runtime\host\fxr\' + $borderHostVersion + '\hostfxr.dll'))) {
    Assert-Installer ((Get-FileHash -LiteralPath (Join-Path $borderProgram $borderRelative)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $borderPayload $borderRelative)).Hash) ('Archivo instalado coincide con distribución: ' + $borderRelative)
}
Assert-Installer (!(Test-Path -LiteralPath (Join-Path $borderProgram 'data'))) 'El instalador no distribuye identidades ni datos de usuario'
# Import runs the installed app and its bundled runtime without capture hooks or clipboard access.
Invoke-InstallerProcess (Join-Path $borderProgram 'BorderLess.exe') ('--data "' + $borderData + '" --import-profile "' + [IO.Path]::GetFullPath($PortableTestProfile) + '"')
$borderSettings = Join-Path $borderData 'settings.json'
$borderImported = Get-Content -Raw -LiteralPath $borderSettings | ConvertFrom-Json
$borderOriginal = Get-Content -Raw -LiteralPath (Join-Path $PortableTestProfile 'settings.json') | ConvertFrom-Json
Assert-Installer (($borderImported.Id -eq $borderOriginal.Id) -and ($borderImported.ProtectedCertificate -eq $borderOriginal.ProtectedCertificate) -and ($borderImported.Peers[0].ProtectedKey -eq $borderOriginal.Peers[0].ProtectedKey)) 'Aplicación instalada ejecuta migración y conserva identidad y vínculo de prueba'
$borderSettingsHash = (Get-FileHash -LiteralPath $borderSettings).Hash
$borderReceipt = Join-Path $borderImported.ReceiveDirectory 'existing.txt'
$borderReceiptHash = (Get-FileHash -LiteralPath $borderReceipt).Hash
Invoke-InstallerProcess $borderSetup ($borderInstallArgs.Replace('install.log','reinstall.log'))
Assert-Installer (((Get-FileHash -LiteralPath $borderSettings).Hash -eq $borderSettingsHash) -and ((Get-FileHash -LiteralPath $borderReceipt).Hash -eq $borderReceiptHash)) 'Reinstalar conserva configuración y archivo recibido sin cambios'
# Resolve and verify the exact workspace target before invoking its recursive uninstaller.
$borderVerified = [IO.Path]::GetFullPath($borderProgram).TrimEnd('\')
$borderAllowed = [IO.Path]::GetFullPath((Join-Path $borderRoot 'artifacts')).TrimEnd('\') + '\'
if (!$borderVerified.StartsWith($borderAllowed,[StringComparison]::OrdinalIgnoreCase) -or (Get-ItemProperty -LiteralPath $borderRegistry).DisplayName -notlike 'BorderLess QA*') { throw 'QA uninstall target did not pass workspace and product checks.' }
Invoke-InstallerProcess (Join-Path $borderVerified 'unins000.exe') ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + (Join-Path $borderQa 'uninstall.log') + '"')
Assert-Installer (!(Test-Path -LiteralPath (Join-Path $borderVerified 'BorderLess.exe')) -and !(Test-Path -LiteralPath $borderRegistry)) 'Desinstalación elimina solo el programa y su registro QA'
Assert-Installer (((Get-FileHash -LiteralPath $borderSettings).Hash -eq $borderSettingsHash) -and ((Get-FileHash -LiteralPath $borderReceipt).Hash -eq $borderReceiptHash)) 'Desinstalar conserva configuración y archivo recibido'
Write-Output ('QA evidence: ' + $borderQa)
