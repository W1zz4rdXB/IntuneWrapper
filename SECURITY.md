# Security policy

## Supported versions

Security fixes are made for the **latest release** only. Please update before reporting.

## Reporting a vulnerability

**Do not report security problems in public issues, discussions or pull requests.**

Report them privately with GitHub's **[Report a vulnerability](https://github.com/W1zz4rdXB/IntuneWrapper/security/advisories/new)** (repository **Security** tab). Only the maintainers can see these reports.

Please include:

- the affected version (**Help > About**) and your Windows version;
- a description of the problem and its impact;
- steps to reproduce, or a proof of concept;
- any suggested fix.

We will acknowledge your report as soon as possible, keep you informed while we work on a fix, and credit you in the release notes, unless you prefer to stay anonymous. Please give us a reasonable time to release a fix before you disclose the problem publicly.

## Scope

Examples of what we consider security issues:

- Intune Wrapper executing code or commands it shouldn't (for example the `--sandbox-capture` mode running outside Windows Sandbox);
- generated `install.cmd` scripts or command lines that could be abused through crafted file names or installer metadata;
- the download of `IntuneWinAppUtil.exe` being redirectable to a different file;
- sensitive data written to logs, settings or exports.

Out of scope: vulnerabilities in Microsoft's `IntuneWinAppUtil.exe`, Windows, Windows Sandbox or Intune itself (report those to Microsoft), and the behaviour of the installers you package.

## Good practice for users

- Download releases only from the [official releases page](https://github.com/W1zz4rdXB/IntuneWrapper/releases), and verify the SHA-256 checksum (see the [Installation guide](docs/INSTALLATION.md#3-verify-the-download-recommended)).
- Prefer a build signed by your organisation.
- Package only installers from trusted sources, and test every package on a test device before deploying it.
- Auto-detect runs installers inside Windows Sandbox. **Capture on this PC** runs them on your own PC: use a test machine for unknown software.
