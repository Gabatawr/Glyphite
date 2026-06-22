# TODO — Glyphite codebase cleanup & refactoring

## 🐛 Баги

### 1. Испорченная Unicode-строка в FailSafeChatClient
- **Файл:** `src/Glyphite.Host/Services/FailSafeChatClient.cs:157`
- **Проблема:** `"Skipped вЂ” previous tool errored"` — em dash `—` превратился в кракозябры из-за битой UTF-8 кодировки файла
- **Решение:** заменить на `"Skipped — previous tool errored"` (или `" — "`)

## 🔧 Рефакторинг

### 2. MemoryStore — нарушение ISP (Interface Segregation)
- **Файл:** `src/Glyphite.Host/Data/MemoryStore.cs`
- **Проблема:** один класс имплементирует `IAgentStore`, `IBlockStore`, `IConfigStore`. ~3500 строк в partial-файлах.
- **Решение:** разбить на отдельные классы-репозитории (`BlockRepository`, `ConfigRepository`, `SessionRepository`), каждый со своим файлом и интерфейсом.

### 3. CreateReadConnection() — новый SQLite connection на каждый вызов
- **Проблема:** ~30 вызовов `using var conn = CreateReadConnection();` — каждый раз новая строка подключения
- **Контекст:** в рамках скоупа агента можно переиспользовать `_conn` для read-only, но нужна осторожность с конкуренцией
- **Решение:** опционально — переиспользовать существующее подключение для read-only операций

### 4. _writeLock — copy-paste блокировка 48+ раз
- **Файлы:** `MemoryStore*.cs`
- **Проблема:** паттерн `await _writeLock.WaitAsync(); try { ... } finally { _writeLock.Release(); }` повторяется ~48 раз
- **Решение:** сделать helper-метод `async Task<T> WithLockAsync<T>(Func<Task<T>> action)` или декоратор

### 5. Пустые `catch { }` — нужно проработать flow ошибок
- **Файлы:**
  - `MemoryBlock.cs:124` — ошибка парсинга JSON в `ToContextString()`
  - `FailSafeChatClient.cs:139` — ошибка трекинга memory clean
  - `BashSessionManager.cs:72, 196` — ошибка `Kill()` процесса
  - `McpService.cs:108, 322` — ошибка `DisposeAsync()`
- **Решение:** логгировать ошибки через Serilog, а не проглатывать

### 6. `null!` для scoped-сервисов в ChatRepl
- **Файл:** `ChatRepl.cs:16-17`
- **Проблема:** `_turnProcessor = null!` и `_blockMemory = null!`. Если вызвать метод до `SwitchScope()` — NRE
- **Решение:** избавиться от `null!` — инициализировать через фабрику или ленивую загрузку

### 7. `ConfigureAwait(false)` — непоследовательно
- **Проблема:** используется только в `McpService.cs`. В консольном/библиотечном коде не нужно (нет контекста синхронизации)
- **Решение:** убрать `ConfigureAwait(false)` из всех мест (кроме библиотечных сценариев)

### 8. FailSafeChatClient — 540 строк, сложная логика
- **Файл:** `src/Glyphite.Host/Services/FailSafeChatClient.cs`
- **Проблема:** один класс отвечает за параллельное/последовательное выполнение тулов, трекинг ошибок, подсчёт токенов
- **Решение:** разделить на `ToolExecutor`, `UsageTracker`, `FailSafeChatClient` (только обёртка)

### 9. TurnProcessor — async iterator с критической логикой
- **Файл:** `src/Glyphite.Host/Services/TurnProcessor.cs`
- **Проблема:** вся логика turn-а (очистка пика, компакция, стриминг, сохранение usage, flush блоков) — в одном методе с `yield return`
- **Важно:** эту логику долго отлаживали, менять с осторожностью
- **Решение:** выносить шаги по одному, с тестами

### 10. ChatRepl — слишком много ответственности
- **Файлы:** 6 partial-файлов (Input, Commands, Config, Startup, Streaming, ChatRepl.cs)
- **Проблема:** REPL + rendering + управление агентами + конфиги — всё в одном классе
- **Решение:** разделить на отдельные сервисы (CommandHandler, SessionManager, ConfigManager)

## 🧹 Чистка

### 11. Нет юнит-тестов
- **Решение:** не требуется на данном этапе (сознательное решение)

### 12. FormattableString.Invariant в TodoTool
- **Файл:** `src/Glyphite.Host/Tools/TodoTool.cs`
- **Проблема:** остались 3 вызова `FormattableString.Invariant($"...")`, хотя возвращаемая строка уже упрощена (без номера блока)
- **Решение:** заменить на обычную интерполяцию строк

### 13. _compressionOpts в ChatRepl — хот-релоад конфига
- **Файл:** `ChatRepl.cs:24`
- **Проблема:** `_compressionOpts` инициализируется через DI (при старте), а конфиг может меняться через `Glyphite.json` между turn-ами
- **Важно:** хот-релоад должен работать — изменения в `Glyphite.json` должны подтягиваться в следующий turn
- **Решение:** получать свежий `CompressionOptions` через `_cfgService.GetOptionsAsync<CompressionOptions>()`, как это делается в других местах
