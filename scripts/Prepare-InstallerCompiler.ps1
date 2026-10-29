$ErrorActionPreference = 'Stop'
$borderRoot = Split-Path $PSScriptRoot -Parent
$borderDownload = Join-Path $borderRoot 'vendor\inno\innosetup-7.1.1-x64.exe'
$borderCompilerDir = Join-Path $borderRoot 'tools\InnoSetup'
if (!(Test-Path -LiteralPath $borderDownload)) { Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' -OutFile $borderDownload }
if ((Get-FileHash -LiteralPath $borderDownload -Algorithm SHA256).Hash -ne '8A5A6711C3766A6E5FB74F280BA0E3551E719CE94538D901FDBD1F8F9920B3F0') { throw 'Inno Setup checksum mismatch.' }
$borderSignature = Get-AuthenticodeSignature -LiteralPath $borderDownload
if ($borderSignature.Status -ne 'Valid' -or $borderSignature.SignerCertificate.Subject -notmatch 'Pyrsys B.V.') { throw 'Inno Setup signature validation failed.' }
$borderArgs = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CURRENTUSER /NOICONS /TASKS="" /DIR="' + $borderCompilerDir + '"'
$borderProcess = Start-Process -FilePath $borderDownload -ArgumentList $borderArgs -WindowStyle Hidden -PassThru
$borderProcess.WaitForExit()
if ($borderProcess.ExitCode -ne 0) { throw 'Inno Setup compiler installation failed.' }
