[CmdletBinding()]
param([Parameter(Mandatory)][string]$Installer, [string]$Output)
$ErrorActionPreference = 'Stop'
$Installer = (Resolve-Path -LiteralPath $Installer).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$testRoot = Join-Path $tempRoot ('EgoistBattery-InstallerCheck-' + [Guid]::NewGuid().ToString('N'))
# Пробелы, скобки и кириллица проверяют реальные границы аргументов.
$installDirectory = Join-Path $testRoot 'Установка [test] с пробелами'
$dataDirectory = Join-Path $testRoot 'data'
$liveExe = Join-Path $env:LOCALAPPDATA 'Programs/EgoistBattery/EgoistBattery.exe'
$liveIds = @(Get-Process -Name EgoistBattery -ErrorAction SilentlyContinue | Where-Object Path -EQ $liveExe | Select-Object -ExpandProperty Id)
$runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
$startupBefore = if ($runKey) { $runKey.GetValue('EgoistBattery', $null) } else { $null }
if ($runKey) { $runKey.Dispose() }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
try {
    # NSIS /D= — последний аргумент; пробелы в нём не заключаются в кавычки.
    $setup = Start-Process -FilePath $Installer -ArgumentList @('/S', '/TESTMODE', "/D=$installDirectory") -WindowStyle Hidden -PassThru
    if (-not $setup.WaitForExit(60000) -or $setup.ExitCode -ne 0) { throw 'Изолированная установка не прошла.' }
    $testExe = Join-Path $installDirectory 'EgoistBattery.exe'
    $uninstaller = Join-Path $installDirectory 'Uninstall.exe'
    if (-not (Test-Path -LiteralPath $testExe) -or -not (Test-Path -LiteralPath $uninstaller)) { throw 'Не созданы EXE и программа удаления.' }
    $exeHash = (Get-FileHash -LiteralPath $testExe -Algorithm SHA256).Hash
    $probe = Start-Process -FilePath $testExe -ArgumentList @('--probe', '--data-dir', ('"' + $dataDirectory + '"')) -WindowStyle Hidden -PassThru
    if (-not $probe.WaitForExit(30000) -or $probe.ExitCode -ne 0) { throw 'Установленный EXE не прошёл запуск.' }
    if (-not (Test-Path -LiteralPath (Join-Path $dataDirectory 'check-result.json'))) { throw 'Не получено подтверждение чтения устройств.' }
    $keptFile = Join-Path $installDirectory 'keep [user].txt'
    Set-Content -LiteralPath $keptFile -Value 'Сохранить посторонний файл.' -Encoding utf8
    $remove = Start-Process -FilePath $uninstaller -ArgumentList '/S' -WindowStyle Hidden -PassThru
    if (-not $remove.WaitForExit(30000) -or $remove.ExitCode -ne 0) { throw 'Удаление завершилось ошибкой.' }
    $deadline = [DateTimeOffset]::Now.AddSeconds(30)
    while ((Test-Path -LiteralPath $testExe) -and [DateTimeOffset]::Now -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (Test-Path -LiteralPath $testExe) { throw 'EXE остался после удаления.' }
    if (-not (Test-Path -LiteralPath $keptFile)) { throw 'Удаление затронуло посторонний файл.' }
    $currentIds = @(Get-Process -Name EgoistBattery -ErrorAction SilentlyContinue | Where-Object Path -EQ $liveExe | Select-Object -ExpandProperty Id)
    if ((($liveIds | Sort-Object) -join ',') -ne (($currentIds | Sort-Object) -join ',')) { throw 'Проверка затронула работающий фоновый монитор.' }
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    $startupAfter = if ($runKey) { $runKey.GetValue('EgoistBattery', $null) } else { $null }
    if ($runKey) { $runKey.Dispose() }
    if ($startupBefore -ne $startupAfter) { throw 'Проверка изменила действующий автозапуск.' }
    $receipt = [ordered]@{ Success = $true; Install = $true; PublishedExeLaunch = $true; Uninstall = $true
        UnrelatedFileRetained = $true; LiveMonitorRetained = $true; StartupRetained = $true; SHA256 = $exeHash
        FinishedUtc = [DateTimeOffset]::UtcNow.ToString('o') }
    if ($Output) { $receipt | ConvertTo-Json | Set-Content -LiteralPath $Output -Encoding utf8 }
    $receipt | ConvertTo-Json
}
finally {
    $target = [IO.Path]::GetFullPath($testRoot)
    if (-not $target.StartsWith($tempRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $target) -notlike 'EgoistBattery-InstallerCheck-*') { throw 'Отказ очистки: папка вне временного каталога.' }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}
