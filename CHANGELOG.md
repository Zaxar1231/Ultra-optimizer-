# Changelog

## v3.1.0

### Новое
- **`UltimateOptimizer.exe`** — один файл со всеми скриптами внутри и меню: оптимизация, откат, починка, отчёт о ПК, optimizerDuck. Сам запрашивает права администратора.
- **optimizerDuck** — `Get-OptimizerDuck.ps1` / `5-Get-OptimizerDuck.bat` качают последний релиз [itsfatduck/optimizerDuck](https://github.com/itsfatduck/optimizerDuck) под x64 или ARM64 и сверяют SHA-256. Основной оптимизатор тоже скачивает его в `C:\Tools\optimizerDuck` (отключается ключом `-NoDuck`).
- Сборка и релизы через GitHub Actions, проверка скриптов PSScriptAnalyzer.

### Исправлено
- `Ultimate-Optimizer.ps1`: winget-код «уже установлено» (`0x8A150061`) больше не считается ошибкой.
- `Ultimate-Optimizer.ps1`: если не удалось прочитать `disablelastaccess`, откат больше не вызывает `fsutil` с пустым значением.
- `Ultimate-Optimizer.ps1`: понятное сообщение для Windows 7/8.1 и предупреждение для 32-битной системы.
- `PC-Info-Mail.ps1`: убран зашитый e-mail автора — адреса спрашиваются при первом запуске и запоминаются локально.
- `PC-Info-Mail.ps1`: Secure Boot на компьютерах с Legacy BIOS показывается как `n/a` вместо ошибки.
- `Win11-Repair.ps1`: при перерегистрации приложений пропускаются пакеты без манифеста; `chkdsk /scan` без лишнего `echo Y`; в подсказке восстановления указана точка «Before Ultimate-Optimizer».

## v3.0.0
- Первая версия: твики из плейбука AtlasOS v0.5.0, точный бэкап и откат, профиль NVIDIA, установка программ через winget, починка Windows, отчёт о ПК.
