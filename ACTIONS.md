# Sharptillery - Code Review Actions

Last updated: 2025-01-10

---

## Investigation

### INV-001: Verify YAML phase functionality
- **Location**: `MainService.cs`, `config.yaml`
- **Severity**: Medium
- **Description**: Need to verify that YAML configuration with phases is actually working correctly.
- **Questions**:
  - Does multi-phase execution work end-to-end?
  - Are results aggregated correctly across phases?
  - Does the HTML report show phase-separated data?
  - Is `config.yaml` a valid test case?

---

## P2 - Nice to Have

### AP-002: Thread instead of Task
- **Location**: `MainService.cs:87,111`
- **Description**: Uses `new Thread(manager.RunTest)` when `Task.Run()` would be cleaner and more idiomatic.
- **Impact**: Less composable, harder to await properly.

### AP-004: Mixing concerns in MainService.Run()
- **Location**: `MainService.cs:29-82`
- **Description**: `Run()` handles YAML parsing, validation, phase loops, non-phase runs, and report writing all in one method.
- **Suggested Fix**: Extract into smaller focused methods or classes.

### AP-006: Console output scattered everywhere
- **Location**: `Manager.cs`, `MainService.cs`, `Client.cs`
- **Description**: `Console.Out.WriteLine` scattered across classes instead of using ILogger consistently.
- **Impact**: Hard to control verbosity, no log levels.

### AP-007: Blocking WaitOne() in async context
- **Location**: `Client.cs:35`
- **Description**: `_manager.StartClients.WaitOne()` blocks the thread in an `async Task` method.
- **Suggested Fix**: Use `ManualResetEventSlim` with async waiting or `TaskCompletionSource`.

---

## P3 - Cleanup (Low Priority)

### BUG-001: IDisposable cosmetic issue
- **Location**: `Manager.cs`
- **Description**: Manager implements `IDisposable` but this is a CLI tool — OS reclaims resources on exit anyway.
- **Note**: Not a real bug, just cosmetic.

### BUG-002: Misleading volatile keyword
- **Location**: `Manager.cs:21`
- **Description**: `volatile List<DataPoint>` is misleading — all writes happen on one thread, reads happen after completion.
- **Suggested Fix**: Remove `volatile`, optionally make `Results` return a copy.

### BUG-003: Double DateTime.UtcNow
- **Location**: `Client.cs:110-111`
- **Description**: `RequestReceivedTime` and `EndTime` set with two separate `DateTime.UtcNow` calls.
- **Impact**: Minor microsecond timing inaccuracy.

### SIMP-006: TopupSettings nested in Manager
- **Location**: `Manager.cs:57`
- **Description**: Could be extracted to `Models/` for clarity.

### DEAD-002: Excel reporting disabled
- **Location**: `MainService.cs:146-148`
- **Description**: `throw new NotImplementedException()` when `.xlsx` extension is used.
- **Decision needed**: Implement or remove the option.

### DEAD-004: Phase accumulation not implemented
- **Location**: `MainService.cs:103-104`
- **Description**: TODO comment about phases not accumulating results — each phase overwrites.
- **Note**: Related to INV-001.

---

## Completed (2025-01-10)

| ID | Summary |
|----|---------|
| SIMP-001 | Migrated CLI from custom FlagParser to System.CommandLine |
| DEAD-001 | Implemented per-client rate limiting (`-r`/`--rps`) |
| SIMP-005 | Replaced O(n²) RPS calculation with O(n) sliding window |
| AP-001 | Extracted ConsoleReporter from Manager |
| AP-003 | Unified settings mapping with `ToSettings()` method |
| SIMP-004 | Extracted `IsError()` to DataPoint instance method |
| SIMP-003 | Extracted `Percentile()` to StatsUtils |
| AP-005 | Centralized settings validation in `Settings.Validate()` |
| SIMP-002 | Removed FactoryEnum dead code, simplified CustomHttpClientFactory |
| BUG-004 | Removed stale pragma and unused `_logger` from MainService |
| BUG-005 | Renamed `Done` → `_done` in Manager |
| DEAD-003 | Removed unused `_httpClientFactory` field (part of SIMP-002) |
