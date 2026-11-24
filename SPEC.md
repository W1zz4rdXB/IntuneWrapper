# Intune Wrapper: Application Specification

> **Decisions record (Section 13, confirmed 2026-11-19)**
>
> | Decision | Choice |
> |---|---|
> | Language | **C# WinForms on .NET Framework 4.8** (not PowerShell + PS2EXE). SDK-style project built with the .NET 8 SDK; ships as one `IntuneWrapper.exe` with no extra DLLs. |
> | Code signing | Unsigned by default; `build.ps1 -CertThumbprint` signs when a certificate is available. Checksum + README guidance always. |
> | Tool bundling | Download on demand from GitHub. |
> | Capture mode | Included in v1. |
> | Intune upload | Out of scope for v1. |
>
> **1.1.0 changes (2026-11-19):** FR-10 default output folder is `D:\IntuneOutput` (fallback `%USERPROFILE%\IntuneOutput`), never the Desktop, because redirected Desktops write to a file server. FR-12 is extended to every field of the Intune "Add Win32 app" wizard (App information, Program, Requirements, structured Detection rules with MSI / File / Registry types and the portal's fields), and FR-27 "Copy all" lists them per wizard step with portal labels.
>
> **1.2.0 changes (2026-11-19):** FR-7 and FR-10 replaced by a packaging folder (default `D:\Packaging`) holding `IntuneWinAppUtil.exe` and, per program (folder named after the app Name), `input\` (copy of the installer, which is what gets packaged) and `output\` (.intunewin and exported text). FR-26 now applies to the input folder.
>
> **1.3.0 changes (2026-11-19):** detection rules are always File rules (Section 5 detection and FR-19): the main exe from the MSI file tables, or from DisplayIcon/InstallLocation after Capture, with a String (version) >= file version check when the version is known.
>
> Because of the language change, Sections 9 and 10 are implemented in their C# equivalents: PowerShell modules become classes in `src/IntuneWrapper/Core` (no UI code), Pester tests become xUnit tests, and PS2EXE becomes `dotnet build`. Section 12's PowerShell-specific pitfalls (`$args`, `-STA`) map to `[STAThread]` and an explicit argument quoting helper.

This document describes a Windows application that packages installers for Microsoft Intune and presents the install and uninstall commands. It also records decisions and pitfalls from the prototype so they aren't repeated.

## 1. Purpose and scope

Intune Wrapper lets an IT colleague drop an .exe or .msi installer onto a window. The app:

- Packages the installer into a .intunewin file using IntuneWinAppUtil.exe.
- Determines the silent install command and the uninstall command.
- Presents both commands clearly with copy buttons, plus the detection information Intune needs.

The app is distributed as a single IntuneWrapper.exe that colleagues can run without installing anything by hand.

**In scope for v1:** single-file conversion, command presentation and editing, MSI and common EXE framework detection, uninstall capture by registry diff, history, settings, and a packaged executable.

**Out of scope for v1:** batch conversion, direct upload to Intune through Graph API, code signing automation, macOS or Linux support.

## 2. Users and environment

- Users: IT administrators and packagers, some not PowerShell experts. The UI must be usable without documentation.
- Target OS: Windows 10 1809 or later, and Windows 11.
- Privileges: runs as a standard user. Conversion does not need admin. Capture mode may need admin for HKLM reads; the app detects that and shows a clear message instead of failing silently.
- Network: optional, only to download IntuneWinAppUtil.exe from GitHub. Works fully offline once the tool is present.

## 3. Distribution requirements

- Deliverable: one executable, IntuneWrapper.exe, plus README.md and CHANGELOG.md.
- Must run where PowerShell scripts are blocked by execution policy.
- Settings and history live under `%APPDATA%\IntuneWrapper\`, never the install folder, so it works from a read-only share.
- Semantic versioning (1.0.0). Version appears in the window title, the About dialog, and the exe file properties.
- "About" shows version, tool path, and a link to the packaging tool's GitHub repo.

## 4. Functional requirements

### 4.1 Input
- **FR-1** Accept one file by drag and drop onto the window or the drop zone. Folders are not supported in v1: show a clear error.
- **FR-2** Browse button opens a file dialog filtered to `*.exe;*.msi`.
- **FR-3** Accepted extensions are .exe and .msi. Other files show an error.
- **FR-4** If several files are dropped, use the first and show a status note that the others were ignored.
- **FR-5** On load, read and display metadata: name, size, product name, version, publisher.

### 4.2 Tool management
- **FR-6** On startup, check the saved IntuneWinAppUtil.exe path. If missing, show a dialog: Browse, Download latest from GitHub, or Cancel.
- **FR-7** Download uses the GitHub API to find the latest release of `microsoft/Microsoft-Win32-Content-Prep-Tool` and saves IntuneWinAppUtil.exe to `%APPDATA%\IntuneWrapper\tools\`. Show progress; handle no network, rate limit, no asset found.
- **FR-8** "Check for tool update" compares the local file version with the latest release and offers to update.
- **FR-9** The app runs without the tool present (setup dialog instead of a crash).

### 4.3 Output
- **FR-10** Output folder defaults to `%USERPROFILE%\Desktop\IntuneOutput`, changeable with Browse, created if missing.
- **FR-11** If a .intunewin with the same name exists, ask before overwriting. No cancels and keeps the old file.

### 4.4 Command generation and presentation (core feature)
- **FR-12** After a file loads, generate and display without further clicks: install command, uninstall command, install behavior (System/User, default System), restart behavior (default Suppress), detection rule (MSI product code or registry key/value), notes with confidence and warnings.
- **FR-13** Every generated field shows a confidence indicator: Detected, Suggested (verify), or Unknown (placeholder to fill in).
- **FR-14** Every field is editable. Edits are kept in the session and used in copy operations and history.
- **FR-15** Each command has a Copy button. "Copy all" copies a formatted summary (4.7).
- **FR-16** The uninstall command is never empty. If unknown it holds a clearly marked placeholder such as `<UNINSTALL COMMAND: use Capture mode>` and the status shows a warning.

### 4.5 Uninstall capture mode (EXE only)
- **FR-17** "Capture uninstall": snapshot the uninstall registry keys; user installs the app on a test machine/sandbox and clicks "Finish capture"; second snapshot; diff for new or changed entries.
- **FR-18** From new entries, offer QuietUninstallString (preferred) or UninstallString. With several entries, the user picks one.
- **FR-19** Capture also fills the detection rule (registry key and DisplayVersion).
- **FR-20** Capture is labelled as reading the local machine: it must run on a clean or test machine.

### 4.6 Conversion
- **FR-21** Run IntuneWinAppUtil.exe with `-c` source folder, `-s` setup file, `-o` output folder, `-q` quiet; overwrite only after confirmation (FR-11).
- **FR-22** Conversion runs in the background with progress indicator and Cancel.
- **FR-23** Tool stdout/stderr appear in a collapsible log pane.
- **FR-24** On success show path and size of the .intunewin, with "Open folder" and "Copy path".
- **FR-25** On failure show exit code and a plain-language explanation; keep the log visible.
- **FR-26** If the source folder contains other files, warn that all of them will be packaged and offer to open the folder first.

### 4.7 Copy and export
- **FR-27** "Copy all" format:
  ```
  Application:    <name> <version>
  Package:        <file name>.intunewin
  Install:        <install command>
  Uninstall:      <uninstall command>
  Install context: System
  Restart:        Suppress
  Detection:      <rule summary>
  ```
- **FR-28** Optional export to `<name>-commands.txt` in the output folder: same content plus notes.

### 4.8 History
- **FR-29** Keep the last 50 conversions in `%APPDATA%\IntuneWrapper\history.json`: timestamp, file name, SHA-256, output path, commands, success.
- **FR-30** History window lists entries; selecting one restores its commands; entries can be deleted.
- **FR-31** Corrupt or missing history is reset quietly, with a backup of the bad file.

### 4.9 Settings
- **FR-32** Tool path, default output folder, default install context, show log pane by default.
- **FR-33** Saved as JSON in `%APPDATA%\IntuneWrapper\settings.json`; missing keys get defaults.

## 5. Command generation rules

### 5.1 MSI
- Install: `msiexec /i "<file>.msi" /qn /norestart`
- Uninstall: `msiexec /x {PRODUCT-CODE} /qn /norestart`
- ProductCode, ProductName, ProductVersion, Manufacturer read from the Property table, database opened read-only. Handles released after use.
- Detection: MSI product code. Confidence: Detected.

### 5.2 EXE framework detection (priority order)

| Framework | Signature | Install | Uninstall pattern |
|---|---|---|---|
| Inno Setup | `Inno Setup` | `"<file>" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | UninstallString + `/VERYSILENT /NORESTART` |
| NSIS | `Nullsoft Install System` | `"<file>" /S` | UninstallString + `/S` |
| InstallShield | `InstallShield` | `"<file>" /s /v"/qn /norestart"` | UninstallString + `/s /v"/qn"` |
| WiX / Burn | `.wixburn` PE section / full Burn marker | `"<file>" /quiet /norestart` | QuietUninstallString |
| Squirrel | `Squirrel` and `Update.exe` | `"<file>" --silent` | QuietUninstallString |
| Unknown | none | `"<file>" /S` (Unknown) | Capture mode |

Rules: use specific markers (never plain `WiX|Burn`); a framework match gives Suggested confidence, not Detected; uninstall needs the real registry entry, so point the user to Capture mode.

### 5.3 Version and metadata
FileVersionInfo ProductName, ProductVersion, CompanyName, FileDescription for EXE; fall back to the file name without extension.

### 5.4 Registry locations
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` (64-bit and 32-bit/WOW6432Node views) and `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`. Relevant values: DisplayName, DisplayVersion, UninstallString, QuietUninstallString, key path.

Diff: new = key path absent from first snapshot; changed = DisplayVersion or UninstallString differs. Show both; default to new.

## 6. User interface

### 6.1 Main window (top to bottom)
- Drop zone: large dashed panel, "Drop an installer here (.exe or .msi)", Browse button, colour change when a valid file is dragged over.
- File summary: name, size, product, version, publisher, coloured type badge (MSI, EXE: Inno Setup, EXE: Unknown...).
- Commands panel: install (text, Copy, badge); uninstall (text, Copy, badge, Capture button when relevant); install context dropdown; restart dropdown; detection (text, Copy); notes (read-only).
- Actions: Convert, Copy all, Export txt, Open output folder, History.
- Collapsible log pane. Status bar: state, tool version, output folder.
- Resizable, minimum 760x640. Segoe UI 10 pt, monospace selectable command boxes. Enter never triggers Convert. Badges use colour and text.

### 6.2 Dialogs
Tool setup, capture wizard (3 steps, Cancel at each), overwrite confirmation, history, settings, about.

### 6.3 Accessibility
Keyboard access for all actions, logical tab order, tooltips on every button and badge, plain-language errors with technical detail in the log.

## 7. Error handling

| Situation | Behaviour |
|---|---|
| Tool missing | Setup dialog (FR-6) |
| Tool download fails | Message with reason and link to the releases page |
| Unsupported file type | Error, no state change |
| Source file locked/unreadable | Error with path, suggest closing the program using it |
| Non-zero exit code | Exit code, last 20 log lines, possible causes |
| .intunewin not created | Failure even if exit code is 0 |
| MSI product code unreadable | Fall back to Unknown, placeholder |
| Output folder not writable | Error before starting conversion |
| Paths with spaces/special chars | Always quoted |
| Unexpected exception | Caught at top level, full detail to `%APPDATA%\IntuneWrapper\logs\`, friendly message |

The app never closes without explanation.

## 8. Logging
`%APPDATA%\IntuneWrapper\logs\app-YYYY-MM-DD.log`, one per day, keep 14 days. Log startup/version, tool path/version, file loaded (name and hash), commands generated, conversion arguments and exit code, errors with stack traces. Never log file contents or credentials.

## 9. Technical design (C# implementation)
- .NET Framework 4.8, WinForms, `[STAThread]`. Long operations (analysis, conversion, download, capture) run on background tasks; UI updates via the WinForms synchronization context.
- `src/IntuneWrapper/Core`: logic classes with no WinForms references (AppPaths, Logger, Settings, InstallerInspector, MsiReader, CommandGenerator, CommandLine, Packager, ToolManager, UninstallRegistry, History, Summary). `src/IntuneWrapper/UI`: forms only.
- `tests/IntuneWrapper.Tests`: xUnit tests for Core.
- No third-party runtime dependencies (JSON via DataContractJsonSerializer), so the build output is a single exe.

## 10. Build and distribution
- `build/build.ps1`: run tests, build Release, copy exe to `dist/`, write `IntuneWrapper.exe.sha256`, zip exe + checksum + README. Optional `-CertThumbprint` signs with signtool.
- Version lives in `Directory.Build.props` only.
- Unsigned binaries may trigger SmartScreen: sign with an internal certificate (recommended), allowlist, or verify checksum per README.
- Tag releases as `v1.0.0`.

## 11. Testing
Automated: MSI command generation, each framework signature, Unknown EXE placeholder, argument quoting (spaces, apostrophes, parentheses, trailing backslash), registry diff, settings missing/corrupt, history corrupt reset.

Manual acceptance (clean Win10 + Win11): tool-missing setup + download; MSI drop; Inno drop (Suggested + Capture placeholder); Capture on test machine; convert with spaces; overwrite prompt No keeps file; edited command in Copy all; offline download error; read-only share; standard user (capture reports admin need when relevant).

## 12. Known prototype issues (do not repeat)
Specific framework markers; confidence on every suggestion; uninstall placeholder always with a visible warning; check the output file exists even on exit code 0; always quote paths; STA thread for clipboard and drag-drop; release MSI handles.
