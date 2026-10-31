=====================================================================
        ULTIMATE OPTIMIZER v3.1 — оптимизация уровня AtlasOS
            для любого ПК/ноутбука на Windows 10 и 11
=====================================================================

ЧТО ЭТО
  Твики взяты из официального плейбука AtlasOS v0.5.0 (только те,
  что не могут ничего сломать) + игровые настройки + профиль NVIDIA
  + автоматическая установка нужных программ.
  Переустанавливать Windows НЕ нужно.

ГЛАВНОЕ: НИЧЕГО НЕ ЛОМАЕТ
  Перед каждым изменением скрипт записывает старое значение
  (реестр, службы, задачи, питание, сетевая карта, приложения)
  в C:\ProgramData\UltimateOptimizer\backup.json.
  2-UNDO-Everything.bat возвращает ВСЁ ровно как было.
  Плюс создаётся обычная точка восстановления Windows.


---------------------------------------------------------------------
 СОСТАВ
---------------------------------------------------------------------
  UltimateOptimizer.exe     всё в одном: меню со всеми пунктами ниже
  1-OPTIMIZE.bat            запуск оптимизации
  2-UNDO-Everything.bat     точный откат всего
  3-Repair-Windows.bat      починка Windows (DISM, SFC, сеть, апдейты)
  4-PC-Info-Report.bat      отчёт о железе на почту / в Telegram
  5-Get-OptimizerDuck.bat   скачать и запустить optimizerDuck

  Ultimate-Optimizer.ps1    сам оптимизатор
  Win11-Repair.ps1          починка
  PC-Info-Mail.ps1          отчёт о компе
  Get-OptimizerDuck.ps1     загрузчик optimizerDuck
  NVIDIA-MaxPerformance.nip   профиль NVIDIA (скрипт ставит его сам)
  NVIDIA-Driver-Defaults.nip  откат профиля NVIDIA к заводскому


---------------------------------------------------------------------
 КАК ЗАПУСТИТЬ
---------------------------------------------------------------------
  Проще всего: запусти UltimateOptimizer.exe → «Да» в окне UAC → выбери пункт.

  Или через .bat:
  1. Распакуй архив в папку (например C:\Ultimate). Из архива не запускай.
  2. Двойной клик по 1-OPTIMIZE.bat → «Да» в окне UAC.
     SmartScreen ругается → «Подробнее» → «Выполнить в любом случае».
  3. Ответь на вопросы (Enter = безопасный вариант «нет»).
  4. Дождись «DONE» → перезагрузка.

  Вручную через PowerShell (от администратора):
     cd C:\Ultimate
     Set-ExecutionPolicy -Scope Process Bypass -Force
     .\Ultimate-Optimizer.ps1            с вопросами
     .\Ultimate-Optimizer.ps1 -Yes       без вопросов (рискованное НЕ трогает)
     .\Ultimate-Optimizer.ps1 -NoApps    без скачивания программ
     .\Ultimate-Optimizer.ps1 -NoDuck    без скачивания optimizerDuck
     .\Ultimate-Optimizer.ps1 -Undo      откатить всё

  Можно запускать повторно сколько угодно — бэкап хранит самые
  первые (заводские) значения и не перезаписывается.


---------------------------------------------------------------------
 ЧТО ДЕЛАЕТ СКРИПТ
