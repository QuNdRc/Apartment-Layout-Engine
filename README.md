# Apartment Layout Engine

Движок планировки квартир, в котором локальная языковая модель
(Gemma 4 E2B 4.6B на llama.cpp) в реальном времени предлагает правки планировки
и ссылается на действующие строительные нормы.

Приложение **полностью автономно**: не требует интернета, облачных API и GPU.
Модель, инференс, RAG по СП/ГОСТ — всё на машине пользователя.

---

## Как это работает

Пользователь перетаскивает комнаты по холсту (drag-and-drop), расставляет
двери, затем общается с AI-ассистентом во встроенном чате. Модель анализирует
текущую геометрию квартиры, конфликты, нормы — и предлагает исправления
в виде команд `[CMD: MOVE …, SWAP …, DOOR …, ADD …, REMOVE …]`, которые тут же
применяются к плану.

```
Пользователь                          C++ движок
   │                                      │
   │  перетаскивает комнаты               │  физика / сетка
   │  ставит двери                        │  детекция конфликтов
   │                                      │  state-string
   ▼                                      ▼
┌─────────────────────────────────────────────────────────┐
│  WPF UI (C#, .NET 10)                                   │
│                                                         │
│  1. Запрос в чат                                         │
│  2. BM25-поиск по snip_norms/ (СП 30, СП 54, …)        │
│  3. Промпт = система + нормы + геометрия + запрос        │
│  4. LlamaBridge.dll → llama.dll → Gemma 4 E2B 4.6B     │
│  5. [CMD: …] парсинг → команды → движок                 │
└─────────────────────────────────────────────────────────┘
```

**Результат:** 1–2 итерации в чате — и планировка приходит в консистентное
состояние. Можно экспортировать в DXF (CAD).

---

## Ключевые особенности

- **Полностью офлайн.** Ноль байт в сеть. Инференс локальный, RAG локальный,
  модель на диске. Никаких API-ключей, подписок, облаков.
- **Не нужен GPU.** Gemma 4 E2B 4.6B (Q5_K_M, ~3.5 ГБ) работает на CPU.
  На i7/i9 ответ генерируется за ~10–15 секунд.
- **Живой drag-and-drop.** Комнаты двигаются мышью, физический движок (силы,
  затухание) сам разрешает коллизии. Двери ставятся кликом по общей стене.
- **Ссылается на реальные нормы.** Встроенный BM25-индекс ищет по 9 документам
  СП/СНиП (градостроительство, жилые здания, водопровод, нагрузки, …) и подаёт
  релевантные выдержки в промпт модели.
- **Экспорт в DXF.** Готовый план можно открыть в AutoCAD / nanoCAD / LibreCAD.
- **Прозрачный инференс.** Никаких Python-прослоек в инференсе.
  C# → P/Invoke → LlamaBridge.dll → llama.cpp. Чистый C++ мост.

---

## Технический стек

