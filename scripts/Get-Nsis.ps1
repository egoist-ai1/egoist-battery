[CmdletBinding()]
param([string]$CompilerPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$settings = (Get-Content -LiteralPath (Join-Path $projectRoot 'packaging/toolchain.json') -Raw | ConvertFrom-Json).nsis
$version = [string]$settings.version
$candidates = @()
if ($CompilerPath) { $candidates += $CompilerPath }
$resolvedCommand = Get-Command makensis.exe -ErrorAction SilentlyContinue
if ($resolvedCommand) { $candidates += $resolvedCommand.Source }
$candidates += Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'NSIS/makensis.exe'
$candidates += Join-Path $env:ProgramFiles 'NSIS/makensis.exe'
$toolDirectory = Join-Path $projectRoot "artifacts/toolchain/nsis-$version"
$candidates += Join-Path $toolDirectory "nsis-$version/makensis.exe"
foreach ($candidate in $candidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
    $actualVersion = (& $candidate '/VERSION' | Out-String).Trim()
    if ($LASTEXITCODE -eq 0 -and $actualVersion -eq "v$version") { return [IO.Path]::GetFullPath($candidate) }
    if ($CompilerPath -and $candidate -eq $CompilerPath) { throw "Нужен NSIS $version; указан другой компилятор." }
}
New-Item -ItemType Directory -Path $toolDirectory -Force | Out-Null
$archive = Join-Path $toolDirectory "nsis-$version.zip"
if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $settings.sha256) {
    Invoke-WebRequest -Uri $settings.url -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $settings.sha256) {
        # SourceForge иногда возвращает HTML со временной ссылкой на тот же архив.
        $html = [IO.File]::ReadAllText($archive)
        $pattern = [regex]::Escape([string]$settings.url) + '\?ts=[^"<>\s]+'
        $downloadUrl = [Net.WebUtility]::HtmlDecode([regex]::Match($html, $pattern).Value)
        if (-not $downloadUrl -or ([Uri]$downloadUrl).Host -ne 'downloads.sourceforge.net') { throw 'SourceForge не вернул ожидаемый архив NSIS.' }
        Invoke-WebRequest -Uri $downloadUrl -OutFile $archive
    }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $settings.sha256) { throw 'SHA256 архива NSIS не совпадает. Выполнение остановлено.' }
Expand-Archive -LiteralPath $archive -DestinationPath $toolDirectory -Force
$compiler = Join-Path $toolDirectory "nsis-$version/makensis.exe"
if ((& $compiler '/VERSION' | Out-String).Trim() -ne "v$version") { throw 'Версия распакованного NSIS не совпадает.' }
return $compiler
