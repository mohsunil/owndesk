param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$PackageUrl,
    [Parameter(Mandatory=$true)][string]$PrivateKeyPath,
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ($Version -notmatch '^\d{1,4}\.\d{1,4}\.\d{1,4}$') { throw 'Version must be major.minor.patch.' }
$uri=[Uri]$PackageUrl
if ($uri.Scheme -ne 'https' -or $uri.UserInfo.Length -gt 0) { throw 'PackageUrl must use HTTPS.' }
$packageRoot=[IO.Path]::GetFullPath($PackageDirectory)
$binary=Join-Path $packageRoot 'OwnDesk.exe'
if ((Get-Item -LiteralPath $binary).VersionInfo.FileVersion -ne ($Version+'.0')) { throw 'Built manager version differs from release version.' }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$zipPath=Join-Path $Destination ('OwnDesk-update-'+$Version+'.zip')
if (Test-Path -LiteralPath $zipPath) { throw 'Release archive already exists; choose a clean output directory.' }
$paths=@('OwnDesk.exe','OwnDesk.Update.exe','使用说明.html','开始使用.txt','THIRD-PARTY-NOTICES.txt','测试记录.json','文件校验.sha256','licenses/LICENSE-OwnDesk.txt','licenses/LICENSE-RustDesk-AGPL-3.0.txt','source/build.ps1','source/build-setup.ps1','source/app.manifest','source/SOURCE-README.txt','source/assets/OwnDesk.ico','source/assets/OwnDesk.png','source/tools/Publish-Update.ps1','source/installer/InstallerMain.cs')
$sourceFiles=Get-ChildItem -LiteralPath (Join-Path $packageRoot 'source\src') -Filter '*.cs'
foreach ($file in $sourceFiles) { $paths+='source/src/'+$file.Name }
$files=@()
$stream=[IO.File]::Open($zipPath,[IO.FileMode]::CreateNew)
$archive=New-Object IO.Compression.ZipArchive($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
try {
    foreach ($relative in ($paths | Sort-Object -Unique)) {
        $path=Join-Path $packageRoot $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $item=Get-Item -LiteralPath $path
        $hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $entry=$archive.CreateEntry($relative,[IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime=[DateTimeOffset]::Parse('2026-01-01T00:00:00Z')
        $input=[IO.File]::OpenRead($path);$output=$entry.Open()
        try { $input.CopyTo($output) } finally { $input.Dispose();$output.Dispose() }
        $files+=@{Path=$relative;Sha256=$hash;Size=$item.Length}
    }
} finally { $archive.Dispose();$stream.Dispose() }
$release=[ordered]@{Schema=1;Product='OwnDesk';Version=$Version;Architecture='x64';Channel='stable';PackageUrl=$PackageUrl;Sha256=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant();Size=(Get-Item -LiteralPath $zipPath).Length;Notes='单文件安装、连接码配对、签名更新与失败回退。跨电脑网页直接拖放仍待实现和实测。';Files=$files}
$payload=[Text.Encoding]::UTF8.GetBytes(($release | ConvertTo-Json -Depth 8 -Compress))
$entropy=[Text.Encoding]::UTF8.GetBytes('OwnDesk release signer v1')
$clear=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes([IO.Path]::GetFullPath($PrivateKeyPath)),$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
$rsa=New-Object Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp=$false
try {
    $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($clear))
    $signature=$rsa.SignData($payload,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
} finally { [Array]::Clear($clear,0,$clear.Length);$rsa.Dispose() }
$envelope=@{Schema=1;Payload=[Convert]::ToBase64String($payload);Signature=[Convert]::ToBase64String($signature)}
$utf8=New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $Destination 'latest.json'),($envelope | ConvertTo-Json -Compress),$utf8)
[IO.File]::WriteAllText((Join-Path $Destination 'release-details.json'),($release | ConvertTo-Json -Depth 8),$utf8)
Write-Output ('Prepared signed feed and package: '+$Version)
Write-Output $zipPath
