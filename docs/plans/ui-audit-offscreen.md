# TanTienQR — UI Audit Plan (Sandboxed)

## Objective
Audit the TanTienQR Avalonia desktop app for **visual/layout defects** (misalignment, uneven spacing, overflow/clipping, inconsistent sizing, text truncation, broken grids), and produce a **report for the user to approve** before any fix is applied.

## Non-negotiable constraints
1. **DO NOT modify any application source file.** This phase is read-only w.r.t. `TanTienQR/`, `ClassLibraryMT*`, `tools/`, `license-portal/`.
2. **DO NOT show any window on the user's screen.** The user is working on another task. All rendering MUST be off-screen via `Avalonia.Headless`.
3. **DO NOT touch the user's real databases.** The app writes SQLite to `logs/` under `AppContext.BaseDirectory`. The harness must run against a **copied/sandboxed output directory**, never the user's `TanTienQR/bin/Debug/net10.0/logs`.

## Verified environment facts (already confirmed)
- Repo root: `d:\DuAn\VisionF`
- .NET SDK 10.0.403 present; `dotnet build TanTienQR\TanTienQR.csproj -c Debug` succeeds with 0 warnings/errors.
- Avalonia 12.1.4. `Avalonia.Headless` 12.1.4 restores fine from nuget.org.
- Off-screen render + PNG save **already proven working** with this exact pattern:
  - `AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();`
  - `win.Show(); Dispatcher.UIThread.RunJobs(); Thread.Sleep(500); Dispatcher.UIThread.RunJobs();`
  - `new RenderTargetBitmap(new PixelSize(w,h), new Vector(96,96)); bitmap.Render(win); bitmap.Save(path);`
  - Confirmed output PNG at `C:\tmp\headless_test.png` renders correctly (text visible, correct colors).
- Login credentials given by user: **admin / tantien512**.
- Existing `users.db` exists at `TanTienQR\bin\Debug\net10.0\logs\users.db` (contains the admin account).
- The app's license (`license.lic`) does NOT gate the UI — it only affects PLC "Ready" state (D16) and a status badge. `LoginViewModel` has no license check. So the UI is fully reachable without a valid license.
- Login is a multi-step wizard in `LoginShellWindow` (username step -> forced password change step -> main window). Note: seeded admin has `MustChangePassword = 1`, so the password-change wizard step may appear; the harness must handle/drive it.

## App architecture (for navigation)
- `TanTienQR/Program.cs` -> `BuildAvaloniaApp()` (reusable entry point).
- `TanTienQR/App.axaml.cs` -> `App : Application`; `Initialize()` loads XAML + embedded fonts (`fonts:SpaceGrotesk`, `fonts:JetBrainsMono`, `fonts:JetBrainsMedium`); `OnFrameworkInitializationCompleted()` builds a DI `Host`, loads license/config, starts services (PLC heartbeat, camera worker, handheld scanner TCP listener on 127.0.0.1:6999), then runs `RunLoginLoopAsync`.
- `App.RunLoginLoopAsync`: creates `LoginShellWindow` -> waits -> creates `MainWindow` -> waits -> on logout loops back.
- `MainWindow.axaml` (1360x820, `WindowDecorations=None`) contains a sidebar + `InfoView` (via `ViewLocator`).
- `ViewLocator` maps `MainViewModel` -> `InfoView`. Other views are hosted inside `InfoView`.
- View files to audit (`TanTienQR/Views/`):
  `InfoView`, `CodeLookupView`, `ConfigView`, `DevView`, `HistoryView`, `AccountsView`, `PlcSettingsView`, `ProductionOrdersView`, `ProductionView`, `LoginShellWindow`, `LicenseWindow`, `PasswordChangeWindow`, `PlcParamEditWindow`, `RecipeDeleteConfirmWindow`, `UserEditWindow`.
- ViewModels: `TanTienQR/ViewModels/` (MainViewModel, LoginViewModel, ConfigEditorViewModel, HistoryViewModel, AccountsViewModel, PlcSettingsViewModel, DevViewModel, CodeLookupViewModel, ProductionInfoViewModel, ProductionOrdersViewModel, VirtualKeyboardViewModel).
- DI registration: `TanTienQR/Services/TanTienAppServices.cs` (`AddTanTienServices`).
- Role/permission gating: `MainViewModel` exposes `IsRoleWorker/IsRoleOperator/IsRoleAdministrator/IsRoleDeveloper`, `CanManageUsers`, and `ISessionContext` from `ClassLibraryMT.Auth`.

