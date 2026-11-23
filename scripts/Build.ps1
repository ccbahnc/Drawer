param([Parameter(Mandatory=$true)][string]$Iscc)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot

$base=$root
$payload=Join-Path $base 'staging\win-x64'

& (Join-Path $PSScriptRoot 'BuildApplication.ps1')
$app=Get-Content -LiteralPath (Join-Path $root 'application-build-info.json') -Raw | ConvertFrom-Json
$expected=$app.DllSHA256
if((Get-FileHash -LiteralPath (Join-Path $payload 'Drawer.dll')).Hash -ne $expected){throw 'Current application payload changed after compilation'}
$manifest=@(Import-Csv -LiteralPath (Join-Path $base 'payload-sha256.csv'))
foreach($file in $manifest){if((Get-FileHash -LiteralPath (Join-Path $payload $file.Path)).Hash -ne $file.SHA256){throw "Changed payload: $($file.Path)"}}
$lines=@('procedure InitializePayloadFiles();','begin','  PayloadFiles := TStringList.Create;','  PayloadFiles.Sorted := True;')
foreach($file in $manifest){$value=($file.Path.Replace('/','\').ToLowerInvariant()+'|'+$file.SHA256.ToLowerInvariant()).Replace("'","''");$lines+="  PayloadFiles.Add('$value');"}
$lines+='end;'
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'PayloadManifest.iss'),($lines -join "`r`n"),[Text.UTF8Encoding]::new($true))
$vswhere=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs=& $vswhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(-not $vs){throw 'MSVC x64 tools are required to build the directory helper'}
New-Item -ItemType Directory -Path (Join-Path $root 'helper\build') -Force | Out-Null
$native=@"
@echo off
call "$vs\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul
if errorlevel 1 exit /b 1
cd /d "$root\helper"
rc /nologo /fo build\FolderPermission.res FolderPermission.rc
if errorlevel 1 exit /b 1
cl /nologo /std:c++17 /EHsc /O2 /MT /W4 /WX /DUNICODE /D_UNICODE FolderPermission.cpp /Fo:build\FolderPermission.obj /Fe:build\DrawerFolderPermission.exe build\FolderPermission.res /link /SUBSYSTEM:WINDOWS /ENTRY:wmainCRTStartup advapi32.lib shell32.lib ole32.lib uuid.lib
exit /b %errorlevel%
"@
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'CompileHelper.cmd'),$native,[Text.ASCIIEncoding]::new())
& (Join-Path $PSScriptRoot 'CompileHelper.cmd') *> (Join-Path $root 'helper-build.log')
if($LASTEXITCODE -ne 0){throw 'Native helper compilation failed; see helper-build.log'}
& $Iscc "/DPayload=$payload" (Join-Path $PSScriptRoot 'Drawer.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
$package=Join-Path $root 'output\Drawer-0.2-win11-x64-test-r1-setup.exe'
$hash=(Get-FileHash -LiteralPath $package).Hash
Set-Content -LiteralPath (Join-Path $root 'package-sha256.txt') -Value "$hash  Drawer-0.2-win11-x64-test-r1-setup.exe" -Encoding UTF8
[ordered]@{
    InstallerRevision='0.2-test-r1'; InstallerFileVersion='0.2.0.1'; Publisher='XGaming'
    InstallerBuildUtc=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'); InstallerSHA256=$hash
    InstallerBytes=(Get-Item -LiteralPath $package).Length
    ApplicationBuild=$app.Build; ApplicationRecompiled=$true
    ApplicationDllSHA256=$expected; ApplicationPayloadFiles=$manifest.Count
    HelperSHA256=(Get-FileHash -LiteralPath (Join-Path $root 'helper\build\DrawerFolderPermission.exe')).Hash
    HelperUsesStaticCRT=$true; InstalledOrUITested=$false; ActualUACGrantedDuringAgentChecks=$false
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'build-info.json') -Encoding UTF8
if(Test-Path -LiteralPath (Join-Path $root '测试说明.txt')){Copy-Item -LiteralPath (Join-Path $root '测试说明.txt') -Destination (Join-Path $root 'output\测试说明.txt') -Force}
Copy-Item -LiteralPath (Join-Path $root 'package-sha256.txt') -Destination (Join-Path $root 'output\package-sha256.txt') -Force
Write-Output "Installer SHA256 $hash"
