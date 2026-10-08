[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Install', 'RestoreStartup')][string]$Action = 'Install',
    [string]$SourceExe,
    [string]$BackupPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\EgoistBattery'
$installedExe = Join-Path $installDirectory 'EgoistBattery.exe'
$dataDirectory = Join-Path $env:LOCALAPPDATA 'EgoistBattery'
$haloExe = Join-Path $env:LOCALAPPDATA 'Programs\HaloBattery\HaloBattery.exe'
$startupValue = '"' + $installedExe + '" --tray'

function Get-ExactProcess([string]$Executable) {
    @(Get-Process -Name 'EgoistBattery', 'HaloBattery' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Executable })
}
function Stop-OwnInstance {
    if ((Get-ExactProcess $installedExe).Count -eq 0) { return }
    $taskStop = Start-Process -FilePath $installedExe -ArgumentList '--exit' -WindowStyle Hidden -PassThru
    if (-not $taskStop.WaitForExit(10000)) { throw 'Команда выхода не завершилась.' }
    foreach ($taskProcess in Get-ExactProcess $installedExe) {
        if (-not $taskProcess.WaitForExit(10000)) { throw 'Приложение не завершилось. Файлы не заменены.' }
    }
}
function Set-PreviousValue($RegistryKey, [string]$Name, $Value) {
    if ($null -eq $Value) { $RegistryKey.DeleteValue($Name, $false) }
    else { $RegistryKey.SetValue($Name, [string]$Value, [Microsoft.Win32.RegistryValueKind]::String) }
}

