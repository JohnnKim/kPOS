#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Executable,
    [string] $ReportPath = (Join-Path ([IO.Path]::GetTempPath()) 'kpos-windows10-verification.json')
)

$ErrorActionPreference = 'Stop'
$os = Get-CimInstance Win32_OperatingSystem
if ($os.ProductType -ne 1 -or [int]$os.BuildNumber -lt 17763 -or [int]$os.BuildNumber -ge 22000) {
    throw 'Run this acceptance check on a Windows 10 PC, version 1809 or later. A Windows 11 or Server run cannot verify Windows 10 compatibility.'
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'This release requires 64-bit Windows.'
}
$exe = (Resolve-Path -LiteralPath $Executable).ProviderPath
$report = [ordered]@{
    checkedAt = [DateTimeOffset]::UtcNow.ToString('O')
    os = $os.Caption
    build = $os.BuildNumber
    executable = $exe
    sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    result = 'Failed'
}
try {
    & (Join-Path $PSScriptRoot 'Test-WindowsExecutable.ps1') -Executable $exe
    $report.result = 'Passed'
}
finally {
    $report | ConvertTo-Json | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}
Write-Output "Windows 10 packaged-app verification passed. Report: $ReportPath"
