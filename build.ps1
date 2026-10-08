[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipInstaller,
    [string]$NsisPath
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$projectRoot = $PSScriptRoot
$toolchain = Get-Content -LiteralPath (Join-Path $projectRoot 'packaging/toolchain.json') -Raw | ConvertFrom-Json
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/EgoistBattery/EgoistBattery.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Версия приложения должна иметь вид x.y.z.' }
$releaseDirectory = Join-Path $projectRoot "artifacts/$version"
$packageDirectory = Join-Path $releaseDirectory 'package'
$runtimeVersion = [string]$toolchain.dotnetRuntime
New-Item -ItemType Directory -Path $releaseDirectory, $packageDirectory -Force | Out-Null
Push-Location -LiteralPath $projectRoot
try {
    # Один набор параметров восстановления и публикации сохраняет lock-файл.
    $publishProperties = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false', '-p:DebugType=embedded', "-p:EgoistRuntimeVersion=$runtimeVersion")
    dotnet restore 'src/EgoistBattery/EgoistBattery.csproj' -r win-x64 --locked-mode -p:SelfContained=true @publishProperties
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось восстановить зафиксированные зависимости.' }
    dotnet run --project 'tests/EgoistBattery.Tests' -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Проверки Core завершились ошибкой.' }
    dotnet publish 'src/EgoistBattery/EgoistBattery.csproj' -c $Configuration -r win-x64 --self-contained true --no-restore @publishProperties -o $releaseDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Публикация приложения завершилась ошибкой.' }
    $publishedExe = Join-Path $releaseDirectory 'EgoistBattery.exe'
    $metadata = (Get-Item -LiteralPath $publishedExe).VersionInfo
    if ($metadata.ProductName -ne 'Egoist Battery' -or $metadata.FileVersion -ne "$version.0") { throw 'Версия или имя опубликованного EXE не совпадают с проектом.' }
    Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $packageDirectory 'EgoistBattery.exe') -Force
    foreach ($file in @('LICENSE', 'packaging/README-install.md', 'packaging/NOTICE.txt', 'packaging/COMMUNITYTOOLKIT-LICENSE.txt', 'packaging/NSIS-LICENSE.txt')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $packageDirectory -Force
    }
    # PowerShell 5.1 в Windows требует BOM для кириллицы в сценарии установщика.
    [IO.File]::WriteAllText((Join-Path $packageDirectory 'InstallerLifecycle.ps1'),
        [IO.File]::ReadAllText((Join-Path $projectRoot 'packaging/InstallerLifecycle.ps1')), [Text.UTF8Encoding]::new($true))
    # Лицензии поставляем вместе со встроенным runtime.
    $nugetDirectory = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
    $runtimePackage = Join-Path $nugetDirectory "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
    $desktopPackage = Join-Path $nugetDirectory "microsoft.windowsdesktop.app.runtime.win-x64/$runtimeVersion"
    Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination (Join-Path $packageDirectory 'DOTNET-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $packageDirectory 'DOTNET-THIRD-PARTY-NOTICES.txt') -Force
    Copy-Item -LiteralPath (Join-Path $desktopPackage 'LICENSE') -Destination (Join-Path $packageDirectory 'WINDOWS-DESKTOP-LICENSE.txt') -Force
    $assets = [Collections.Generic.List[string]]::new()
    $assets.Add($publishedExe)
    if (-not $SkipInstaller) {
        & (Join-Path $projectRoot 'packaging/Generate-Assets.ps1')
        $compiler = & (Join-Path $projectRoot 'scripts/Get-Nsis.ps1') -CompilerPath $NsisPath
        $setupName = "EgoistBattery-$version-Setup-x64.exe"
        & $compiler '/V2' "/DPRODUCT_VERSION=$version" "/DSOURCE_DIR=$packageDirectory" "/DOUTPUT_FILE=$(Join-Path $releaseDirectory $setupName)" (Join-Path $projectRoot 'packaging/installer.nsi')
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось собрать установщик NSIS.' }
        $assets.Add((Join-Path $releaseDirectory $setupName))
    }
    $archive = Join-Path $releaseDirectory "EgoistBattery-$version-Portable-x64.zip"
    Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archive -CompressionLevel Optimal -Force
    $assets.Add($archive)
    $sourceCommit = (git rev-parse HEAD).Trim()
    $files = @($assets | ForEach-Object {
        [ordered]@{ Name = [IO.Path]::GetFileName($_); SizeBytes = (Get-Item -LiteralPath $_).Length; SHA256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    $manifest = [ordered]@{
        Product = 'Egoist Battery'; Version = $version; Platform = 'win-x64'; SourceCommit = $sourceCommit
        SdkVersion = (dotnet --version).Trim(); RuntimeVersion = $runtimeVersion
        NsisVersion = if ($SkipInstaller) { $null } else { [string]$toolchain.nsis.version }
        BuiltAtUtc = [DateTimeOffset]::UtcNow.ToString('o'); Signed = $false; Files = $files
    }
    $manifestPath = Join-Path $releaseDirectory 'MANIFEST.json'
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    $checksumLines = @($files | ForEach-Object { $_.SHA256 + '  ' + $_.Name })
    $checksumLines += (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() + '  MANIFEST.json'
    [IO.File]::WriteAllLines((Join-Path $releaseDirectory 'SHA256SUMS.txt'), $checksumLines, [Text.UTF8Encoding]::new($false))
    $manifest | ConvertTo-Json -Depth 6
}
finally { Pop-Location }
