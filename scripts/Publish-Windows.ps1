[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string] $Version,

    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$zipName = "kPOS-$Version-win-x64.zip"
$checksumName = "$zipName.sha256"
$destination = New-Item -ItemType Directory -Force -Path $OutputDirectory
foreach ($name in $zipName, $checksumName) {
    if (Test-Path -LiteralPath (Join-Path $destination.FullName $name)) {
        throw "Release asset already exists: $name"
    }
}

$temp = Get-Item -LiteralPath ([IO.Path]::GetTempPath())
$staging = New-Item -ItemType Directory -Path (Join-Path $temp.FullName "kpos-publish-$([guid]::NewGuid().ToString('N'))")
try {
    $app = Join-Path $PSScriptRoot '../kPOS.csproj'
    $publish = Join-Path $staging.FullName 'publish'
    dotnet publish $app --configuration Release --runtime win-x64 --self-contained --output $publish -warnaserror -p:Version=$Version -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $archive = Join-Path $staging.FullName $zipName
    Compress-Archive -LiteralPath (Join-Path $publish 'kPOS.exe') -DestinationPath $archive
    $extracted = Join-Path $staging.FullName 'extracted'
    Expand-Archive -LiteralPath $archive -DestinationPath $extracted
    $executable = Join-Path $extracted 'kPOS.exe'
    (Get-Item -LiteralPath $executable).IsReadOnly = $true
    & (Join-Path $PSScriptRoot 'Test-WindowsExecutable.ps1') -Executable $executable
    # Use a lower-version copy of the same build to exercise Windows executable replacement.
    if ([version]($Version.Split('-')[0]) -gt [version]'0.0.0') {
        $previous = Join-Path $staging.FullName 'previous'
        dotnet publish $app --no-restore --configuration Release --runtime win-x64 --self-contained --output $previous -warnaserror -p:Version=0.0.0 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) {
            throw "Building the upgrade test fixture failed with exit code $LASTEXITCODE."
        }
        & (Join-Path $PSScriptRoot 'Test-WindowsExecutable.ps1') -Executable $executable -PreviousExecutable (Join-Path $previous 'kPOS.exe')
    }

    $checksum = Join-Path $staging.FullName $checksumName
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $checksum -Encoding ascii -Value "$hash  $zipName"
    Move-Item -LiteralPath $archive, $checksum -Destination $destination.FullName
    Write-Output "Built and verified $zipName"
}
finally {
    $stageDirectory = Get-Item -LiteralPath $staging.FullName -Force
    if ($stageDirectory.Parent.FullName.TrimEnd([char[]] '\/') -ne $temp.FullName.TrimEnd([char[]] '\/') -or
        ($stageDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Unexpected package staging path: $($stageDirectory.FullName)"
    }
    Remove-Item -LiteralPath $stageDirectory.FullName -Recurse -Force
}