## Harness design (build it as a NEW project — do not modify the app)
Create a **new standalone console project** OUTSIDE the app's source tree, e.g. `d:\DuAn\VisionF\tools\UiAuditHarness\`
- `UiAuditHarness.csproj`: `net10.0`, references `Avalonia` 12.1.4, `Avalonia.Headless` 12.1.4, `Avalonia.Skia` 12.1.4, `Avalonia.Controls.WebView` 12.1.1 (needed because the app uses WebView and `Program.cs` pulls it in), plus `ProjectReference` to `..\..\TanTienQR\TanTienQR.csproj`.
- Referencing the app project gives access to `TanTienQR.App`, `TanTienQR.Program`, and all Views/ViewModels.
- **Sandbox isolation**:
  - Copy the built app output (`TanTienQR\bin\Debug\net10.0\*`) into `tools\UiAuditHarness\sandbox\` (or a temp dir) and run the harness with `AppContext.BaseDirectory`/working dir pointed there so `logs/users.db`, `logs/app.db`, `logs/plc.db`, `logs/scans.db` and `license.lic` are all sandbox-local. Alternatively set `Auth:DbPath`, `Logging:Sqlite:Path`, `PlcSettings:DbPath`, `Scans:DbPath` to absolute paths inside the sandbox via env vars (`TANTIEN_` prefix is supported by `AddEnvironmentVariables("TANTIEN_")`).
  - Disable/neutralize outbound network work so the audit never touches production: PLC (`127.0.0.1:9600`), camera (`127.0.0.1:9000`), handheld scanner TCP (`127.0.0.1:6999`), web (`https://estrace.checkee.vn`), and the PO server (`http://127.0.0.1:5080`). Prefer config overrides pointing at closed local ports. The scanner's TCP listener binds a port — make sure the harness run does not conflict with a user-running instance of the app.
  - NOTE: `Avalonia.Controls.WebView` may not render under headless mode. If `PlcSettingsView`'s webview fails or hangs, capture that as a FINDING (and guard the harness with a timeout so it cannot hang forever).
- **Driving the UI**: use `Avalonia.Headless` input simulation (`Avalonia.Headless` provides `window.MouseDown/MouseUp/KeyPress/KeyTextInput` and `HeadlessWindowExtensions`). Log in as `admin`/`tantien512`, handle the forced password-change step (set a sandbox-only password, e.g. `Sandbox123!`), and then walk every tab/section.
- **Screenshot capture**: after each navigation step, render the window to PNG at its full logical size. Save to `tools\UiAuditHarness\shots\<view>__<state>.png`. Use `RenderTargetBitmap` + `bitmap.Save(path)` (note: the string overload is obsolete — use the `BitmapEncoderOptions` overload or `Save(path, null)` is fine but prefer the non-obsolete overload to avoid warnings).
- **Also capture, for each view**: window `Bounds`, and a **visual-tree dump** of every `Control` with its `Bounds` (relative to window), `Name`, `Type`, `DesiredSize`, and `IsVisible`. This is how you detect misalignment objectively rather than by eyeballing alone.
- **Automated alignment checks** (compute, don't just eyeball):
  - Elements clipped by their parent (child bounds exceed parent's bounds).
  - Elements extending outside the window bounds.
  - Text blocks with text longer than their allotted width (use `TextBlock` desired size vs actual).
  - Misaligned sibling rows/columns (e.g. two cards in a `UniformGrid`/`Grid` with differing heights).
  - Inconsistent gaps/paddings between repeated items (compare `Margin`/`Padding` of sibling cards in the same `ItemsControl`).
  - Grid rows/columns whose `Height`/`Width` is `Auto` but content overflows, or fixed sizes that are too small for the content.
- Finally, **read each captured PNG with the image-reading tool** and visually inspect for: overlapping elements, cut-off Vietnamese diacritics, truncated text, inconsistent spacing, misaligned columns, odd empty areas, stretched/collapsed controls, broken cards.

## What to deliver (the deliverable is a REPORT, not code changes)
Write a Markdown report to `d:\DuAn\VisionF\docs\ui-audit-report.md` with:
- A short summary: total views inspected, total issues found, severity breakdown.
- For EACH issue: a unique ID (e.g. `UI-001`), the view + element path/name, a short severity (**Blocker / Major / Minor / Cosmetic**), a precise description of the visual defect, the **evidence** (the screenshot file name + the measured numbers from the visual-tree dump, e.g. "card A height 320 vs card B height 308"), and a **suggested fix** described in words (do NOT implement it).
- A short "Patterns / Systemic issues" section for defects that repeat across views (e.g. a shared style resource causing inconsistent padding).
- A section listing what could NOT be audited and why (e.g. WebView content under headless).
Keep the report in **Vietnamese**, since the app UI and the user's working language are Vietnamese.
- Also save the screenshots into `d:\DuAn\VisionF\docs\ui-audit-shots\` so the user can open them.

## Rules
- Report findings; do NOT fix anything in this phase. The user explicitly wants to approve fixes one by one.
- If the harness hits a blocker (login impossible, DI failure, WebView hang), diagnose and document it rather than hacking the app source.
- Do not commit anything to git unless asked.
