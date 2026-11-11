$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content "$root\Glance.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = "$root\publish"

foreach ($rid in 'x64', 'arm64') {
    $dir = if ($rid -eq 'x64') { "$out\portable" } else { "$out\portable-arm64" }
    dotnet publish "$root\Glance.csproj" -c Release -r "win-$rid" --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $dir
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }
    Copy-Item "$dir\Glance.exe" "$out\Glance-$version-$rid.exe" -Force
}

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $iscc)) { $iscc = $null }
}
if ($iscc) {
    & $iscc "/DAppVersion=$version" "/O$out" "$root\installer\Glance.iss"
    if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }
} else {
    Write-Warning 'ISCC.exe not found, skipping the installer'
}
