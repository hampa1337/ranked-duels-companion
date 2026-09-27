# Ranked Duels Companion

A small Windows tray app for the **Ranked Duels** World of Warcraft addon (on CurseForge).
It uploads your duels to **[rankedduels.io](https://rankedduels.io)** automatically, so the ladder stays up to date without you uploading anything by hand.

**Download:** [rankedduels.io/me](https://rankedduels.io/me) or the [latest release](../../releases/latest).

## What it does, exactly

The whole app is in this repository. In short:

- **Reads one file:** `World of Warcraft\_retail_\WTF\Account\<ACCOUNT>\SavedVariables\Ranked Duels.lua`, the file the addon writes when you log out or type `/reload`. It reads nothing else on your PC. ([SaveFiles.cs](SaveFiles.cs))
- **Sends that file** to the Ranked Duels server (`/upload`), which picks out your duels. ([Api.cs](Api.cs))
- **Logs you in with Battle.net** in your normal browser. The app never sees your Battle.net password. It only receives a Ranked Duels login token, which it stores encrypted with Windows' own protection (DPAPI) in `%APPDATA%\Ranked Duels Companion\settings.json`. ([Settings.cs](Settings.cs))
- **Installs itself** for your Windows user only (no admin rights): `%LOCALAPPDATA%\Programs\Ranked Duels Companion`, a Start menu shortcut, an entry in *Apps & features* and, if you want, a start-with-Windows entry. ([Install.cs](Install.cs))
- **Checks for updates** by reading `https://rankedduels.io/download/version.txt`. It never downloads or runs anything by itself: it only shows an "Update available" link.

## Is the download really built from this code?

Yes, and you can check it. Every release is built by GitHub Actions from this repository ([release.yml](.github/workflows/release.yml)), never on someone's PC, and GitHub signs a **build attestation** for the `.exe`. With the [GitHub CLI](https://cli.github.com/) installed:

```
gh attestation verify RankedDuelsCompanion.exe --repo hampa1337/ranked-duels-companion
```

It confirms the file was built by this repository's workflow, from a specific commit. Each release also lists the file's SHA-256 checksum.

## "Windows protected your PC"

The app isn't code-signed yet, so Windows SmartScreen warns about an unknown publisher. Click **More info → Run anyway**. We plan to get free code signing for open-source projects, which removes the warning.

## Build it yourself

Needs Windows and the [.NET SDK](https://dotnet.microsoft.com/download) (8 or newer). The app targets .NET Framework 4.8, which ships with Windows 10 and 11, so it's a single small `.exe`.

```
dotnet build -c Release
```

The result is `bin\Release\net48\RankedDuelsCompanion.exe`.

## License

[MIT](LICENSE)