if (-not $PSCmdlet.ShouldProcess($installedExe, $Action)) { return }
$runKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
try {
    if ($Action -eq 'RestoreStartup') {
        if (-not $BackupPath -or -not (Test-Path -LiteralPath $BackupPath -PathType Leaf)) { throw 'Укажите backup.json через -BackupPath.' }
        $taskBackup = Get-Content -LiteralPath $BackupPath -Raw -Encoding utf8 | ConvertFrom-Json
        if ($taskBackup.InstalledExe -ne $installedExe -or $taskBackup.StartupValue -ne $startupValue) { throw 'Резервная копия относится к другой установке.' }
        if ($runKey.GetValue('EgoistBattery', $null) -ne $startupValue) { throw 'Автозапуск изменён после установки. Автоматическое восстановление остановлено.' }
        $taskHaloCurrent = $runKey.GetValue('HaloBattery', $null)
        if ($null -ne $taskHaloCurrent -and $taskHaloCurrent -ne $taskBackup.HaloStartup) { throw 'Автозапуск Halo изменён после установки.' }
        Stop-OwnInstance
        Set-PreviousValue $runKey 'EgoistBattery' $taskBackup.EgoistStartup
        Set-PreviousValue $runKey 'HaloBattery' $taskBackup.HaloStartup
        if ($taskBackup.HaloWasRunning -and (Test-Path -LiteralPath $haloExe) -and (Get-ExactProcess $haloExe).Count -eq 0) {
            Start-Process -FilePath $haloExe -WindowStyle Hidden | Out-Null
        }
        [pscustomobject]@{ Success = $true; Action = $Action; FilesRetained = $true } | ConvertTo-Json
        return
    }

    if (-not $SourceExe) { $SourceExe = Join-Path $projectRoot 'artifacts\1.0.0\EgoistBattery.exe' }
    $SourceExe = [IO.Path]::GetFullPath($SourceExe)
    if (-not (Test-Path -LiteralPath $SourceExe -PathType Leaf)) { throw 'Готовая сборка EXE не найдена.' }
    $taskOldHaloStartup = $runKey.GetValue('HaloBattery', $null)
    if ($null -ne $taskOldHaloStartup -and $taskOldHaloStartup -notin @($haloExe, ('"' + $haloExe + '"'))) {
        throw 'Неожиданная команда автозапуска Halo. Другие команды не изменены.'
    }
    $taskHaloProcesses = Get-ExactProcess $haloExe
    $taskStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $taskBackupDirectory = Join-Path $projectRoot "backups\installation-$taskStamp"
    New-Item -ItemType Directory -Path $taskBackupDirectory -Force | Out-Null
    $taskBackupFile = Join-Path $taskBackupDirectory 'backup.json'
    [ordered]@{
        Created = [DateTimeOffset]::Now.ToString('o'); InstalledExe = $installedExe; StartupValue = $startupValue
        EgoistStartup = $runKey.GetValue('EgoistBattery', $null); HaloStartup = $taskOldHaloStartup
        HaloWasRunning = $taskHaloProcesses.Count -gt 0
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskBackupFile -Encoding utf8
    Stop-OwnInstance
    if (Test-Path -LiteralPath $installedExe) { Copy-Item -LiteralPath $installedExe -Destination (Join-Path $taskBackupDirectory 'EgoistBattery.previous.exe') }
    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    Copy-Item -LiteralPath $SourceExe -Destination $installedExe -Force
    $taskHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
    if ($taskHash -ne (Get-FileHash -LiteralPath $SourceExe -Algorithm SHA256).Hash) { throw 'Проверка копии EXE не пройдена.' }

    $taskStartedAt = [DateTimeOffset]::Now
    $taskStarted = Start-Process -FilePath $installedExe -ArgumentList '--tray' -WindowStyle Hidden -PassThru
    $taskReady = $false
    $taskStatePath = Join-Path $dataDirectory 'tray-state.json'
    $taskDeadline = [DateTimeOffset]::Now.AddSeconds(20)
    while ([DateTimeOffset]::Now -lt $taskDeadline) {
        if ($taskStarted.HasExited) { throw 'Приложение завершилось при запуске.' }
        if (Test-Path -LiteralPath $taskStatePath) {
            try {
                $taskState = Get-Content -LiteralPath $taskStatePath -Raw -Encoding utf8 | ConvertFrom-Json
                $taskStateStamp = if ($taskState.UpdatedAt -is [DateTime]) { [DateTimeOffset]$taskState.UpdatedAt }
                    else { [DateTimeOffset]::Parse($taskState.UpdatedAt, [Globalization.CultureInfo]::InvariantCulture) }
                if ($taskStateStamp -ge $taskStartedAt) { $taskReady = $true; break }
            } catch [IO.IOException] { }
        }
        Start-Sleep -Milliseconds 200
    }
    if (-not $taskReady) { throw 'Не подтверждено создание значков в трее. Автозапуск не изменён.' }
    # Старое приложение и его данные сохраняем, отключаем только его запуск.
    foreach ($taskProcess in $taskHaloProcesses) {
        $taskProcess.Refresh()
        if (-not $taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id }
    }
    $runKey.SetValue('EgoistBattery', $startupValue, [Microsoft.Win32.RegistryValueKind]::String)
    if ($null -ne $taskOldHaloStartup) { $runKey.DeleteValue('HaloBattery', $false) }
    $taskShell = New-Object -ComObject WScript.Shell
    try {
        foreach ($taskFolder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
            $taskShortcut = $taskShell.CreateShortcut((Join-Path $taskFolder 'Egoist Battery.lnk'))
            $taskShortcut.TargetPath = $installedExe; $taskShortcut.WorkingDirectory = $installDirectory
            $taskShortcut.Description = 'Заряд подключённых устройств'; $taskShortcut.IconLocation = $installedExe + ',0'; $taskShortcut.Save()
            [Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShortcut) | Out-Null
        }
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($taskShell) | Out-Null }
    $taskReceipt = [ordered]@{
        Success = $true; InstalledAt = [DateTimeOffset]::Now.ToString('o'); InstalledExe = $installedExe
        SHA256 = $taskHash; ProcessId = $taskStarted.Id; StartupValue = $startupValue; Backup = $taskBackupFile
        Tray = $taskState
    }
    $taskReceipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskBackupDirectory 'receipt.json') -Encoding utf8
    $taskReceipt | ConvertTo-Json -Depth 6
} finally { $runKey.Dispose() }