---------------------------------------------------------------------
  Сам подстраивается под железо:
   - ноутбук: на зарядке всё на максимум, на батарее — экономнее,
     гибернация остаётся (чтобы не терять данные при разрядке)
   - HDD: SysMain не трогает (на HDD он ускоряет), индексацию
     поиска переводит в ручной режим на слабых ПК
   - мало ОЗУ: выключает прозрачность
   - NVIDIA / AMD / Intel: свои настройки под каждую

  ПИТАНИЕ (из Atlas): свой план «Ultimate Optimizer», без парковки
   ядер, агрессивный турбобуст, без засыпания USB/NVMe/PCIe,
   энергосбережение сетевой карты выключено, быстрый запуск выключен.

  ПРОЦЕССОР И ЗАДЕРЖКИ: Win32PrioritySeparation = 38 (Atlas),
   MMCSS = 10 (Atlas), приоритет игр, dynamic tick выключен,
   FTH выключен (Atlas), группировка svchost (Atlas: меньше ОЗУ
   и процессов), NTFS без записи времени доступа.

  ИГРЫ: HAGS, игровой режим, Game DVR выключен, MSI-режим
   прерываний для видеокарты, у AMD отключён ULPS.

  ИНТЕРФЕЙС (Atlas): быстрые визуальные эффекты, меню без задержек,
   без акселерации мыши, без фоновых приложений.

  ТЕЛЕМЕТРИЯ (Atlas): DiagTrack, отчёты об ошибках, CEIP, PCA,
   реклама, Copilot, Recall, виджеты, задачи сбора данных.

  ПРИЛОЖЕНИЯ: удаляет мусор из списка Atlas (Teams, Clipchamp,
   Bing-приложения, Solitaire, TikTok, Candy Crush...).
   ОСТАВЛЯЕТ: Store, Xbox / Game Bar, Фото, Калькулятор, Камеру,
   Ножницы, Блокнот, Записки. Удалённое возвращается откатом.

  ЧТО ИЗ ATLAS СОЗНАТЕЛЬНО НЕ ВЗЯТО (ломает на ноутах / в сети):
   - отключение устройств (SMBus, ME и т.п.) — на многих ноутах
     через SMBus работает тачпад
   - отключение NetBT и общего доступа к файлам — ломает сеть
   - отключение всех процессных защит (CFG) — ломает античиты
     вроде Vanguard (Valorant)
   - отключение автообновлений Windows — это дыра в безопасности

  СПРОСИТ ОТДЕЛЬНО (по умолчанию НЕТ):
   - выключить VBS / Memory Integrity: +5–15% FPS, но перестанут
     работать WSL2, Docker, Hyper-V
   - выключить защиту от Spectre/Meltdown: прирост в основном на
     процессорах до 2019 года

  ПРОГРАММЫ (через winget, сам):
   - Visual C++ 2015+ x64/x86 и DirectX — фиксят ошибки «нет DLL» в играх
   - HWiNFO, Process Lasso, Autoruns, CrystalDiskInfo, 7-Zip
   - MSI Afterburner (если есть видеокарта NVIDIA/AMD)
   - G-Helper (ASUS) / Legion Toolkit (Lenovo)
   - NVIDIA Profile Inspector → в C:\Tools\nvidiaProfileInspector
   - optimizerDuck (последний релиз с GitHub, x64/ARM64, проверка
     SHA-256) → в C:\Tools\optimizerDuck. Только скачивается —
     запускай сам, когда захочешь (или 5-Get-OptimizerDuck.bat).
   Если winget нет (старая Win10): поставь «Установщик приложений»
   (App Installer) из Microsoft Store и запусти скрипт снова.


---------------------------------------------------------------------
 ПРОФИЛЬ NVIDIA (ставится сам, ID сверены с заголовком NVIDIA NVAPI)
---------------------------------------------------------------------
  Power management mode ........ Prefer maximum performance
  Low Latency Mode ............. On (1 заранее готовый кадр)
  Texture filtering - Quality .. High performance
  Anisotropic sample optim. .... On
  Anisotropic filter optim. .... On
  Vertical Sync ................ Force off
  Preferred refresh rate ....... Highest available
  Shader Cache ................. On, размер без ограничения
  Ansel ........................ Off

  Перед установкой скрипт сохраняет твои текущие настройки
  (файл .nip рядом с nvidiaProfileInspector.exe).
  Поставить вручную: открой nvidiaProfileInspector.exe → кнопка
  Import → NVIDIA-MaxPerformance.nip → Apply changes.
  Вернуть заводские: так же импортируй NVIDIA-Driver-Defaults.nip.
  Если нужен V-Sync (например, есть G-Sync), включи его в игре
  или в панели NVIDIA.


---------------------------------------------------------------------
 ЕСЛИ ЧТО-ТО НЕ ТАК
