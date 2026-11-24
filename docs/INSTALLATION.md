# Intune Wrapper: Installation guide

This guide covers downloading, verifying and starting Intune Wrapper, setting up Microsoft's packaging tool and (optionally) Windows Sandbox, and updating or removing the tool. For day-to-day use, see the [User manual](USER_MANUAL.md).

Intune Wrapper is **portable**: there is no installer. You extract one `.exe` and run it.

---

## 1. System requirements

| | Requirement |
|---|---|
| Operating system | Windows 10 version 1809 or later, or Windows 11 (64-bit recommended) |
| .NET | .NET Framework 4.8. It is built into Windows 10 1903 and later and Windows 11. On Windows 10 1809, install it from Microsoft first. |
| Rights | Standard user. No administrator rights are needed to run or package. |
| Disk | About 1 MB for the tool, plus room for your packages (roughly the size of each installer, twice). |
| Network | Only to download `IntuneWinAppUtil.exe` once (see [Network access](#7-network-access)). |
| Optional: Auto-detect | Windows Sandbox: Windows 10/11 **Pro, Enterprise or Education**, hardware virtualization enabled in the BIOS/UEFI, at least 4 GB RAM (8 GB recommended). Not available on Windows Home. |

To check your .NET Framework version, run this in PowerShell:

```powershell
(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release -ge 528040
```

`True` means .NET Framework 4.8 or later is installed.

---

## 2. Download

Download the latest release zip, `IntuneWrapper-<version>.zip`, from the [releases page](https://github.com/W1zz4rdXB/IntuneWrapper/releases/latest). It contains:

| File | Purpose |
|---|---|
| `IntuneWrapper.exe` | The application, the only file needed to run it |
| `IntuneWrapper.exe.sha256` | Checksum to verify the download |
| `README.md`, `LICENSE`, `CHANGELOG.md`, `THIRD-PARTY-NOTICES.md` | Project information |
| `docs\` | This guide and the user manual, with screenshots |

---

## 3. Verify the download (recommended)

Check that the file was not changed or damaged on the way. In PowerShell, in the extracted folder:

```powershell
(Get-FileHash .\IntuneWrapper.exe -Algorithm SHA256).Hash
Get-Content .\IntuneWrapper.exe.sha256
```

The two hashes must be the same (upper or lower case does not matter). If they differ, delete the files and download again.

---

## 4. Unblock and start

1. Extract the zip to a folder of your choice, for example `C:\Tools\IntuneWrapper` or a network share. Intune Wrapper never writes next to the exe, so a **read-only share works**.
2. Windows marks downloaded files as "from the internet". To remove that mark, right-click `IntuneWrapper.exe` > **Properties**, tick **Unblock**, and click **OK**. Or in PowerShell:
   ```powershell
   Unblock-File .\IntuneWrapper.exe
   ```
3. Double-click `IntuneWrapper.exe`.

### "Windows protected your PC" (SmartScreen)

Release builds are not code-signed unless the publisher of your copy signed them, so SmartScreen may show this warning the first time. Once you have verified the checksum (step 3), click **More info > Run anyway**.

In an organisation, the cleanest options are:

- **sign the exe** with your organisation's code-signing certificate (see [Signing](../CONTRIBUTING.md#signing-a-build) in the contributing guide), or
- ask your security team to **allow it** in your antivirus, AppLocker or WDAC policy, by file hash or by your signature.

---

## 5. First start: the packaging tool

Intune Wrapper uses Microsoft's **Win32 Content Prep Tool** (`IntuneWinAppUtil.exe`) to create `.intunewin` files. It is Microsoft software and is not included. On first start this dialog appears:

| Button | What it does |
|---|---|
| **Download latest** | Downloads the latest release from [github.com/microsoft/Microsoft-Win32-Content-Prep-Tool](https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool) into the packaging folder |
| **Browse...** | Uses a copy you already have; it is copied into the packaging folder |
| **Cancel** | Continue without it: you can review commands, but **Convert** stays unavailable until the tool is set up |

The tool is always kept in the **packaging folder**, `D:\Packaging\IntuneWinAppUtil.exe` by default. Later you can use **Tools > Check for tool update** to compare your copy with the latest release.

### Packaging folder

All packages go into one folder, with a subfolder per app:

```
D:\Packaging\
├── IntuneWinAppUtil.exe
└── <App name>\
    ├── input\      copy of the installer (and drivers/extra files) - this is what gets packaged
    └── output\     the .intunewin package and the exported settings text
```

The default is `D:\Packaging` when the PC has a local D: drive, and `%USERPROFILE%\Packaging` otherwise. The Desktop is never used, because it is often redirected to a file server. You can change the folder in the main window or under **File > Settings**.

---

## 6. Optional: turn on Windows Sandbox (for Auto-detect)

**Auto-detect** installs the app in Windows Sandbox: a clean, disposable copy of Windows. It finds the uninstall command and detection rule there without changing your PC. Turn Sandbox on once per PC. This needs administrator approval and a restart.

**From Intune Wrapper:** press **Ctrl+D** (Auto-detect). If Sandbox is off, the dialog offers **Turn on Windows Sandbox...**. Approve the prompt, then restart.

**From Windows:** Start > *Turn Windows features on or off* > tick **Windows Sandbox** > **OK**, then restart.

**From an elevated PowerShell:**

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
```

If Sandbox does not start, check:

- **Virtualization** is enabled in the BIOS/UEFI. Task Manager > Performance > CPU should show "Virtualization: Enabled".
- Your edition is **Pro, Enterprise or Education**. Home is not supported.
- Your organisation hasn't disabled it by policy.
- Only **one** Sandbox runs at a time: close any open Sandbox window.

Without Sandbox, you can still use **Capture on this PC** (see the user manual).

---

## 7. Network access

Intune Wrapper only goes online when you download or update `IntuneWinAppUtil.exe`. It then contacts:

- `https://api.github.com` (latest release information)
- `https://raw.githubusercontent.com` and `https://github.com` (the file itself)

It sends **no telemetry** and no data about your installers. Behind a proxy that blocks these addresses, download `IntuneWinAppUtil.exe` manually from the GitHub page above and use **Browse...**.

---

## 8. Where Intune Wrapper stores data

| Location | Contents |
|---|---|
| `%APPDATA%\IntuneWrapper\settings.json` | Your settings |
| `%APPDATA%\IntuneWrapper\history.json` | The last 50 conversions |
| `%APPDATA%\IntuneWrapper\logs\` | Daily logs, kept 14 days (no file contents or passwords) |
| Packaging folder (e.g. `D:\Packaging`) | The packaging tool, installers and packages |
| `%TEMP%\IntuneWrapper\sandbox-*` | Working folders of Auto-detect runs (safe to delete) |

On devices that install a package with drivers or extra installers, the generated `install.cmd` logs to `%ProgramData%\IntuneWrapper\Logs\<App>-install.log`.

---

## 9. Distributing to colleagues

- **Network share:** copy `IntuneWrapper.exe` to a share. Everyone runs it from there and keeps their own settings in `%APPDATA%`.
- **Intune:** you can package Intune Wrapper with itself, using these values:

  | Field | Value |
  |---|---|
  | Install command | `cmd /c mkdir "%ProgramFiles%\IntuneWrapper" & copy /y IntuneWrapper.exe "%ProgramFiles%\IntuneWrapper\"` |
  | Uninstall command | `cmd /c rmdir /s /q "%ProgramFiles%\IntuneWrapper"` |
  | Detection | File, `%ProgramFiles%\IntuneWrapper`, `IntuneWrapper.exe`, String (version), Greater than or equal to the version |

  Add a Start menu shortcut in your own script if you need one.
- Provide the `.sha256` file along with it, and sign the exe if you can.

---

## 10. Updating

1. Close Intune Wrapper.
2. Replace `IntuneWrapper.exe` with the new version (verify and unblock it as above).
3. Start it. Settings, history and packages are kept. Older settings are upgraded automatically.

See the [Changelog](../CHANGELOG.md) for what changed.

---

## 11. Removing Intune Wrapper

1. Delete `IntuneWrapper.exe` (and the extracted folder).
2. Optional: delete `%APPDATA%\IntuneWrapper` (settings, history, logs).
3. Optional: delete the packaging folder (your packages!) and `%TEMP%\IntuneWrapper`.
4. Optional: turn Windows Sandbox off again under *Turn Windows features on or off*.

Nothing is written to the registry, and there is no uninstaller to run.

---

## 12. Installation troubleshooting

| Problem | Solution |
|---|---|
| Nothing happens, or ".NET Framework" error on start | Install .NET Framework 4.8 (see [section 1](#1-system-requirements)) |
| "Windows protected your PC" | Verify the checksum, then *More info > Run anyway*, or sign/allow-list the exe |
| Blocked by antivirus, AppLocker or WDAC | Ask your security team to allow the file hash or your signature |
| Download of IntuneWinAppUtil.exe fails | Check the proxy and firewall ([section 7](#7-network-access)), or download it manually and use **Browse...** |
| "The output folder is not writable" | Choose a packaging folder you can write to (**File > Settings**) |
| Windows Sandbox missing or won't start | See [section 6](#6-optional-turn-on-windows-sandbox-for-auto-detect) |
| Something else | **View > Open log folder** and look at today's `app-YYYY-MM-DD.log` |
