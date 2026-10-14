# Development download only. Automatic portable extraction failed its spike.
$ErrorActionPreference = 'Stop'
$scoutRoot = Split-Path -Parent $PSScriptRoot
$officeRoot = Join-Path $scoutRoot '.runtime/semantic-search/office'
New-Item -ItemType Directory -Path $officeRoot -Force | Out-Null
$package = Join-Path $officeRoot 'LibreOfficePortable_26.2.4_MultilingualStandard.paf.exe'
$uri = 'https://download.documentfoundation.org/libreoffice/portable/26.2.4/LibreOfficePortable_26.2.4_MultilingualStandard.paf.exe'
$expected = '4bde93374aef4409243505b20d16561a4628ac7591457dd01fc6e1ccf571ba65'
if (-not (Test-Path -LiteralPath $package)) {
    Invoke-WebRequest -Uri $uri -OutFile ($package + '.partial')
    $actual = (Get-FileHash -LiteralPath ($package + '.partial') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw 'Office package checksum mismatch.' }
    Move-Item -LiteralPath ($package + '.partial') -Destination $package
}
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'Office package checksum mismatch.'
}
Write-Output "Verified Office research package: $package"
Write-Output 'This script downloads only; automated per-user extraction remains unresolved.'
