param([string]$Iscc)
$ErrorActionPreference = 'Stop'
$installRoot = Split-Path -Parent $PSScriptRoot
$product = Join-Path $installRoot 'source\Drawer\Drawer.csproj'
$payload = Join-Path $installRoot 'staging\win-x64'
$symbolRoot = Join-Path $installRoot 'symbols'
$vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -requires Microsoft.Component.MSBuild -property installationPath
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\amd64\MSBuild.exe'
if (Test-Path -LiteralPath $payload) {
    $resolvedPayload = (Resolve-Path -LiteralPath $payload).Path
    if (-not $resolvedPayload.StartsWith($installRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        ((Get-Item -LiteralPath $resolvedPayload).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Unsafe staging path'
    }
    Remove-Item -LiteralPath $resolvedPayload -Recurse -Force
}
& $msbuild $product /restore /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$payload\" /p:NuGetAudit=false /nologo /v:minimal /fl "/flp:logfile=$installRoot\build.log;verbosity=normal;encoding=UTF-8"
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed' }
Set-Content -LiteralPath (Join-Path $payload 'drawer-install-id.txt') -Value 'A3C6E7D8-2B10-4F64-91DD-5F21D9A06701' -Encoding ASCII
New-Item -ItemType Directory -Path $symbolRoot -Force | Out-Null
Get-ChildItem -LiteralPath $payload -Recurse -File -Filter '*.pdb' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $symbolRoot -Force
    Remove-Item -LiteralPath $_.FullName
}
$forbidden = Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object {
    $_.Extension -in '.lnk','.url','.pdb','.log','.mp4','.pfx','.snk','.pem','.key','.cs','.ps1' -or
    $_.Name -in 'collection.json','library.json','drawers.json','HistoryCleanup.json'
}
if ($forbidden) { throw 'Unexpected data or development files in payload' }
$manifest = Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{
        Path = [IO.Path]::GetRelativePath($payload, $_.FullName)
        Bytes = $_.Length
        SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
}
$manifest | Export-Csv -LiteralPath (Join-Path $installRoot 'payload-sha256.csv') -NoTypeInformation -Encoding UTF8
$assemblyFile = Get-ChildItem -LiteralPath (Join-Path $installRoot 'source\obj') -Recurse -Filter Drawer.AssemblyInfo.cs | Where-Object FullName -match '\\Release\\' | Select-Object -First 1
$metadata=Get-Content -LiteralPath $assemblyFile.FullName -Raw
if($metadata -match 'RecordingEdition'){throw 'Recording build metadata must not appear in installable app'}
$stamp=[regex]::Match($metadata,'BuildUtc", "([^"]+)"').Groups[1].Value
[ordered]@{ApplicationVersion='0.1.0';FileVersion='0.1.0.1';Build="Drawer-0.1-test-r1-$stamp";DllSHA256=(Get-FileHash -LiteralPath (Join-Path $payload 'Drawer.dll')).Hash;PayloadFiles=@($manifest).Count;PayloadBytes=($manifest|Measure-Object Bytes -Sum).Sum;RecordingEdition=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installRoot 'application-build-info.json') -Encoding UTF8