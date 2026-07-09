@echo off
chcp 65001 >nul
rem ============================================================
rem  Установка плагина ClashIdFixer для Navisworks Manage 2022.
rem  Положите этот файл рядом с папкой ClashIdFixer (содержимое
rem  bin\Release) и запустите ОТ ИМЕНИ АДМИНИСТРАТОРА.
rem ============================================================

set "DEST=C:\Program Files\Autodesk\Navisworks Manage 2022\Plugins\ClashIdFixer"

net session >nul 2>&1
if errorlevel 1 (
    echo Нужны права администратора: щёлкните файл правой кнопкой -^> "Запуск от имени администратора".
    pause
    exit /b 1
)

if not exist "%~dp0ClashIdFixer\ClashIdFixer.dll" (
    echo Рядом со скриптом не найдена папка ClashIdFixer с ClashIdFixer.dll.
    echo Ожидаемая структура: install.cmd + папка ClashIdFixer\ .
    pause
    exit /b 1
)

if not exist "C:\Program Files\Autodesk\Navisworks Manage 2022\" (
    echo Не найден Navisworks Manage 2022 в стандартной папке установки.
    echo Поправьте переменную DEST в этом скрипте под свой путь.
    pause
    exit /b 1
)

xcopy /Y /E /I "%~dp0ClashIdFixer" "%DEST%" >nul
if errorlevel 1 (
    echo Ошибка копирования в "%DEST%".
    pause
    exit /b 1
)

echo Установлено: %DEST%
echo Перезапустите Navisworks - появится вкладка "BIM УП".
pause
