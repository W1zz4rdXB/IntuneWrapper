# Changelog

All notable changes to Intune Wrapper. Versions follow [semantic versioning](https://semver.org/).

## [1.6.0] - 2026-11-24

### Added
- **Package contents** tab: package drivers and other files with the setup. Add files or folders (buttons or drag-and-drop on the list) and choose per item:
  - **Include only:** copied into the package; the setup uses it itself.
  - **Install INF drivers (pnputil):** `pnputil /add-driver "<folder>\*.inf" /subdirs /install` (64-bit pnputil, also when the Intune agent runs 32-bit).
  - **Run installer (silent):** a second .exe/.msi, with suggested switches you can edit.

  When something has to run, Convert writes `install.cmd` into the input folder and the Intune install command becomes `install.cmd`. It runs the setup and then each step, logs to `%ProgramData%\IntuneWrapper\Logs`, treats 0/3010/1641 (and 259 for pnputil: no matching device) as success, and stops with the failing step's exit code otherwise.
- **Capture on this PC** is now the fallback when the Sandbox install fails: the Auto-detect dialog offers **Capture on this PC instead...**. The Capture wizard (for all installer types) now:
  - warns when the app already seems to be installed;
  - checks the File detection rule against the installed app;
  - has a 4th step, **Uninstall and verify**: run the found uninstall command (administrator prompt for machine-wide apps) or uninstall via Settings > Apps, then **Verify removal** checks the entry and the program file are gone. Verified values are marked Detected.

- Additional files on the command line (`IntuneWrapper.exe <installer> [files or folders...]`) are added to the package contents. Dropping several files offers to add the extra ones.
- Open-source release under the **MIT License**, with an installation guide, user manual with screenshots, contributing guide, code of conduct, security policy and third-party notices. The release zip now includes all of them.

### Changed
- Auto-detect tests exactly what will be packaged: the prepared input folder with drivers, extra installers and `install.cmd`.
- The exe's file properties and the About dialog show the copyright and license.
- The "other files will be packaged" warning ignores items on the Package contents tab.

## [1.5.0] - 2026-11-19

### Added
- **Auto-detect in Windows Sandbox** (Program tab, Detection rules tab, Tools menu, Ctrl+D). Intune Wrapper starts a clean, disposable Windows Sandbox and:
  1. installs the app silently with the install command (as Intune would, from the installer's folder);
  2. finds the new uninstall entry and the app's main program file;
  3. builds the File detection rule and checks that it finds the app;
  4. runs the uninstall command and checks that the entry and the file are gone.

  The verified uninstall command and File detection rule are filled in, marked Detected, and the Sandbox closes by itself. Your own PC is not changed.
- When Windows Sandbox is not turned on, the dialog explains why it's needed and offers to turn it on (administrator approval and a restart).
- The Sandbox-only mode (`--sandbox-capture`) refuses to run outside Windows Sandbox.

## [1.4.0] - 2026-11-19

### Added
- File detection rules are checked like the Intune admin center does, so the portal no longer rejects pasted values:
  - **Path:** a local path starting with a drive (`C:\...`) or an environment variable (`%ProgramFiles%\...`), without `/ * ? " < > |`, not a network path.
  - **File or folder:** a name only, without `\ / : * ? " < > |`, shorter than 255 characters.
  - **Value:** must match the detection method (version like `1.2.3.4`, whole MB, or a date).
  - The reason is shown in red under the field, and the tab stays marked (!).
- **Browse...** next to File or folder: pick the installed exe (e.g. on a test machine) to fill Path, File or folder and a String (version) >= check.
- A full file path pasted into Path or File or folder is split into both fields, and surrounding quotes are removed.

### Changed
- Copy refuses empty values, placeholders such as `<PATH: use Capture mode>`, and invalid detection values, and explains why.
- Copy all asks first when required fields are missing, and marks the copied text "NOT READY FOR INTUNE".

## [1.3.0] - 2026-11-19

### Changed
- Detection rules are always **File** rules. Rule type is fixed to File in the editor, and MSI/Registry rules are no longer generated.
  - **MSI:** the main exe and its default install folder are read from the MSI's Directory, Component and File tables, e.g. `%ProgramFiles%\Contoso\App` + `app.exe`, **String (version) Greater than or equal to** the file version from the MSI. ProgramFilesFolder installs set "Associated with a 32-bit app on 64-bit clients" to Yes.
  - **Capture:** uses the exe in DisplayIcon, or the main exe in InstallLocation (skipping uninstallers and helpers), and reads its file version on the capture machine. Without an exe, the install folder itself is detected.
  - History entries with an MSI or Registry rule are restored as File rules, with the old rule in the notes.

## [1.2.0] - 2026-11-19

### Changed
- New packaging folder structure, default `D:\Packaging` (or `%USERPROFILE%\Packaging` without a D: drive):
  ```
  D:\Packaging\IntuneWinAppUtil.exe
  D:\Packaging\<Name>\input\<installer>          copy of the original installer
  D:\Packaging\<Name>\output\<installer>.intunewin  plus <Name>-commands.txt
  ```
  The program folder uses the Name from the App information tab.
- Convert copies the installer into `input` and packages that folder, so files next to the original installer (e.g. in Downloads) are never packaged. A different file with the same name in `input` is only replaced after confirmation.
- IntuneWinAppUtil.exe is kept in the packaging folder. Downloads and updates go there, a browsed copy is copied there, and a copy from an earlier version (`%APPDATA%\IntuneWrapper\tools`) is moved in automatically.
- "Output folder" is replaced by "Packaging folder" in the main window and Settings. The window shows which input and output folders this program uses.

## [1.1.0] - 2026-11-19

### Added
- All fields the Intune admin center needs for a Win32 app, on tabs that follow the "Add Win32 app" wizard:
  - **App information:** Name, Description, Publisher, App version.
  - **Program:** install/uninstall command, installation time, allow available uninstall, install behavior, device restart behavior, return codes.
  - **Requirements:** operating system architecture (x64/x86/ARM64, read from the MSI Template when available), minimum operating system.
  - **Detection rules:** rule type MSI, File or Registry, with the portal's fields and options (Path, File or folder, Detection method, Operator, Value, Key path, Value name, Associated with a 32-bit app on 64-bit clients). Fields Intune hides for a detection method are hidden too.
- Tabs with missing required fields are marked "(!)", and the warning banner lists what is still required.
- Capture fills in a File rule from the app's DisplayIcon or InstallLocation, using `%ProgramFiles%`-style paths, alongside the Registry rule. It also fills in a missing publisher or version.
- Default minimum OS in Settings.

### Changed
- The default output folder is `D:\IntuneOutput` (or `%USERPROFILE%\IntuneOutput` without a D: drive) instead of the Desktop, which is often redirected to a file server. Settings still using the old Desktop default are moved automatically.
- Copy all and the text export list every field per wizard step, using the portal's labels.
- Device restart behavior uses the portal wording ("No specific action").
- History saves and restores all portal fields. Entries from 1.0.0 still restore.
- Tool update check compares the downloaded file's content, because IntuneWinAppUtil's file version (e.g. 6.2509.50) does not match its GitHub release tag (e.g. v1.8.7).

## [1.0.0] - 2026-11-19

### Added
- Drag-and-drop or Browse for a single `.exe` or `.msi`; folders, other file types and multiple files are handled with clear messages.
- MSI: product code, name, version and manufacturer read from the Property table (read-only, handles released). Install, uninstall and detection marked **Detected**.
- EXE: framework detection for Inno Setup, NSIS, InstallShield, WiX Burn (`.wixburn` section) and Squirrel, with framework switches marked **Suggested**. Unknown installers get **Unknown** placeholders.
- install4j detection (`-q`), beyond the original specification.
- Every field is editable, with a confidence badge (Detected / Suggested / Unknown / Edited). The uninstall command is never left empty.
- Capture uninstall wizard: registry snapshot before and after installing, then pick the new entry. Fills the uninstall command (QuietUninstallString, `msiexec /x`, or UninstallString with silent switches) and a registry or MSI detection rule.
- Conversion with IntuneWinAppUtil.exe in the background, with Cancel, a log pane, overwrite confirmation (the old package is kept on No or failure), an output-exists check even on exit code 0, and a warning when the source folder holds other files.
- IntuneWinAppUtil.exe setup: browse, download the latest release from GitHub, and check for updates.
- Copy buttons, Copy all, export to `<name>-commands.txt`.
- History of the last 50 conversions with restore and delete. Corrupt history and settings files are reset with a backup.
- Settings, About dialog, daily logs (14 days) under `%APPDATA%\IntuneWrapper\logs`.
- Command-line argument: `IntuneWrapper.exe <installer>` loads the file at startup.