| Компонент | Технология |
|-----------|-----------|
| GUI | WPF, C#, .NET 10 (net10.0-windows) |
| Физический движок | C++20, ApartmentLayoutEngine.dll (SoA/DOD, continuous + grid режимы) |
| Инференс | llama.cpp b10063, динамическая линковка llama.dll |
| Модель | Gemma 4 E2B 4.6B (Q5_K_M), ~3.5 ГБ, 35 слоёв, SWA + Gated Delta Net + Lightning Indexer |
| Мост | LlamaBridge.dll (C++ /EXPORT), P/Invoke из C# |
| RAG | BM25-индекс (C#-порт `bm25_index.h` из Solution5) |
| Связывание | DllImport + NativeLibrary динамическая загрузка, LPUTF8Str-маршалинг |

---

## Состав репозитория

```
.
├── bin/                              ← Portable-бинарь (exe + DLL + модель + нормы)
│   ├── WpfApp1.exe
│   ├── ApartmentLayoutEngine.dll     ← C++ движок симуляции
│   ├── LlamaBridge.dll               ← C++ мост к llama.cpp
│   ├── llama.dll, ggml*.dll          ← llama.cpp + CPU-бэкенды
│   ├── gemma-4-E2B-it-Q5_K_M.gguf    ← Модель (Git LFS)
│   └── snip_norms/                   ← СП/СНиП для RAG
├── LlamaBridge/                      ← Исходный код моста (показан как демонстрация C++/ML)
│   ├── bridge_static.cpp             ← Ядро: LB_Init, LB_LoadModel, LB_Generate
│   ├── test_static.cpp               ← Консольный smoke-тест
│   └── include/llama.h               ← llama.cpp C API
├── WpfApp1/                          ← C# слой инференса: P/Invoke, оркестратор, RAG
│   └── LlamaBridge.cs, PythonOrchestrator.cs, PromptBuilder.cs,
│       CommandParser.cs, Bm25Index.cs, SnipRagIndex.cs
├── snip_norms/                       ← База СП/СНиП (9 документов)
├── .gitattributes                    ← Git LFS для *.gguf
├── LICENSE                           ← MIT
└── README.md
```

> Полный движок (DynamicLibrary1), Python-сервис (ai_orchestrator) и UI-код
> остаются в приватном репозитории Solution2. Здесь — только инференсная часть
> и готовый бинарник.

---

## Быстрый старт

### 1. Скачайте бинарник

Папка `bin/` содержит всё для запуска «из коробки»:
- `WpfApp1.exe` — собран под .NET 10 (не нужен установленный runtime).
- Все DLL — ApartmentLayoutEngine, LlamaBridge, llama, ggml (20+ CPU-бэкендов).
- **Модель** `gemma-4-E2B-it-Q5_K_M.gguf` (~3.5 ГБ, Git LFS).
- **Нормы** `snip_norms/` — 9 документов СП/СНиП.

Модель хранится через Git LFS. При клонировании выполните:

```
git lfs pull
```

Если LFS недоступен, скачайте модель отдельно
([Gemma 4 E2B 4.6B Q5_K_M](https://huggingface.co/bartowski/gemma-4-E2B-it-GGUF))
и положите файл в `bin/`.

### 2. Запустите

```
bin/WpfApp1.exe
```

При первом запуске модель загружается в память ~10–20 секунд. В консоли могут
появляться красные строки `llama.cpp` — это норма, отладочный вывод INFO-уровня
(включение fused-операций: Lightning Indexer, Flash Attention, GDN, …).

### 3. Постройте план

- **Левая панель** — кнопки добавления комнат (гостиная, кухня, санузел, коридор)
  и дверей.
- **Холст** — перетаскивайте комнаты мышью, физика сама расталкивает.
- **Правая панель** — чат с AI. Примеры запросов:

  > соедини дверью коридор, гостиную и кухню
  >
  > сдвинь санузел к мокрой зоне
  >
  > помещение 12 м² в правой части — это гостиная

  Модель отвечает текстом на русском и блоком `[CMD: …]`, который сразу
  применяется к холсту.

---

## Что делает этот репозиторий демонстрационным

В папках [`LlamaBridge/`](LlamaBridge/bridge_static.cpp) и [`WpfApp1/`](WpfApp1/LlamaBridge.cs) лежит **только код,
отвечающий за инференс**: C++ мост к llama.cpp, P/Invoke-обёртка на C#,
оркестратор, RAG-интеграция. Это демонстрирует:

- Стыковку нативного C++ с .NET через P/Invoke + `NativeLibrary`.
- Работу с llama.cpp API: `llama_model_load_from_file`, `llama_decode`,
  `llama_sampler_*`, `llama_batch_get_one`.
- Токенизацию модели Gemma 4 (E2B, SWA, GDN).
- Собственную имплементацию BM25-индекса (порт из C++ в C#).
- Сборку `[CMD: …]` промптов и парсинг ответа.

Код физического движка (`DynamicLibrary1`) и Python-сервис (`ai_orchestrator`)
остаются приватными.

---

## Сборка из исходников

Требуется: Visual Studio 2022+ (MSVC), .NET 10 SDK.

```
# 1. C++ движок (ApartmentLayoutEngine.dll)
msbuild DynamicLibrary1/DynamicLibrary1.vcxproj /p:Configuration=Release

# 2. C++ мост (LlamaBridge.dll)
msbuild LlamaBridge/LlamaBridge.vcxproj /p:Configuration=Release

# 3. WPF-клиент
dotnet build WpfApp1/WpfApp1.csproj -c Release

# 4. Скопируйте модель и нормы в папку сборки
# WpfApp1/bin/Release/net10.0-windows/
```

---

## Модель

Используется [Gemma 4 E2B 4.6B (Q5_K_M)](https://huggingface.co/bartowski/gemma-4-E2B-it-GGUF) —
открытая модель Google, квантизованная для CPU-инференса. Почему именно она:

- Гибридная архитектура (Gated Delta Net + Lightning Indexer) — быстрее
  классических transformer-only моделей на CPU.
- Sliding window attention (SWA) — линейная сложность по длине контекста.
- Русский язык в базовой подготовке.

При желании модель можно заменить на любую GGUF-совместимую: положите
`.gguf`-файл в `bin/` рядом с `WpfApp1.exe` (приложение ищет модель
в директории запуска).

---

## Лицензия

Код — MIT. Модель Gemma 4 E2B распространяется под [лицензией Gemma](https://ai.google.dev/gemma/terms).