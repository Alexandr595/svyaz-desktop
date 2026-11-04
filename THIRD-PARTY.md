# Сторонние компоненты

## Установщик и .NET

- Установщик собирается официальным NSIS 3.13, исходный архив: https://sourceforge.net/projects/nsis/files/NSIS%203/3.13/nsis-3.13.zip/download . SHA-256: `BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8`.
- NSIS используется только при сборке, его компилятор не устанавливается пользователю. Условия: https://nsis.sourceforge.io/License .
- Self-contained установка включает Microsoft .NET 8 для Windows x64. Комплектные `LICENSE.txt` и `ThirdPartyNotices.txt` сохраняются в payload. Условия и исходники: https://github.com/dotnet/runtime и https://github.com/dotnet/wpf .
- Иконка и установочные изображения созданы в проекте; мастер-файл `assets/svyaz.svg`, генератор `tools/Svyaz.AssetBuilder`.

## zapret2 1.0.6

- Автор: bol-van и участники проекта.
- Исходный код и релиз: https://github.com/bol-van/zapret2/releases/tag/v1.0.6
- Официальный архив: https://github.com/bol-van/zapret2/releases/download/v1.0.6/zapret2-v1.0.6.zip
- SHA-256 полученного архива: `E141B51B60C895FE9548F4D644365461DCAD8155E068D284A9AAE1724504237D`.
- Комплект Windows: `engine/zapret2`. Бинарные файлы, Lua и образцы TLS/STUN взяты без изменений; файлы лицензий сохранены рядом.
- Лицензия проекта: MIT, текст в `engine/zapret2/LICENSE-zapret2.txt`. WinDivert: LGPLv3, текст в `engine/licenses/LICENSE-windivert.txt`. Cygwin API library: LGPLv3 или новее с исключением для связывания, условия и исходники: https://cygwin.com/licensing.html и https://cygwin.com/git.html .
- Приложение проверяет SHA-256 восьми файлов перед запуском. Применяемые стратегии определены в `ZapretEngine.cs`; чужие установочные скрипты и службы не запускаются.

## GoodbyeDPI 0.2.3rc3

- Автор: ValdikSS и участники проекта.
- Исходный код: https://github.com/ValdikSS/GoodbyeDPI/tree/0.2.3rc3
- Официальный архив: https://github.com/ValdikSS/GoodbyeDPI/releases/download/0.2.3rc3/goodbyedpi-0.2.3rc3-2.zip
- SHA-256 полученного архива: `37F96B32D050DADCC930A639EBA68E1CCD57ED5C04A5F77DFCA908F01905A4C5`.
- Лицензия: Apache-2.0, полный текст `engine/licenses/LICENSE-goodbyedpi.txt`.
- Файлы x86_64 используются без изменений. Скрипты установки службы и общие списки блокировок в приложение не включены.

## WinDivert и встроенные зависимости

- WinDivert: https://github.com/basil00/WinDivert; лицензия из оригинального дистрибутива сохранена в `engine/licenses/LICENSE-windivert.txt`.
- getline: `engine/licenses/LICENSE-getline.txt`.
- uthash: `engine/licenses/LICENSE-uthash.txt`.

Установщик, переподписание и модификация драйвера не выполнялись. WinDivert загружается выбранным движком только при включении обработки с правами администратора. Проверка SHA-256 подтверждает совпадение с включённой в проект копией; она не является независимой проверкой безопасности или подписи автора.
