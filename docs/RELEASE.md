# Выпуск и проверка

## Воспроизводимая среда

Сборка закрепляет .NET SDK 8.0.425 через `global.json`, runtime 8.0.31 и NuGet locked-mode. Для установщика используется NSIS 3.13. Если его нет, сценарий берёт переносимый набор инструментов; его SHA256 закреплена в `packaging/toolchain.json`, системная установка не нужна.

```powershell
pwsh -NoProfile -File ./build.ps1
```

Команда собирает приложение, выполняет 70 тестов, публикует одиночный EXE, формирует установщик и portable-архив и записывает `SHA256SUMS.txt` в `artifacts/1.0.0`. Для EXE и архива без установщика:

```powershell
pwsh -NoProfile -File ./build.ps1 -SkipInstaller
```

Изолированная проверка установщика:

```powershell
pwsh -NoProfile -File ./scripts/Test-Installer.ps1 -Installer ./artifacts/1.0.0/EgoistBattery-1.0.0-Setup-x64.exe
```

UI self-test запускайте с отдельной временной папкой данных:

```powershell
EgoistBattery.exe --ui-test --data-dir <временная-папка>
```

## Состав выпуска

Для выпуска `1.0.0` ожидаются `EgoistBattery-1.0.0-Setup-x64.exe`, `EgoistBattery-1.0.0-Portable-x64.zip`, автономный `EgoistBattery.exe`, `SHA256SUMS.txt` и `MANIFEST.json`. Публикуйте подготовленные и проверенные файлы вместе с тегом `v1.0.0`. CI не публикует выпуск автоматически.

Для GitHub CLI подготовьте проверенные файлы и выполните:

```powershell
gh release create v1.0.0 ./artifacts/1.0.0/EgoistBattery-1.0.0-Setup-x64.exe ./artifacts/1.0.0/EgoistBattery-1.0.0-Portable-x64.zip ./artifacts/1.0.0/EgoistBattery.exe ./artifacts/1.0.0/SHA256SUMS.txt ./artifacts/1.0.0/MANIFEST.json
```

Установщик не подписан и не выполняет автоматические обновления. Для проверки скачанного файла сравните его хеш с соответствующей записью в `SHA256SUMS.txt`. После установки сверяйте `%LOCALAPPDATA%\Programs\EgoistBattery\EgoistBattery.exe` с опубликованным EXE: получите хеш командой `Get-FileHash` и сравните с соответствующей строкой `SHA256SUMS.txt`. `SHA256SUMS.txt` подтверждает совпадение файла с опубликованной суммой, но сам по себе не удостоверяет автора.

Закреплённый SDK и набор инструментов делают конфигурацию сборки повторяемой, но не гарантируют побайтно одинаковый артефакт при разных путях, исходниках или ревизиях Git. Публичные подтверждения выпуска и снимки экрана следует хранить в `evidence/`; личные отчёты и резервные копии в публичные материалы не включайте.

Сценарий `scripts/Install.ps1` — локальный прежний установщик с резервными копиями. Пользователям рекомендуется Setup из выпуска. Необязательный `scripts/DualSenseAudio.ps1` является отдельной ручной утилитой владельца; инсталлятор приложения не меняет аудиоустройства или драйверы.