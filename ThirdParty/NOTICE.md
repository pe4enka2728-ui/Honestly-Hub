# Компоненты комплекта

Honestly TUN управляет следующими неизменёнными официальными компонентами как отдельными процессами. Названия и лицензии их авторов сохранены.

| Компонент | Версия | Назначение | Источник |
|---|---|---|---|
| Xray-core | v26.3.27, Windows x64 | Протоколы серверов подписки | [Официальный релиз](https://github.com/XTLS/Xray-core/releases/tag/v26.3.27) |
| sing-box | v1.14.3, Windows amd64 | Системный TUN, DNS, локальная статистика | [Официальный релиз](https://github.com/SagerNet/sing-box/releases/tag/v1.14.3) |
| Wintun | 0.14.1, amd64 | Подписанная библиотека TUN Windows | [Официальный SDK](https://www.wintun.net/) |

Лицензия Xray MPL-2.0 — `Runtime\xray\LICENSE`. Лицензия sing-box — `Runtime\sing-box\LICENSE`, полный текст GPL-3.0 — `ThirdParty\GPL-3.0.txt`. Лицензия готовых бинарных файлов Wintun — `LICENSE-wintun.txt` в каталогах Runtime. Комплектный libcronet.dll сохранён из официального архива sing-box.

Исходники точных версий Xray и sing-box включены в эту папку как `xray-26.3.27-source.tar.gz` и `sing-box-1.14.3-source.tar.gz`. Исходные репозитории: [XTLS/Xray-core](https://github.com/XTLS/Xray-core), [SagerNet/sing-box](https://github.com/SagerNet/sing-box). Инструкции сборки и списки зависимостей находятся в соответствующих архивах (`go.mod`, `go.sum` и инструкции проекта).

Архивы ядер получены с официальных GitHub Releases и сверены с SHA256, опубликованными в метаданных релиза:

- Xray-windows-64.zip: `d004c39288ce9ada487c6f398c7c545f7d749e44bdfdd59dbc9f865afba4e1ad`
- sing-box-1.14.3-windows-amd64.zip: `d8b86ef58936dba9230ec6a57ffa4243933f3c14ef4809828b6c553440ed1518`

Хеши извлечённых EXE/DLL перечислены в `Source\CoreHashes.json` и встроены в сборку для проверки файлов перед запуском. Скачанные ядра не выполнялись при подготовке комплекта.

Документация используемых интерфейсов: [TUN sing-box](https://sing-box.sagernet.org/configuration/inbound/tun/), [SOCKS sing-box](https://sing-box.sagernet.org/configuration/outbound/socks/), [Clash API](https://sing-box.sagernet.org/configuration/experimental/clash-api/), [протоколы Xray](https://xtls.github.io/en/config/outbounds/).

## Flag icons

Country flag PNGs are from Twemoji v16.0.1, copyright Twitter, Inc. and other contributors.
Source: https://github.com/jdecked/twemoji/tree/v16.0.1/assets/72x72
Graphics license: Creative Commons Attribution 4.0 International (CC-BY-4.0), https://creativecommons.org/licenses/by/4.0/.
The complete license is in LICENSE-Twemoji-Graphics.txt. PNG files are included unmodified; the application scales them when rendering.

## Музыкальная панель 2.3

- NAudio.Wasapi и NAudio.Core 2.2.1, Mark Heath, MIT. Используются для WASAPI loopback и FFT. Полная лицензия — LICENSE-NAudio.txt. Пакеты: https://www.nuget.org/packages/NAudio.Wasapi/2.2.1 и https://www.nuget.org/packages/NAudio.Core/2.2.1. Исходники: https://github.com/naudio/NAudio/tree/v2.2.1.
- Microsoft.Windows.SDK.NET.dll и WinRT.Runtime.dll получены из пакета Microsoft.Windows.SDK.NET.Ref 10.0.19041.57. Проекции Windows SDK и C#/WinRT обеспечивают доступ к системному медиасеансу Spotify. Лицензия Windows SDK из https://aka.ms/WinSDKLicenseURL включена без изменения как LICENSE-Windows-SDK.rtf. MIT-лицензия C#/WinRT — LICENSE-CsWinRT.txt; источник https://github.com/microsoft/CsWinRT.
- API Windows: https://learn.microsoft.com/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager и https://learn.microsoft.com/windows/win32/coreaudio/loopback-recording.

Обложка трека поступает от локального медиасеанса Spotify во время работы. Обложки и музыкальные записи в комплект не включены.

## Spotify integration 2.4

Spotify Web API accessed using the .NET HTTP client and OAuth PKCE. No Spotify SDK or UI Automation interop package is distributed. Spotify is a trademark of Spotify AB. Account credentials, tokens, artwork and audio are not bundled.


## ThatPet

The ThatPet software and artwork retain their original authorship and terms.
Character artwork is separate from the software MIT license.

```text
MIT License

Copyright (c) 2026 Victor (xtrimsystems) — original claude-pet
Copyright (c) 2026 ThatLev — ThatPet changes, apps and character art

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

The character packs in packs/ (artwork) are not covered by this licence:
all rights reserved by ThatLev unless a pack says otherwise.
```
