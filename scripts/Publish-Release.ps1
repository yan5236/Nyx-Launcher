<#
.SYNOPSIS
验证发布版本及公共微软配置，发布 Windows x64 自包含单 EXE，并生成 SHA256SUMS。
.PARAMETER Version
三段 SemVer，可含 v 前缀及预发布标识；与 Tag 和程序集版本保持一致。
.PARAMETER OutputDirectory
输出根目录，默认 artifacts；版本子目录必须为空，避免混入旧文件或覆盖既有发布。
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$Version = $Version -creplace '^[vV]', ''
if ($Version.Length -gt 128 -or $Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$') {
    throw '发布版本须为 v主版本.次版本.修订号，可附带预发布标识。'
}
foreach ($number in $Matches[1], $Matches[2], $Matches[3]) {
    if ([long]$number -gt 65534) { throw '版本分量不能超过 65534。' }
}
if ($Version.Contains('-')) {
    foreach ($part in $Version.Split('-', 2)[1].Split('.')) {
        if ($part -match '^0[0-9]+$') { throw '预发布数字标识不能含前导零。' }
    }
}
$config = Join-Path $root 'config/microsoft-auth.release.json'
$auth = Get-Content -LiteralPath $config -Raw -Encoding utf8 | ConvertFrom-Json
$clientId = [Guid]::Empty
$redirect = $null
if (-not [Guid]::TryParse($auth.ClientId, [ref]$clientId) -or $clientId -eq [Guid]::Empty -or
    $auth.ClientId -eq 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' -or
    -not [Uri]::TryCreate($auth.RedirectUri, [UriKind]::Absolute, [ref]$redirect) -or
    $redirect.Scheme -ne 'http' -or $redirect.Host -ne 'localhost' -or $redirect.Port -le 0 -or
    $redirect.UserInfo -or $redirect.Query -or $redirect.Fragment -or
    @($auth.PSObject.Properties.Name | Where-Object { $_ -notin 'ClientId', 'RedirectUri' }).Count) {
    throw '发布配置只允许有效的公共 ClientId 和 localhost RedirectUri，不接受示例 ID 或额外字段。'
}
$directory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $Version
if ((Test-Path -LiteralPath $directory) -and (Get-ChildItem -LiteralPath $directory -Force)) {
    throw "发布目录非空，不能覆盖：$directory"
}
$publish = Join-Path $directory 'publish'
$dist = Join-Path $directory 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
dotnet publish (Join-Path $root 'src/NyxLauncher/NyxLauncher.csproj') -c Release -p:Platform=x64 -p:PublishProfile=SingleFile "-p:Version=$Version" "-p:MicrosoftAuthConfig=$config" "-p:IntermediateOutputPath=$directory/obj/" "-p:OutputPath=$directory/build/" -o $publish
if ($LASTEXITCODE -ne 0) { throw '单文件发布失败。' }
# 原生包附带的 PDB 仅供调试，分发目录只复制唯一运行产物，保留构建目录中的符号便于排障。
$files = @(Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object Extension -ne '.pdb')
if ($files.Count -ne 1 -or $files[0].Name -ne 'NyxLauncher.exe') {
    throw '发布结果不是单 EXE，停止生成发布产物；请检查 WinUI 运行时和内容打包配置。'
}
$asset = Join-Path $dist 'NyxLauncher.exe'
Copy-Item -LiteralPath $files[0].FullName -Destination $asset
$hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS'), "$hash  NyxLauncher.exe`n", [Text.UTF8Encoding]::new($false))
Write-Output "发布完成：$dist"
