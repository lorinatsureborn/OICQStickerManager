# Compatibility Regression Probe

Run from the repository root on Windows with the .NET 10 SDK:

```powershell
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\audit\tests\
```

The separate output directory avoids overwriting a running Asuka executable.
The probe exits with code 1 when any assertion fails. It uses temporary files,
hidden test windows and a local UI Automation provider; it does not send QQ
messages, acquire real QQ keys or change actual QQ favorites. Debugger tests use
only a child fixture process and a synthetic key. Temporary files are removed
after the run. Watcher diagnostic messages may be appended to its existing log.

Coverage includes real filesystem events, lifecycle, per-process QQ capability
state, target-bound serialized sends, clipboard ownership, ordered persistence,
profile relocation, custom QQ directories, pending deletion, SQLCipher page
authentication, committed encrypted WAL, DPAPI protection, owned helper cleanup,
hardware-debugger restoration, PE layout parsing, physical panel placement,
blur fallback, clipboard result generations and async command behavior.
The suite includes pruned UIA ancestor handling,
current editor-focus evidence, bounded focus settlement, guarded recovery,
legacy tab/toolbar geometry and reclaimable UIA-process lifecycles. Legacy panel
fixtures cover visible nodes outliving button evidence, hidden nodes overriding
fresh evidence, and rejection of unrelated geometry, processes and cursor-only
anchors. Button templates respect the selected isolated profile. Placement fixtures
cover narrowing to either side at high DPI without covering the native QQ panel,
and retaining the preferred width when space permits. Worker fixtures test
hung leases with live heartbeats, throwing status observers, request correlation,
response publication order, request timeouts and process cleanup.
Key-helper fixtures cover the launcher's active version, mismatched running builds,
missing/escaping version paths, legacy single-version fallback, raw PE alignment
padding, and a real Windows PowerShell subprocess using an explicit fake target.
The fake helper returns only a synthetic key and does not start or debug QQ.
AI persistence fixtures verify that the active profile restores all working
configuration fields, ordered saves retain AI profiles and the protected QQ key,
and an inactive profile does not replace the saved draft.
AI API fixtures use synthetic keys and an injected HTTP handler, covering OpenAI
project/service-account key detection, completion budgets, Claude model cursors
and capability metadata, HTTP errors, cancellation, format retries, diagnostic
redaction, active model persistence, draft invalidation and isolated cache flushes.
A hidden WPF window verifies that the missing-key guide opens an editable API key
section from the gallery, single-sticker editor and batch editor on the first try,
and that manual settings entry still works. It also verifies masked key input,
password automation protection,
profile-to-input synchronization, settings refresh, model input before verification,
late verification replies and suggestions arriving after a different sticker is
opened. It does not show the app or run the app's startup hooks. Run only these
35 AI cases by appending `-- --ai-only` to the command above; no API credits are used.
The 12 storage/security cases can run separately with `-- --ai-security-only`.
They cover DPAPI profile round trips, memory-only drafts, refusal of old plaintext
fields, unreadable ciphertext, backup sanitization, interrupted atomic writes,
request-object formatting, and arbitrary-key redaction in errors and logs.
Cipher fixtures cover SHA512 and legacy SHA1 page authentication, legacy 48-byte
cipher trailers with either 48-byte or 80-byte SQLite reserves, wrong keys,
corrupt pages, mixed-format rejection and committed legacy encrypted WAL.
Private implementation boundaries are accessed through reflection to keep this
initial regression suite independent of the app's startup and user data.

Read-only diagnostics for a real installation/module (no debugger attachment):

```powershell
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\audit\tests\ -- --inspect-key-installation
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\audit\tests\ -- --inspect-key-module "C:\Program Files\Tencent\QQNT\versions\9.9.19-35469\resources\app\wrapper.node"

dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\audit\tests\ -- --inspect-qq-points "1040,974;1094,974"

dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\audit\tests\ -- --inspect-qq-structure <QQ-window-handle>

dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\audit\tests\ -- --inspect-qq-database bin\audit\session-20261006
```

Point inspection has a three-second process deadline. Structure inspection allows
three seconds for subscription and observes at most 40 events over 60 seconds;
it records geometry/identity rather than chat text. Neither diagnostic invokes
QQ controls or modifies its data. Replace coordinates/handle with current local
observations; these examples are not persistent automation targets.