---------------------------------------------------------------------
  - Что-то работает не так → 2-UNDO-Everything.bat → перезагрузка.
  - Windows не грузится → при загрузке 3 раза подряд выключи ПК
    кнопкой → «Дополнительные параметры» → «Восстановление системы»
    → точка «Before Ultimate-Optimizer».
  - Глючит Windows сама по себе → 3-Repair-Windows.bat, пункт 3.
  - Нет интернета → 3-Repair-Windows.bat, пункт 4.
  - Лог каждого запуска: C:\ProgramData\UltimateOptimizer\log_*.txt


---------------------------------------------------------------------
 OPTIMIZERDUCK (5-Get-OptimizerDuck.bat)
---------------------------------------------------------------------
  Отдельная open-source программа (GPL-3.0, автор itsfatduck) с
  графическим интерфейсом. Скрипт всегда качает свежую версию с
  официальной страницы релизов и сверяет контрольную сумму.
  Ключи: -NoLaunch (только скачать), -Force (скачать заново).
  Не применяй одни и те же твики двумя программами одновременно —
  откат Ultimate Optimizer вернёт только свои изменения.


---------------------------------------------------------------------
 ОТЧЁТ О КОМПЕ (4-PC-Info-Report.bat)
---------------------------------------------------------------------
  При первом запуске спросит твой Gmail (отправитель) и куда слать
  отчёт — запомнит. Сменить: -ResetPassword.
  Gmail: включи двухэтапную аутентификацию и создай пароль приложения
  на https://myaccount.google.com/apppasswords.
  Через VPN почта не уйдёт, тогда отправляй в Telegram:
     .\PC-Info-Mail.ps1 -Method Telegram
  @BotFather → /newbot → токен → вставь в скрипт → напиши боту.
  Ключи: -NoMail (только открыть отчёт), -To адрес, -ResetPassword,
         -ResetTelegram


=====================================================================
 ЧТО ЕЩЁ ДАЁТ ПРИРОСТ (больше любого софта)
=====================================================================
  1. SSD вместо HDD — старый ПК оживает в разы.
  2. ОЗУ: минимум 8 ГБ, лучше 16. Две одинаковые планки (двухканал).
  3. Чистка от пыли + новая термопаста — убирает троттлинг.
  4. Ноутбук: играть на зарядке, режим Performance/Turbo в фирменной
     утилите, подставка с обдувом.
  5. Андервольт видеокарты в MSI Afterburner (Ctrl+F → точка 0.850 V
     на ~1750 МГц → всё правее выровнять → Apply).


=====================================================================
 ДРУГИЕ ПРОГРАММЫ (не запускай всё подряд — хватит этого пака)
=====================================================================
  AtlasOS (полная установка) ..... https://atlasos.net
  optimizerDuck .................. https://github.com/itsfatduck/optimizerDuck/releases
  Chris Titus WinUtil ............ https://github.com/ChrisTitusTech/winutil
  Win11Debloat ................... https://github.com/Raphire/Win11Debloat
  O&O ShutUp10++ ................. https://www.oo-software.com/en/shutup10
  NVIDIA Profile Inspector ....... https://github.com/Orbmu2k/nvidiaProfileInspector/releases
  NVCleanstall ................... https://www.techpowerup.com/nvcleanstall/
  DDU (удаление драйверов) ....... https://www.wagnardsoft.com
  MSI Afterburner ................ https://www.msi.com/Landing/afterburner/graphics-cards
  ThrottleStop ................... https://www.techpowerup.com/download/techpowerup-throttlestop/
  Process Lasso .................. https://bitsum.com
  HWiNFO ......................... https://www.hwinfo.com/download/
  LatencyMon ..................... https://www.resplendence.com/latencymon
  Autoruns ....................... https://learn.microsoft.com/sysinternals/downloads/autoruns
  CPU-Z .......................... https://www.cpuid.com/softwares/cpu-z.html
  GPU-Z .......................... https://www.techpowerup.com/gpuz/
  CrystalDiskInfo ................ https://crystalmark.info/en/software/crystaldiskinfo/
  G-Helper (ASUS) ................ https://github.com/seerge/g-helper/releases
  Legion Toolkit (Lenovo) ........ https://github.com/BartoszCichecki/LenovoLegionToolkit/releases
=====================================================================
