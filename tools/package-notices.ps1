param(
    [Parameter(Mandatory=$true)][string]$ReleasePath,
    [string]$DependencyFile
)
$ErrorActionPreference='Stop'
$releaseRoot=(Resolve-Path -LiteralPath $ReleasePath).Path
$projectRoot=Split-Path $PSScriptRoot -Parent
$noticeSource=Join-Path $PSScriptRoot 'third-party-notices'
if (-not $DependencyFile) { $DependencyFile=Join-Path $releaseRoot 'SandUp.deps.json' }
$dependencies=Get-Content -LiteralPath $dependencyFile -Raw | ConvertFrom-Json
$assetFile=Join-Path $projectRoot 'src\SandUp.Desktop\obj\project.assets.json'
$assets=Get-Content -LiteralPath $assetFile -Raw | ConvertFrom-Json
$packageRoots=@($assets.packageFolders.PSObject.Properties.Name)
$inventory=@()
$noticeText=[System.Text.StringBuilder]::new()
$supplements=[System.Collections.Generic.HashSet[string]]::new()

function Add-Notice([string]$title,[string]$path) {
    $null=$noticeText.AppendLine().AppendLine("===== $title =====")
    $null=$noticeText.Append([IO.File]::ReadAllText($path)).AppendLine()
}

Add-Notice 'SandUp - MIT license' (Join-Path $projectRoot 'LICENSE')
foreach($entry in $dependencies.libraries.PSObject.Properties){
    if($entry.Value.type -eq 'project'){continue}
    $parts=$entry.Name -split '/',2
    $packageId=$parts[0] -replace '^runtimepack\.',''
    $version=$parts[1]
    $relative=Join-Path $packageId.ToLowerInvariant() $version
    $packagePath=$null
    foreach($root in $packageRoots){
        $candidate=Join-Path $root $relative
        if(Test-Path -LiteralPath $candidate){$packagePath=$candidate;break}
    }
    if(-not $packagePath){throw "未找到已发布依赖的包元数据：$($entry.Name)"}
    $metadata=Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' | Select-Object -First 1
    if(-not $metadata){throw "缺少依赖元数据：$($entry.Name)"}
    [xml]$xml=Get-Content -LiteralPath $metadata.FullName -Raw
    $packageName="$packageId-$version"
    foreach($file in Get-ChildItem -LiteralPath $packagePath -File | Sort-Object Name){
        if($file.Name -match 'license|notice|copyright'){
            Add-Notice "$packageName / $($file.Name)" $file.FullName
        }
    }
    switch -Wildcard ($packageId) {
        'Microsoft.Data.Sqlite.Core' { $null=$supplements.Add($packageName) }
        'Microsoft.WindowsDesktop.App.Runtime.win-x64' { $null=$supplements.Add($packageName) }
        'SQLitePCLRaw.*' { $null=$supplements.Add("SQLitePCLRaw-$version") }
    }
    $inventory += [ordered]@{
        name=$packageId; version=$version; type=$entry.Value.type
        license=[string]$xml.package.metadata.license.InnerText
        licenseUrl=[string]$xml.package.metadata.licenseUrl
        copyright=[string]$xml.package.metadata.copyright
        repository=[string]$xml.package.metadata.repository.url
    }
}
foreach($name in $supplements | Sort-Object) {
    $originals=Get-ChildItem -LiteralPath (Join-Path $noticeSource $name) -File | Sort-Object Name
    if(-not $originals){throw "缺少依赖的上游许可声明：$name"}
    foreach($file in $originals) {
        Add-Notice "$name / $($file.Name)" $file.FullName
    }
}
$fonts=Join-Path $projectRoot 'src\SandUp.Desktop\Assets\Fonts'
Add-Notice 'Noto Sans SC / NOTICE.txt' (Join-Path $fonts 'NOTICE.txt')
Add-Notice 'Noto Sans SC / OFL.txt' (Join-Path $fonts 'OFL.txt')
$inventory += [ordered]@{
    name='Noto Sans SC'; version='2.004'; type='font'
    license='OFL-1.1'; licenseUrl='https://openfontlicense.org/open-font-license-official-text/'
    copyright='© 2014-2021 Adobe (http://www.adobe.com/).'
    repository='https://github.com/notofonts/noto-cjk/tree/Sans2.004/Sans/SubsetOTF/SC'
}
$noticePath=Join-Path $releaseRoot 'SandUp-Licenses.txt'
$intro="SandUp licenses and third-party notices`r`n`r`n===== Published dependency inventory =====`r`n"
$manifest=$inventory | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($noticePath, $intro + $manifest + "`r`n" + $noticeText.ToString(), [Text.UTF8Encoding]::new($true))
Write-Output "依赖清单已生成：$($inventory.Count) 项"
