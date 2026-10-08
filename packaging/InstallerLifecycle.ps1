[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallDirectory,
    [switch]$TestMode
)
$ErrorActionPreference = 'Stop'
try {
    if ($TestMode) { exit 0 }
    $exe = Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) 'EgoistBattery.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { exit 0 }
    if ((Get-Item -LiteralPath $exe).VersionInfo.ProductName -ne 'Egoist Battery') { throw 'В папке находится другая программа.' }
    $processes = @(Get-Process -Name EgoistBattery -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    if ($processes.Count -eq 0) { exit 0 }
    $stopRequest = Start-Process -FilePath $exe -ArgumentList '--exit' -WindowStyle Hidden -PassThru
    if (-not $stopRequest.WaitForExit(10000) -or $stopRequest.ExitCode -ne 0) { throw 'Команда выхода не завершилась.' }
    foreach ($process in $processes) {
        if (-not $process.WaitForExit(15000)) { throw 'Программа не завершилась; установка остановлена.' }
    }
    exit 0
}
catch { Write-Error $_; exit 1 }
