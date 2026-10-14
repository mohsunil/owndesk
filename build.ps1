param([string]$Destination = (Join-Path $PSScriptRoot 'build'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Requires Windows with .NET Framework 4.8 or later.' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$compileArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/utf8output','/main:OwnDesk.Program',('/out:' + (Join-Path $Destination 'OwnDesk.exe')),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Web.Extensions.dll','/reference:System.Security.dll','/reference:System.Net.Http.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
$icon = Join-Path $PSScriptRoot 'assets\OwnDesk.ico'
if (Test-Path -LiteralPath $icon) { $compileArgs += '/win32icon:' + $icon }
& $compiler @compileArgs @sources
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
$helperArgs = @($compileArgs | Where-Object { $_ -notmatch '^/main:' -and $_ -notmatch '^/out:' }) + @('/main:OwnDesk.UpdaterProgram',('/out:' + (Join-Path $Destination 'OwnDesk.Update.exe')))
& $compiler @helperArgs @sources
if ($LASTEXITCODE -ne 0) { throw 'Updater compilation failed.' }
Write-Output (Join-Path $Destination 'OwnDesk.exe')
