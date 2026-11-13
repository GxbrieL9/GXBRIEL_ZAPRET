# Сторонние компоненты

GXBRIEL_ZAPRET использует отдельные компоненты с собственными лицензиями. Авторские уведомления сохранены в `third_party/licenses` и во встроенном архиве уведомлений приложения.

| Компонент | Назначение | Лицензия и исходники |
| --- | --- | --- |
| zapret2 1.0.5.2 | Движок обхода DPI, сценарии blockcheck2 | MIT; https://github.com/bol-van/zapret2/tree/v1.0.5.2 |
| WinDivert 2.2.2 | Перехват сетевых пакетов | LGPL-3.0; https://github.com/basil00/WinDivert/tree/v2.2.2 |
| Cygwin 3.4.10 из выпуска движка | Среда исходного движка | GPL с исключениями Cygwin; https://cygwin.com/licensing.html |
| Cygwin и пакеты для blockcheck2 | Bash, curl и служебные утилиты | Лицензии соответствующих пакетов; точные версии и SHA-512 — в `third_party/cygwin-packages.json` |
| .NET Runtime | Запуск самостоятельной сборки | MIT и уведомления .NET; https://github.com/dotnet/runtime |
| Hanken Grotesk, Space Grotesk, JetBrains Mono | Шрифты интерфейса | SIL Open Font License 1.1; https://github.com/google/fonts |
| Корневые сертификаты Mozilla/curl | Проверка HTTPS в curl | Сведения и исходные сертификаты: https://curl.se/docs/caextract.html |

**Соответствующие исходники:** архив `third-party-sources.zip` опубликован в [выпуске зависимостей](https://github.com/GxbrieL9/GXBRIEL_ZAPRET/releases/tag/dependencies-2026-11) и прилагается к версиям приложения. Он содержит исходные архивы пакетов Cygwin, исходники Cygwin 3.4.10, WinDivert 2.2.2 и zapret2 1.0.5.2, лицензии, подписанные сведения о пакетах и их контрольные суммы. Архивы включают инструкции и сценарии сборки соответствующих проектов. Сетевые бинарные файлы WinDivert/winws не модифицируются.

Пакеты Cygwin для blockcheck2 загружаются по подписанному официальному списку; подпись списка и контрольные суммы всех архивов проверяются. Пакеты и соответствующие исходники сохраняются вместе.

Sysinternals TCPView/tcpvcon не распространяются в составе приложения. Если пользователь самостоятельно установил совместимую утилиту, старый формат её вывода поддерживается; штатный путь захвата работает через Windows netstat.
