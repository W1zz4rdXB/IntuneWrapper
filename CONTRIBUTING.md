# Contributing to Intune Wrapper

Thank you for helping! Bug reports, ideas, documentation fixes and code are all welcome. By contributing, you agree that your contribution is licensed under the project's [MIT License](LICENSE), and you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Reporting bugs and asking for features

Open an issue at [https://github.com/W1zz4rdXB/IntuneWrapper/issues](https://github.com/W1zz4rdXB/IntuneWrapper/issues). For a bug, please include:

- the Intune Wrapper version (**Help > About**) and your Windows version;
- the installer type and framework shown in the *Installer* box (for example *EXE: Inno Setup*);
- what you did, what you expected, and what happened instead;
- the relevant part of today's log (**View > Open log folder**). Logs contain file names and paths, but never file contents: remove anything you consider private before posting.

**Security problems** are never reported in public issues: see [SECURITY.md](SECURITY.md).

## Building from source

### Requirements

- Windows 10/11
- [.NET SDK](https://dotnet.microsoft.com/download) 8 or later (it builds the .NET Framework 4.8 project; the reference assemblies come from NuGet)
- Optional: Windows SDK `signtool.exe`, for signing

### Build and test

```powershell
git clone https://github.com/W1zz4rdXB/IntuneWrapper.git
cd IntuneWrapper
dotnet test                        # unit tests (about 150, under a minute)
dotnet build -c Release            # src\IntuneWrapper\bin\Release\net48\IntuneWrapper.exe
```

The integration test downloads the real `IntuneWinAppUtil.exe` from GitHub and packages a test file:

```powershell
$env:INTUNEWRAPPER_INTEGRATION = '1'; dotnet test
```

### Release package

```powershell
powershell -ExecutionPolicy Bypass -File build\build.ps1 [-Integration] [-CertThumbprint <thumbprint>]
```

This runs the tests, builds Release, and creates in `dist\`: `IntuneWrapper.exe`, `IntuneWrapper.exe.sha256` and `IntuneWrapper-<version>.zip` (exe, checksum, license, notices, changelog, README and `docs\`).

### Signing a build

Pass the thumbprint of a code-signing certificate in your certificate store:

```powershell
powershell -ExecutionPolicy Bypass -File build\build.ps1 -CertThumbprint 0123456789ABCDEF...
```

The exe is signed with SHA-256 and a timestamp **before** the checksum is computed.

## Project layout

```
src/IntuneWrapper/Core    Logic without UI: installer inspection, command generation, Intune portal fields
                          and validation, packaging, tool download, registry capture, Sandbox automation,
                          package items and install.cmd, history, settings, logging
src/IntuneWrapper/UI      WinForms windows and dialogs
tests/                    xUnit tests for Core
build/build.ps1           Test, build, checksum, zip, optional signing
build/make-icon.ps1       Regenerates assets/icon.ico
docs/                     Installation guide, user manual, screenshots
SPEC.md                   Original specification and decision record
```

## Guidelines

- **Keep the logic in `Core`, without WinForms.** Everything there should be testable without opening a window.
- **Add or update tests** for every change in `Core`. Run `dotnet test` before opening a pull request.
- **No runtime dependencies.** The release must stay a single `IntuneWrapper.exe` on .NET Framework 4.8 (JSON uses the built-in `DataContractJsonSerializer`).
- **Never guess silently.** Every generated value carries a `Confidence` (Detected / Suggested / Unknown), and placeholders must stay recognisable (`<UPPERCASE: ...>`) so they can't be copied into Intune.
- **Intune wording.** Field labels and options mirror the Intune admin center exactly (see `Core/IntunePortal.cs`).
- **Quote paths** in command lines (see `Core/CommandLine.cs`); never build them with plain string concatenation.
- **Safety first.** Code that installs, uninstalls or shuts down must only run where it is meant to (the Sandbox agent refuses to run outside Windows Sandbox).
- Style: C# with the existing conventions (4 spaces, braces on new lines, `_camelCase` fields). See `.editorconfig`.
- Version: change it only in `Directory.Build.props`, and add an entry to `CHANGELOG.md`.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Make your change, with tests, documentation and a changelog entry.
3. Check that `dotnet test` passes.
4. Open a pull request that describes **what** changed and **why**. Add screenshots for UI changes.

Maintainers review pull requests as time allows. Small, focused pull requests are reviewed fastest.

## Documentation screenshots

The screenshots in `docs/images` use neutral demo installers ("Contoso Notes"), never real customer software or personal paths. Please keep it that way.
