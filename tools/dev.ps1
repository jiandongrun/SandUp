param(
    [ValidateSet('Restore','Build','Test','Run','Smoke','Publish')][string]$Action='Build',
    [string]$SdkPath
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$requiredSdk=(Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
if(-not $SdkPath){
    $candidates=@(
        (Get-Command dotnet -ErrorAction SilentlyContinue).Source,
        (Join-Path $env:USERPROFILE "AppData\Local\Temp\healthywork-dotnet-$requiredSdk\dotnet.exe"),
        (Join-Path $env:USERPROFILE 'AppData\Local\Temp\healthywork-dotnet\dotnet.exe')
    )
    foreach($candidate in $candidates){
        if($candidate -and (Test-Path -LiteralPath $candidate) -and ((& $candidate --list-sdks) -match "^$([regex]::Escape($requiredSdk)) ")){$SdkPath=$candidate;break}
    }
}
if(-not $SdkPath){throw "需要 .NET SDK $requiredSdk。安装后重试，或用 -SdkPath 指定该版本的 dotnet.exe。"}
$env:DOTNET_CLI_HOME=Join-Path $projectRoot 'tmp\dotnet-home'
$env:DOTNET_ROOT=Split-Path $SdkPath -Parent
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
switch($Action){
    'Restore' { & $SdkPath restore SandUp.sln --locked-mode -m:1 }
    'Build' { & $SdkPath build SandUp.sln -c Release --no-restore -m:1 /p:UseSharedCompilation=false }
    'Test' { & $SdkPath test SandUp.sln -c Release --no-restore -m:1 /p:UseSharedCompilation=false }
    'Run' { & $SdkPath 'src\SandUp.Desktop\bin\Release\net10.0-windows\SandUp.dll' }
    'Smoke' { & $SdkPath 'src\SandUp.Desktop\bin\Release\net10.0-windows\SandUp.dll' --smoke }
    'Publish' {
        $releasePath=Join-Path $projectRoot 'output\release\current'
        $stagingPath=Join-Path $projectRoot 'tmp\publish-current'
        # Only these two generated directories may be emptied. Refuse redirected paths.
        function Assert-PublishDirectory([string]$path,[string]$relative) {
            $expected=[IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
            if([IO.Path]::GetFullPath($path) -ne $expected){throw '发布目录超出预定位置。'}
            $cursor=$expected
            while($cursor -and $cursor -ne $projectRoot){
                if(Test-Path -LiteralPath $cursor){
                    if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "发布目录不能使用目录链接：$cursor"}
                }
                $cursor=Split-Path $cursor -Parent
            }
        }
        function Assert-ReleaseStopped {
            $running = Get-Process -Name 'SandUp','HealthyWork.Desktop' -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -and (Split-Path -LiteralPath $_.Path) -eq $releasePath }
            if ($running) { throw '请先从托盘退出 current 目录中的程序，再发布 SandUp · 沙立。' }
        }
        Assert-PublishDirectory $stagingPath 'tmp\publish-current'
        Assert-PublishDirectory $releasePath 'output\release\current'
        Assert-ReleaseStopped
        New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
        Get-ChildItem -LiteralPath $stagingPath -Force | Remove-Item -Recurse -Force
        # Build and collect notices before replacing the usable daily program.
        & $SdkPath publish src\SandUp.Desktop\SandUp.Desktop.csproj -c Release -r win-x64 --self-contained true -m:1 `
            /p:UseSharedCompilation=false /p:NuGetLockFilePath=obj/publish.packages.lock.json /p:RestoreLockedMode=false `
            /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true `
            /p:CopyOutputSymbolsToPublishDirectory=false -o $stagingPath
        if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
        Get-ChildItem -LiteralPath $stagingPath -File -Filter '*.pdb' | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\user-guide.md') -Destination (Join-Path $stagingPath '使用说明.md')
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\research\durability-evidence-20261119.md') -Destination (Join-Path $stagingPath '耐久度研究依据.md')
        Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\release-status.md') -Destination (Join-Path $stagingPath '测试版状态.md')
        $dependencyFile = Join-Path $projectRoot 'src\SandUp.Desktop\bin\Release\net10.0-windows\win-x64\SandUp.deps.json'
        & (Join-Path $PSScriptRoot 'package-notices.ps1') -ReleasePath $stagingPath -DependencyFile $dependencyFile
        Assert-ReleaseStopped
        New-Item -ItemType Directory -Path $releasePath -Force | Out-Null
        Get-ChildItem -LiteralPath $releasePath -Force | Remove-Item -Recurse -Force
        Get-ChildItem -LiteralPath $stagingPath -Force | Copy-Item -Destination $releasePath -Recurse -Force
        Assert-PublishDirectory $stagingPath 'tmp\publish-current'
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
        Set-Content -LiteralPath (Join-Path $projectRoot 'output\release\latest-release.txt') -Value $releasePath
        Write-Output "测试包目录：$releasePath"
    }
}
exit $LASTEXITCODE
