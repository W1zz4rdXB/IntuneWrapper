# Third-party notices

Intune Wrapper is licensed under the [MIT License](LICENSE). This file lists third-party software it uses or works with, and how.

## Included in the release (IntuneWrapper.exe)

**None.** The release is a single executable that uses only the .NET Framework 4.8 and Windows, which are part of the operating system.

## Downloaded on request, not included

### Microsoft Win32 Content Prep Tool (IntuneWinAppUtil.exe)

- Source: <https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool>
- Owner: Microsoft Corporation
- License: Microsoft's own license terms, published in that repository

Intune Wrapper does **not** redistribute `IntuneWinAppUtil.exe`. When the user chooses *Download latest* or *Check for tool update*, it downloads the file directly from Microsoft's GitHub repository to the user's packaging folder. Use of the tool is subject to Microsoft's license terms.

## Used by Windows features

- **Windows Installer** (`msi.dll`): reads MSI databases, read-only.
- **Windows Sandbox**: used by Auto-detect when the user has turned the feature on.
- **pnputil.exe**: called by generated `install.cmd` scripts on target devices to install drivers.

These are components of Windows and are not distributed with Intune Wrapper.

## Build and test only (not distributed)

| Package | License |
|---|---|
| Microsoft.NETFramework.ReferenceAssemblies | MIT |
| Microsoft.NET.Test.Sdk | MIT |
| xunit, xunit.runner.visualstudio | Apache 2.0 |

## Trademarks

Microsoft, Windows, Intune and Microsoft Endpoint Manager are trademarks of the Microsoft group of companies. Intune Wrapper is an independent open-source project, not affiliated with or endorsed by Microsoft. Other product names, such as the installer frameworks Inno Setup, NSIS, InstallShield, WiX, Squirrel and install4j, belong to their respective owners and are mentioned only to describe compatibility.
