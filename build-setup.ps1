param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [Parameter(Mandatory=$true)][string]$Destination,
    [string]$WorkDirectory = ([IO.Path]::GetTempPath())
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceRoot=$PSScriptRoot
$packageRoot=[IO.Path]::GetFullPath($PackageDirectory)
$outputRoot=[IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$working=Join-Path ([IO.Path]::GetFullPath($WorkDirectory)) ('OwnDesk-setup-build-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $working | Out-Null
$payload=Join-Path $working 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($packageRoot,$payload,[IO.Compression.CompressionLevel]::Optimal,$false)
$hash=(Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
$trust=Join-Path $working 'SetupPayloadTrust.cs'
[IO.File]::WriteAllText($trust,('namespace OwnDesk { public static class SetupPayloadTrust { public const string Sha256 = "'+$hash+'"; } }'),(New-Object Text.UTF8Encoding($false)))
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }) + @((Join-Path $sourceRoot 'installer\InstallerMain.cs'),$trust)
$output=Join-Path $outputRoot '巫妖远控-安装版-0.2.0.exe'
$arguments=@('/nologo','/target:winexe','/main:OwnDesk.InstallerProgram','/platform:x64','/optimize+','/utf8output',('/out:'+$output),('/resource:'+$payload+',OwnDesk.Payload.zip'),('/win32manifest:'+(Join-Path $sourceRoot 'app.manifest')),('/win32icon:'+(Join-Path $sourceRoot 'assets\OwnDesk.ico')),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll','/reference:System.Net.Http.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
& $compiler @arguments @sources
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
Write-Output $output
