param(
    [Parameter(Mandatory = $true)][string]$RuntimeIdentifier,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\bundled\7zip')
)

$ErrorActionPreference = 'Stop'
$version = '26.02'
$baseUrl = "https://github.com/ip7z/7zip/releases/download/$version"
$assets = @{
    'win-x64'      = @('7z2602-extra.7z', '081df9e9311dfd9c9e0e98c1c80180b99bb51e4cb24156b5f3057fe3c259d70a', 'x64/7za.exe', '35d4d69d7cd6cb44558f208c3b1334268013f9daf82d2dda848893a1c30c59c2', '7za.exe')
    'win-arm64'    = @('7z2602-extra.7z', '081df9e9311dfd9c9e0e98c1c80180b99bb51e4cb24156b5f3057fe3c259d70a', 'arm64/7za.exe', 'cadbd34657713935222eb14fddbcdd51953501b44c749d9a029fab8f1c46be7e', '7za.exe')
    'linux-x64'    = @('7z2602-linux-x64.tar.xz', '41aaba7b1235304ab5aa0624530c67ae829496cd29e875925271efdccc28c03e', '7zz', '1676a968815b92e865bc0ffeecee3fa284ba4402bf23dc2bec2412c4b502e922', '7zz')
    'linux-arm64'  = @('7z2602-linux-arm64.tar.xz', '70ea6cc737ae1495ea2d7eb20ef3120fe579bd3f1a83a9d2362b62ec5bde2bba', '7zz', '41ca798f0c0652c435cbdd9c3ba49d703c9410c597f40a5cd336304b3964c674', '7zz')
    'osx-x64'      = @('7z2602-mac.tar.xz', '1cf6760579502f87e591ff5c73a005ec50b3e4d6f507e8b038382d563c3175b9', '7zz', '9c56cf3379a0d8544e9244958b96fdc7c17f9ce70f5a160eb2b41f5f3df96d8c', '7zz')
    'osx-arm64'    = @('7z2602-mac.tar.xz', '1cf6760579502f87e591ff5c73a005ec50b3e4d6f507e8b038382d563c3175b9', '7zz', '9c56cf3379a0d8544e9244958b96fdc7c17f9ce70f5a160eb2b41f5f3df96d8c', '7zz')
}
if (-not $assets.ContainsKey($RuntimeIdentifier)) { throw "Unsupported 7-Zip RID: $RuntimeIdentifier" }
$asset = $assets[$RuntimeIdentifier]
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('stowcrate-7zip-' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $OutputRoot $RuntimeIdentifier
New-Item -ItemType Directory -Path $temporary,$target -Force | Out-Null
try {
    $package = Join-Path $temporary $asset[0]
    Invoke-WebRequest "$baseUrl/$($asset[0])" -OutFile $package
    if ((Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset[1]) { throw '7-Zip package integrity mismatch.' }
    $expanded = Join-Path $temporary 'expanded'; New-Item -ItemType Directory $expanded | Out-Null
    if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal)) {
        $bootstrap = Join-Path $temporary '7zr.exe'; Invoke-WebRequest "$baseUrl/7zr.exe" -OutFile $bootstrap
        if ((Get-FileHash $bootstrap -Algorithm SHA256).Hash.ToLowerInvariant() -ne '56b8cc9f4971cef253644fafe54063ed7fdca551d4dee0f8c6baa81b855acd72') { throw '7zr bootstrap integrity mismatch.' }
        & $bootstrap x $package ("-o$expanded") -y | Out-Null
    } else { & tar -xf $package -C $expanded }
    $source = Join-Path $expanded $asset[2]; $destination = Join-Path $target $asset[4]
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $license = Get-ChildItem -LiteralPath $expanded -Recurse -File | Where-Object { $_.Name -ieq 'License.txt' } | Select-Object -First 1
    if ($null -eq $license) { throw 'Pinned 7-Zip package did not contain License.txt.' }
    Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $target 'License.txt') -Force
    if ((Get-FileHash $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset[3]) { throw '7-Zip executable integrity mismatch.' }
    if (-not $RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal)) { & chmod +x $destination }
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