Database inspection is a separate real-data diagnostic, not part of the default
fixture suite. It reads the specified profile's existing protected key and bound
emoji databases, authenticates every committed page, and runs the production
SQLite integrity/index reader. It does not acquire keys or modify QQ databases
or the profile. Only account suffixes, byte/page counts and favorite counts are
printed. Decrypted data is written to a unique temporary directory for SQLite
and removed in a finally block; keys and favorite identifiers are not printed.

This is component regression coverage, not a substitute for QQ-version, IME,
permission, multi-monitor DPI or long-running integration tests.
Input-method recovery fixtures can run with `-- --accessibility-only`. They cover
modern QQ with missing accessible controls, stale and cross-chat button caches,
non-QQ rejection, and retaining a pixel-confirmed opening through verification.
They use off-screen fixture windows and do not switch the user's input method.
The Windows GitHub Actions workflow runs the same commands. Its remote runner
result must be checked separately; adding the workflow is not a CI pass claim.

Batch management regressions can run with `-- --batch-only`. Thirteen model fixtures
cover selection across filters/tabs/accounts, current-page select all, QQ rescan
identity, transient metadata, click-command send suppression and mixed-source tag
editing. QQ cleanup verifies the authoritative index once per account and tests
refavorites, unavailable/changing indices, locked files, path ownership and account
unbinding. The shared hidden WPF fixture exercises the actual library and QQ
toolbars, gallery click route, 4-DIP outlines/checkmarks, tag save/cancel, deletion
confirmation, cross-tab cache cleanup and global selection clear. Synthetic HTTP
responses verify the AI concurrency cap of two, cache reuse, partial failure and
cancellation of active/queued requests. No real API credits or user files are used.
Set `ASUKA_BATCH_SCREENSHOT` or `ASUKA_BATCH_QQ_SCREENSHOT` to PNG paths to save
minimum-size library or QQ renderings. The full suite also verifies per-account
index-read and authentication failure handling in batch orphan confirmation.
The WPF fixture waits past the confirmation dialog's closing animation before
checking that cleanup results remain visible. It also verifies that completion
unlocks the toolbar before dismissing the result, and that a zero-deletion result
for an undecodable image allows exit. Preview fixtures retain the original
send/delete path while falling back to QQ's thumbnail after decoding fails.

QQ coexistence and sending regressions can run separately (54 cases):

```powershell
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\qq-core\tests\ -- --qq-core-only
```

These cover native host lifetime, outside clicks, injected editor-click evidence,
late worker frames after dismissal, QQ's reused panel nodes, missing panel events,
clipboard retry focus changes, and recovery when an already-focused editor emits
no second focus event. The test host uses the same DPI manifest as the app.

Quick-panel placement regressions can run separately:

```powershell
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\panel-placement\tests\ -- --placement-only
```

These exercise the actual quick-panel HWND with transparent content and disabled
blur tiles/input hooks. They check duplicate notifications, direct relocation,
placement before first display, cold handle creation, opening during warm-up and
round trips across every attached monitor without intermediate visible bounds.
The coexistence fixtures also verify button-relative panel cache relocation and
invalidation after the host resizes or a different chat tries to reuse it.

On a machine whose security software blocks the synthetic PowerShell key-helper
bootstrap fixture, append `-- --skip-key-bootstrap` to run every other fixture.
This excludes exactly one key-acquisition case; it does not disable security or
skip coexistence/sending checks. The 2026-10-07 pre-push run of the isolated Git
index snapshot passed 143/143 enabled cases.
After including the 35 AI cases, a separate pre-push index snapshot passed
178/178 enabled cases with the same single bootstrap exclusion.

The following diagnostics operate on the current real QQ window and are **opt-in**:

```powershell
# Changes the QQ draft using the production send path; never presses Enter.
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\qq-core\tests\ -- --exercise-qq-send <QQ-window-handle> <image-path>

# Replaces this profile's toolbar template from the currently visible button.
# Use current observed physical coordinates and keep the pointer away from it.
dotnet run --project probes\CompatibilityTests\CompatibilityTests.csproj -c Release -p:OutputPath=bin\qq-core\tests\ -- --capture-qq-button <QQ-window-handle> "x,y,width,height"
```

The draft diagnostic's `Sent` means the production focus/clipboard/input sequence
was accepted. Confirm actual image insertion in QQ visually; this status alone
is not QQ's acknowledgement of its document content. These two diagnostics are
not part of the default fixture run.
