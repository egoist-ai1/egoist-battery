# Egoist Battery

Приложение WPF на .NET 8. `src/EgoistBattery.Core` содержит модели и парсеры без UI.

Сборка: `dotnet build src/EgoistBattery/EgoistBattery.csproj -c Release`

Проверки: `dotnet run --project tests/EgoistBattery.Tests -c Release`

Аппаратные источники использовать только для чтения состояния. Для DualSense не переключать Bluetooth full-mode и не отправлять HID output/feature reports. Для Logitech HID++ разрешены только документированные GET запросы имени, доступных функций и батареи; SET, сопряжение и изменение настроек запрещены. Сценарий `scripts/DualSenseAudio.ps1` выполнять только по просьбе владельца.
