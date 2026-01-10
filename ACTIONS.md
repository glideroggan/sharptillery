# Sharptillery - Code Review Actions

Last updated: 2025-01-10

## Recent Changes

- **2025-01-10**: SIMP-005 completed — Replaced O(n²) RPS calculation with O(n) sliding window algorithm. Processing 10k requests now instant.
- **2025-01-10**: DEAD-001 completed — Implemented smooth per-client rate limiting (`-r`/`--rps`). Each client paces independently with staggered start and ±20% jitter for realistic load distribution.
- **2025-01-10**: SIMP-001 completed — Migrated from custom `FlagParser` to `System.CommandLine`. Added long flag support, auto-generated help, centralized validation. Deleted 227 lines of custom parsing code.

## Overview

This document tracks issues found during code review and their resolution status.

---

## Bugs

### BUG-001: IDisposable not implemented
- **Location**: `Manager.cs:246-249`
- **Severity**: ~~High~~ Low (cosmetic)
- **Description**: `Dispose()` throws `NotImplementedException`. Manager implements `IDisposable` but never disposes resources (clients, CancellationTokenSource, ManualResetEvents).
- **Impact**: ~~Memory/handle leak on repeated test runs.~~ Minimal — OS reclaims on process exit. Manager only lives for process lifetime.
- **Revised Assessment**: This is a CLI tool, not a long-running service. The OS reclaims all resources on exit. Only real leak is CTS per phase, which is negligible.
- **Action**: [ ] Pending discussion (downgraded to P3)

### BUG-002: ~~Race condition on _responseData~~ (Investigated - Not a bug)
- **Location**: `Manager.cs:44`
- **Severity**: ~~High~~ Low (code smell only)
- **Description**: `volatile List<DataPoint>` appears thread-unsafe, but investigation shows all writes occur in the RunTest thread and reads happen after thread completion.
- **Investigation Result**: 
  - All `_responseData.Add()` calls happen in `DequeueAndAccumulate()` which is only called from `RunTest()`
  - All reads (`Report()`, `ProcessData()`, `Results` property) happen after `RunTest` thread exits
  - The `volatile` keyword is misleading/unnecessary but not causing bugs
- **Remaining Issues**:
  - `volatile` should be removed (misleading)
  - `Results` property exposes internal list directly — could cause issues if accessed during test
- **Recommended Fix**: Remove `volatile`, change `Results` to return a copy: `public List<DataPoint> Results => _responseData.ToList();`
- **Action**: [ ] Pending discussion (downgraded to P3)

### BUG-003: Double DateTime.UtcNow assignment
- **Location**: `Client.cs:110-111`
- **Severity**: Low
- **Description**: `RequestReceivedTime` and `EndTime` are set separately with two `DateTime.UtcNow` calls - could differ by microseconds.
- **Impact**: Minor timing inaccuracy.
- **Action**: [ ] Pending discussion

### BUG-004: Stale pragma warning disable
- **Location**: `MainService.cs:1`
- **Severity**: Low
- **Description**: `#pragma warning disable CA1848` at file level, never restored.
- **Impact**: Suppresses warnings project-wide in this file.
- **Action**: [ ] Pending discussion

### BUG-005: ManualResetEvent field naming
- **Location**: `Manager.cs:62`
- **Severity**: Low
- **Description**: `Done` is a private field but uses PascalCase instead of `_done`.
- **Impact**: Inconsistent with C# conventions.
- **Action**: [ ] Pending discussion

---

## Anti-Patterns

### AP-001: God class - Manager
- **Location**: `Manager.cs` (600+ lines)
- **Severity**: Medium
- **Description**: Handles queuing, timing, progress, auto-tune, reporting, client management, and RPS calculation all in one class.
- **Suggested Fix**: Split into focused classes (RequestQueue, ResponseCollector, AutoTuner, ProgressTracker).
- **Action**: [ ] Pending discussion

### AP-002: Thread instead of Task
- **Location**: `MainService.cs:151,175`
- **Severity**: Low
- **Description**: Uses `new Thread(manager.RunTest)` when `Task.Run()` would be cleaner and more idiomatic.
- **Impact**: Less composable, harder to await properly.
- **Action**: [ ] Pending discussion

### AP-003: Settings duplication
- **Location**: `ArtilleryConfig.cs` vs `Settings.cs`
- **Severity**: Medium
- **Description**: Two nearly identical config classes. `MainService` manually maps between them.
- **Suggested Fix**: Unify into a single source of truth.
- **Action**: [ ] Pending discussion

### AP-004: Mixing concerns in MainService.Main()
- **Location**: `MainService.cs:26-146`
- **Severity**: Medium
- **Description**: `Main()` is 146 lines, handles YAML parsing, validation, phase loops, non-phase runs, and report writing.
- **Suggested Fix**: Extract into smaller focused methods or classes.
- **Action**: [ ] Pending discussion

### AP-005: Repeated validation logic
- **Location**: `MainService.cs:86-90, 109-110`, `Manager.cs:276-280`
- **Severity**: Low
- **Description**: Duration/MaxRequests mutual exclusivity checked in 3 places.
- **Suggested Fix**: Centralize validation in one place.
- **Action**: [ ] Pending discussion

### AP-006: Console output scattered everywhere
- **Location**: `Manager.cs`, `MainService.cs`, `Client.cs`
- **Severity**: Low
- **Description**: `Console.Out.WriteLine` scattered across classes instead of using ILogger consistently.
- **Impact**: Hard to control verbosity, no log levels.
- **Action**: [ ] Pending discussion

