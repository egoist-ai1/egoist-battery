[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Inspect', 'Disable', 'Restore')][string]$Action = 'Inspect',
    [string]$BackupPath
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$policyPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions'
$listPath = Join-Path $policyPath 'DenyDeviceIDs'
# Запрещён только USB-аудиоинтерфейс этой модели. Игровой MI_03 не затрагивается.
$audioHardwareId = 'USB\VID_054C&PID_0CE6&MI_00'
$audioDevices = @(Get-PnpDevice | Where-Object {
    ($_.Class -eq 'MEDIA' -and $_.InstanceId -match '^USB\\VID_054C&PID_0CE6&MI_00\\') -or
    ($_.Class -eq 'AudioEndpoint' -and $_.FriendlyName -match 'DualSense Wireless Controller')
})
function Get-AudioSnapshot {
    @(Get-PnpDevice | Where-Object {
        ($_.Class -eq 'MEDIA' -and $_.InstanceId -match '^USB\\VID_054C&PID_0CE6&MI_00\\') -or
        ($_.Class -eq 'AudioEndpoint' -and $_.FriendlyName -match 'DualSense Wireless Controller') -or
        ($_.InstanceId -match '^(USB|HID)\\VID_054C&PID_0CE6' -and $_.Class -ne 'MEDIA')
    } | ForEach-Object {
        $problem = Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction SilentlyContinue
        [pscustomobject]@{ Id = $_.InstanceId; Name = $_.FriendlyName; Class = $_.Class; Status = $_.Status; ProblemCode = $problem.Data }
    })
}
if ($Action -eq 'Inspect') { Get-AudioSnapshot | ConvertTo-Json -Depth 5; return }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Для отключения аудиоустройств Windows требуется запуск от администратора.'
}
if ($Action -eq 'Disable') {
    if (-not $PSCmdlet.ShouldProcess($audioHardwareId, 'Отключить микрофон и динамики DualSense, запретить повторную установку USB-аудиоинтерфейса')) { return }
    $taskStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $taskBackupDir = Join-Path $projectRoot "backups\dualsense-audio-$taskStamp"
    New-Item -ItemType Directory -Path $taskBackupDir -Force | Out-Null
    $prior = @{}
    foreach ($name in @('DenyDeviceIDs', 'DenyDeviceIDsRetroactive')) {
        $prior[$name] = Get-ItemPropertyValue -LiteralPath $policyPath -Name $name -ErrorAction SilentlyContinue
    }
    $existingList = @{}
    if (Test-Path -LiteralPath $listPath) {
        $taskKey = Get-Item -LiteralPath $listPath
        foreach ($name in $taskKey.GetValueNames()) { $existingList[$name] = $taskKey.GetValue($name) }
    }
    if ($existingList.Count -gt 0 -and $prior.DenyDeviceIDs -ne 1) {
        throw 'Уже имеется неактивный список запретов установки. Его активация затронула бы другие устройства.'
    }
    $before = Get-AudioSnapshot
    $entryName = $null
    if ($existingList.Values -notcontains $audioHardwareId) {
        $index = 1
        while ($existingList.ContainsKey([string]$index)) { $index++ }
        $entryName = [string]$index
    }
    $receipt = [ordered]@{ Created = [DateTimeOffset]::Now.ToString('o'); HardwareId = $audioHardwareId; PriorPolicy = $prior; AddedEntry = $entryName; Before = $before }
    $snapshotFile = Join-Path $taskBackupDir 'snapshot.json'
    $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $snapshotFile -Encoding utf8
    New-Item -Path $policyPath -Force | Out-Null
    New-Item -Path $listPath -Force | Out-Null
    if ($entryName) { New-ItemProperty -LiteralPath $listPath -Name $entryName -Value $audioHardwareId -PropertyType String -Force | Out-Null }
    New-ItemProperty -LiteralPath $policyPath -Name 'DenyDeviceIDs' -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $policyPath -Name 'DenyDeviceIDsRetroactive' -Value 1 -PropertyType DWord -Force | Out-Null
    $changes = @()
    foreach ($device in $audioDevices | Where-Object { $_.Class -eq 'AudioEndpoint' -and $_.Status -ne 'Unknown' }) {
        try {
            Disable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false -ErrorAction Stop
            $changes += [pscustomobject]@{ Id = $device.InstanceId; Disabled = $true; Error = $null }
        } catch {
            $changes += [pscustomobject]@{ Id = $device.InstanceId; Disabled = $false; Error = $_.Exception.Message }
        }
    }
    $after = Get-AudioSnapshot
    [ordered]@{ Backup = $snapshotFile; Changes = $changes; After = $after } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskBackupDir 'result.json') -Encoding utf8
    $bad = @($after | Where-Object { $_.Class -eq 'AudioEndpoint' -and $_.Status -eq 'OK' })
    $game = @($after | Where-Object { $_.Class -eq 'HIDClass' -and $_.Status -eq 'OK' })
    if ($bad.Count -gt 0 -or $game.Count -eq 0) { throw "Проверка после отключения не пройдена; см. $taskBackupDir" }
    [pscustomobject]@{ Success = $true; Backup = $snapshotFile; GameInterfaces = $game.Count; DisabledAudio = $audioDevices.Count } | ConvertTo-Json
    return
}
if (-not $BackupPath -or -not (Test-Path -LiteralPath $BackupPath)) { throw 'Укажите snapshot.json через -BackupPath.' }
$snapshot = Get-Content -LiteralPath $BackupPath -Raw -Encoding utf8 | ConvertFrom-Json
if ($snapshot.HardwareId -ne $audioHardwareId) { throw 'Резервная копия относится к другому устройству.' }
if (-not $PSCmdlet.ShouldProcess($audioHardwareId, 'Восстановить прежние настройки аудио DualSense')) { return }
if ($snapshot.AddedEntry) {
    $currentValue = Get-ItemPropertyValue -LiteralPath $listPath -Name $snapshot.AddedEntry -ErrorAction SilentlyContinue
    if ($currentValue -eq $audioHardwareId) { Remove-ItemProperty -LiteralPath $listPath -Name $snapshot.AddedEntry }
}
foreach ($name in @('DenyDeviceIDs', 'DenyDeviceIDsRetroactive')) {
    $oldValue = $snapshot.PriorPolicy.$name
    if ($null -ne $oldValue) { Set-ItemProperty -LiteralPath $policyPath -Name $name -Value ([int]$oldValue) }
    else { Remove-ItemProperty -LiteralPath $policyPath -Name $name -ErrorAction SilentlyContinue }
}
foreach ($device in $snapshot.Before | Where-Object { $_.Class -in @('MEDIA','AudioEndpoint') -and $_.ProblemCode -ne 22 }) {
    Enable-PnpDevice -InstanceId $device.Id -Confirm:$false -ErrorAction Continue
}
Get-AudioSnapshot | ConvertTo-Json -Depth 5
