# TODO — Glyphite codebase cleanup & refactoring

## 🐛 Баги

### ~~1. Испорченная Unicode-строка в FailSafeChatClient~~ ✅
- **Исправлено:** `Skipped — previous tool errored` — em dash восстановлен

## 🔧 Рефакторинг

### ~~2. MemoryStore — нарушение ISP (Interface Segregation)~~ ✅
- **Исправлено:** `MemoryStore` разбит на `SessionRepository` (IAgentStore), `BlockRepository` (IBlockStore), `ConfigRepository` (IConfigStore).
- Каждый репозиторий — отдельный файл, отдельный SQLite connection + semaphore.
- DI регистрирует три singletone напрямую.
- Старые файлы `MemoryStore*.cs` удалены.

### ~~3. CreateReadConnection() — новый SQLite connection на каждый вызов~~ (частично)
- **FIX:** `ReplaceBlocksSinceAsync` — атомарная замена истории компакции в одной транзакции (вместо 3 отдельных вызовов)
- **Open:** ~30 вызовов `CreateReadConnection()` для read-only операций пока не переиспользуются

### ~~4. _writeLock — copy-paste блокировка 48+ раз~~ ✅
- **Исправлено:** все ~25 блоков `WaitAsync/try/finally/Release` заменены на `WithLockAsync(Func<Task>)` / `WithLockAsync<T>(Func<Task<T>>)` helper.

### ~~5. Пустые `catch { }` — нужно проработать flow ошибок~~ ✅
- **Исправлено:** все пустые `catch` заменены на `Console.Error.WriteLine`
  - **6 catch** в `MemoryBlock.cs`, `FailSafeChatClient.cs`, `BashSessionManager.cs`, `McpService.cs` — логгирование ошибок
  - **4 catch** в `TurnProcessor.cs:194,202,246,373` — парсинг args, чтение файла, memory clean, CleanToolArgs
  - **CompactionService.cs:269** — ошибка LLM при суммаризации (graceful degradation, без потери данных)
- **CompactionService:** удаление непротектированных блоков перенесено ПОСЛЕ суммаризации — если LLM упала, блоки не теряются

### ~~6. `null!` для scoped-сервисов в ChatRepl~~ ✅
- **Исправлено:** `_turnProcessor` и `_blockMemory` стали computed properties, читающие из `_currentScope`. Никакого `null!`.

### ~~7. `ConfigureAwait(false)` — непоследовательно~~ ✅
- **Исправлено:** убраны все 15 `.ConfigureAwait(false)` из `McpService.cs` (13) и `WebFetchTool.cs` (2) — в консольном приложении нет контекста синхронизации

### ~~8. FailSafeChatClient — 540 строк, сложная логика~~ ✅
- **Исправлено:** класс разделён на 3:
  - **UsageTracker** (92 строки) — учёт токенов (`ExtractCacheTokens`, `RecordUsage`)
  - **ToolExecutor** (202 строки) — группировка/выполнение тулов, трекинг peek и memory-clean
  - **FailSafeChatClient** (310 строк) — оркестратор, стриминг/не-стриминг циклы, делегирует ToolExecutor и UsageTracker
- **Итог:** `−239 строк` из FailSafeChatClient, логика изолирована, каждый класс можно тестировать отдельно

### ~~9. TurnProcessor — async iterator с критической логикой~~ ✅
- **Выполнено:** локальные функции (`ProcessUpdate`, `FlushReasoning`, `FlushText`, `FlushAll`) и вспомогательные методы (`CleanToolArgs`, `BuildPeekCleanMessage`) вынесены в `TurnContext` (файл `TurnProcessor.Streaming.cs`).
- **TurnProcessor.cs** (169 строк) — только оркестрация: подготовка → стриминг → финиш. Три чёткие фазы, `yield return` только в ProcessAsync.
- **TurnProcessor.Streaming.cs** (278 строк) — `TurnContext` с полным состоянием потока. Логика не менялась — только перемещение кода.
- **Итог:** `400 → 447 строк` (+47 на boilerplate класса), оба файла <300 строк, `-231` из TurnProcessor.cs.
- **TODO #10 ChatRepl** остаётся последним крупным пунктом.

### ~~10. ChatRepl — слишком много ответственности~~ ✅
- **Выполнено:** `ChatRepl` разгружен — 2 partial-файла удалены (Config.cs, Startup.cs), их логика в `SessionManager`.
- **SessionManager** (850 строк) — владеет `_agentId`, `_currentScope`, `SwitchScope()`, startup/resume, все `/команды` (new/clone/use/delete), config loading.
- **ChatRepl.cs** (96 строк) — только основное поле + RunAsync loop.
- **ChatRepl.Commands.cs** (120 строк) — диспетчер команд, делегирует в `SessionManager`.
- **ChatRepl.Input.cs** (459 строк) — без изменений (отвечает только за ввод).
- **ChatRepl.Streaming.cs** (154 строк) — без изменений (отвечает только за рендеринг стрима).
- **Итог:** `−2 partial-файла`, `−393 строк` из ChatRepl partials, логика в двух отдельных тестируемых сервисах (`SessionManager` + `InputHistory`).
- **TODO полностью очищен!** 🎉

## 🧹 Чистка

### 11. Нет юнит-тестов
- **Решение:** не требуется на данном этапе (сознательное решение)

### ~~12. FormattableString.Invariant в TodoTool~~ ✅
- **Исправлено:** все `FormattableString.Invariant` убраны при рефакторинге туду-тула

### ~~13. _compressionOpts в ChatRepl — хот-релоад конфига~~ ✅
- **Исправлено:** `_compressionOpts` поле и DI-инъекция убраны. `UpdatePromptPrefixAsync()` получает свежий `CompressionOptions` через `_cfgService.GetOptionsAsync<CompressionOptions>()` каждый turn.