### AP-007: Blocking WaitOne() in async context
- **Location**: `Client.cs:35`
- **Severity**: Medium
- **Description**: `_manager.StartClients.WaitOne()` blocks the thread in an `async Task` method.
- **Suggested Fix**: Use `ManualResetEventSlim` with async waiting or `TaskCompletionSource`.
- **Action**: [ ] Pending discussion

---

## Simplification Opportunities

### SIMP-001: Custom FlagParser ✅ COMPLETED
- **Location**: ~~`FlagParser.cs`~~ (deleted)
- **Severity**: Low
- **Description**: ~~Reinvents argument parsing.~~ Migrated to `System.CommandLine` for better UX.
- **Resolution**: 
  - Created `CommandLineParser.cs` using `System.CommandLine` 2.0.1
  - Added long flag support (`--target`, `--clients`, etc.) alongside short flags
  - Auto-generated `--help` and `--version` flags
  - Centralized validation with proper error messages
  - Deleted `FlagParser.cs` (227 lines of custom code removed)
- **Action**: [x] Completed 2025-01-10

### SIMP-002: Unused FactoryEnum.Microsoft
- **Location**: `CustomHttpClientFactory.cs`
- **Severity**: Low
- **Description**: `FactoryEnum.Microsoft` path exists but is never used. Dead code.
- **Action**: [ ] Pending discussion

### SIMP-003: Percentile calculation duplicated
- **Location**: `Manager.cs:80-85`, `HtmlReport.cs:174-181`
- **Severity**: Low
- **Description**: Same percentile logic in two places.
- **Suggested Fix**: Extract to shared utility method.
- **Action**: [ ] Pending discussion

### SIMP-004: IsError predicate duplicated
- **Location**: `Manager.cs:565-566`, `HtmlReport.cs:68-69`
- **Severity**: Low
- **Description**: Same error detection logic defined twice.
- **Suggested Fix**: Extract to `DataPoint` extension method or shared utility.
- **Action**: [ ] Pending discussion

### SIMP-005: ProcessData O(n^2) algorithm ✅ COMPLETED
- **Location**: ~~`Manager.cs:179-236`~~ (refactored to ~180-206)
- **Severity**: Medium
- **Description**: ~~RPS calculation iterates backwards through the list for each point. For 100k requests, this is 10 billion iterations.~~
- **Resolution**:
  - Replaced O(n²) nested loop with O(n) sliding window algorithm
  - Uses two pointers: `left` and `right` moving forward through sorted data
  - Each pointer traverses list once → total 2n operations
  - Processing 10,000 requests now completes instantly (was seconds with dots)
- **Verified**: 5,000 and 10,000 request runs complete with instant "Processing data..."
- **Action**: [x] Completed 2025-01-10

### SIMP-006: TopupSettings nested in Manager
- **Location**: `Manager.cs:72`
- **Severity**: Low
- **Description**: Could be extracted to `Models/` for clarity.
- **Action**: [ ] Pending discussion

---

## Dead/Incomplete Code

### DEAD-001: ConstantRps not wired ✅ COMPLETED
- **Location**: ~~`Manager.cs:270-274`~~ (refactored)
- **Severity**: Medium
- **Description**: ~~`_blocker` SemaphoreSlim is created but never used to throttle requests. The `-r` flag does nothing.~~
- **Resolution**:
  - Created `ClientRateLimiter.cs` with per-client rate limiting
  - Each client calculates interval: `1000ms / (totalRPS / clientCount)`
  - Staggered start delays prevent synchronized bursts
  - ±20% jitter creates realistic load distribution
  - Removed unused `_blocker` SemaphoreSlim and `FlagEnum` enum
  - Fixed `Manager.Dispose()` to actually dispose resources
  - Updated `-r` flag description in help text
- **Verified**: 10 RPS → 5.33s for 50 requests; 100 RPS → ~93 RPS achieved (jitter expected)
- **Action**: [x] Completed 2025-01-10

### DEAD-002: Excel reporting disabled
- **Location**: `MainService.cs:206-208`
- **Severity**: Low
- **Description**: `throw new NotImplementedException()` when `-e excel` is used.
- **Action**: [ ] Pending discussion (implement or remove flag)

### DEAD-003: Unused IHttpClientFactory field
- **Location**: `CustomHttpClientFactory.cs:15`
- **Severity**: Low
- **Description**: `_httpClientFactory` is stored but never used in `Roger` mode (the only mode in use).
- **Action**: [ ] Pending discussion

### DEAD-004: Phase accumulation not implemented
- **Location**: `MainService.cs:167-168`
- **Severity**: Medium
- **Description**: TODO comment: "To not clear out the results..." - phases don't accumulate results, each phase overwrites.
- **Action**: [ ] Pending discussion

---

## Priority Matrix

| Priority | Items |
|----------|-------|
| **P0 - Fix Now** | (none) |
| **P1 - Fix Soon** | (none) |
| **P2 - Nice to Have** | AP-001, AP-003, AP-002, AP-004, AP-005, AP-006, AP-007, SIMP-002 thru SIMP-004 |
| **P3 - Cleanup** | BUG-001, BUG-002, BUG-003, BUG-004, BUG-005, SIMP-006, DEAD-002, DEAD-003, DEAD-004 |
| **Completed** | SIMP-001 (FlagParser → System.CommandLine), DEAD-001 (Rate limiting), SIMP-005 (O(n) ProcessData) |

---

## Discussion Notes

<!-- Add notes from discussion here -->
