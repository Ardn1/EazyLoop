# EazyLoop

EazyLoop — мини-секвенсор для быстрого создания лупов из гитары, голоса и MIDI. Запиши слой с микрофона, добавь партии и сохрани всё в одной папке проекта.

[Скачать портативную бету для Windows x64](https://github.com/Ardn1/EazyLoop/releases/tag/v0.7.10) · [Руководство](docs/guide-ru.md) · [MIT](LICENSE)

![Главное окно EazyLoop с аудио, синтезатором и ударными](docs/screenshots/eazyloop.png)

## Возможности

- **Запись лупов** с выбором микрофона и входа для каждой дорожки, темпом, метрономом и предтактом.
- **Редактор аудио** для разрезания, сдвига фрагментов и настройки громкости по точкам. Исходная запись хранится в проекте.
- **MIDI и встроенные инструменты** с piano roll, нотным станом, редактором ударных, SimpleSynth, SimpleDrums и глиссандо-синтом.
- **Микшер и эффекты** с громкостью, панорамой, мьютом, соло, эквалайзером, автотюном, шумодавом RNNoise и поддержкой VST3.
- **Сохранение проектов и экспорт WAV**, а также темы и пять языков интерфейса.

## Быстрый старт

Распакуй архив и запусти <code>EazyLoop.exe</code>. Установка и отдельный .NET Runtime не требуются. Выбери микрофон, нажми **R** или **Ctrl+Space** для записи, затем добавляй слои. Щелчок по записанной волне открывает редактор, **Ctrl+S** сохраняет проект.

<details>
<summary>Редакторы аудио и MIDI</summary>

![Редактор аудио с фрагментами и точками громкости](docs/screenshots/audio-editor.png)

![Редактор MIDI с аккордами и полосой Velocity](docs/screenshots/midi-editor.png)

</details>

## Сборка

Windows x64, .NET 10 SDK и WPF. Команда портативной сборки из корня репозитория:

    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist/portable

## Лицензии

Исходный код EazyLoop доступен по [MIT](LICENSE). [NAudio](NAudio-LICENSE.txt) распространяется по MIT, [RNNoise](ThirdParty/RNNoise/RNNoise-LICENSE.txt) — по BSD 3-Clause. Внешние VST3 подключаются отдельно.
