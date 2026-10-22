# Magic Keyboard Globe/Fn Filter Driver

The Magic Keyboard exposes the Globe/Fn key as a vendor-specific bit in its HID keyboard report. On Windows, the main keyboard HID collection is owned by the system keyboard stack, so the user-mode bridge often cannot read that raw bit. The filter driver in `src/AppleKeyboardFilter.Driver` sits below HIDClass for matching Apple keyboards and converts the hidden Globe/Fn bit into standard HID F23.

The app already treats F23/F24 as the configurable Globe/Fn action. Emitting F23 in the driver is intentional: it lets the app map Globe/Fn to Control while the real Apple Control key can still be mapped to Windows.

## Driver Settings

The driver reads these optional `REG_DWORD` values from `HKLM\SYSTEM\CurrentControlSet\Services\AppleKeyboardFilter` when it loads. Reconnect the keyboard or restart Windows after changing them.

| Value | Default | Effect |
| --- | --- | --- |
| `EmitFnAsF23` | `1` | Set to `0` to pass Globe/Fn through untouched. |
| `GlobeHidCode` | `0x72` (F23) | HID keyboard usage emitted while Globe/Fn is held. |
| `LockHidCode` | `0` (off) | HID keyboard usage emitted while the Lock/Touch ID key is held. |

Some keyboards only declare key usages up to `0x65`; this has been reported for the Touch ID Magic Keyboard (PID `0320`). Windows drops F23 on those, so Globe/Fn does nothing. Set `GlobeHidCode` to `0x65`, the Application/Menu key, which Apple keyboards do not have. The app treats `0x64`, `0x65`, `0x72`, and `0x73` as Globe/Fn and applies the Globe/Fn setting to them. With `0x65`, a Menu key on another keyboard also acts as Globe/Fn while the app is running, and Globe/Fn acts as a Menu key when the app is not running.

`LockHidCode` does not need the app: set it to the usage the key should produce, for example `0x4C` for Delete. The Lock bit (`0x08` in the vendor byte) has been reported on the Touch ID Magic Keyboard.

From elevated PowerShell:

```powershell
$service = "HKLM:\SYSTEM\CurrentControlSet\Services\AppleKeyboardFilter"
Set-ItemProperty $service -Name GlobeHidCode -Value 0x65 -Type DWord
Set-ItemProperty $service -Name LockHidCode -Value 0x4C -Type DWord
```

## Supported Hardware IDs

The INF targets these Apple keyboard IDs:

- Bluetooth Magic Keyboard USB-C: `BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&0320`
- USB-C Magic Keyboard: `USB\VID_05AC&PID_0321&MI_01`
- Legacy Magic Keyboard A1644: Bluetooth PID `0267`, USB PID `0267`
- Magic Keyboard with Numeric Keypad: Bluetooth PID `026C`, USB PID `026C`

## Build

Install Visual Studio Build Tools plus the Windows Driver Kit, or use the Enterprise WDK. Microsoft documents that the EWDK is a standalone command-line driver build environment that includes Visual Studio Build Tools, the SDK, and the WDK. Then build:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-keyboard-filter-driver.ps1 -Platform x64
```

The script writes a package to:

```text
artifacts\keyboard-filter\AMD64
artifacts\keyboard-filter\AppleKeyboardFilterDriver.zip
```

For ARM64:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-keyboard-filter-driver.ps1 -Platform ARM64
```

## Signing

Normal Windows 11 systems require a trusted kernel driver catalog. Do not ship or install an unsigned public build. Sign the package through Microsoft attestation or HLK signing, or provide a certificate thumbprint for a local trusted signing flow:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-keyboard-filter-driver.ps1 -Platform x64 -CertificateThumbprint <thumbprint>
```

The installer and `scripts\install-keyboard-filter-driver.ps1` verify the driver catalog signature before installing unless `-AllowTestSigned` is explicitly supplied for a development machine.

Verify a signed package before bundling it:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify-keyboard-driver-package.ps1 -ZipPath .\artifacts\keyboard-filter\AppleKeyboardFilterDriver.zip -RequireAllArchitectures -RequireMicrosoftSignature
```

## Verify The Live Driver Stack

After installing or reconnecting a Magic Keyboard, check whether Windows actually has the filter in the driver store and bound to the keyboard target:

```powershell
$statusPath = "$env:TEMP\apple-keyboard-filter-status.txt"
Start-Process -FilePath "$env:LOCALAPPDATA\ApplePeripheralsForWindows\app\MagicTrackpad.exe" -ArgumentList @("--keyboard-filter-status", "--output", $statusPath) -Wait
Get-Content $statusPath
```

From the repo source tree:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\check-keyboard-filter-driver.ps1
```

For a gating check:

```powershell
$statusPath = "$env:TEMP\apple-keyboard-filter-status.txt"
$process = Start-Process -FilePath "$env:LOCALAPPDATA\ApplePeripheralsForWindows\app\MagicTrackpad.exe" -ArgumentList @("--keyboard-filter-status", "--require-ready", "--output", $statusPath) -Wait -PassThru
Get-Content $statusPath
$process.ExitCode
```

Or from the repo source tree:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\check-keyboard-filter-driver.ps1 -RequireReady
```

Use `-RequireMicrosoftSigner` when verifying a public-release machine. A healthy public install should report `Ready: True`; if it reports that the filter is not in the driver store, Globe/Fn cannot be remapped by the app because Windows is still hiding that bit in the stock keyboard stack.

After the driver status is ready, prove the physical key is reaching the bridge:

```powershell
$probePath = "$env:TEMP\apple-globe-probe.json"
$process = Start-Process -FilePath "$env:LOCALAPPDATA\ApplePeripheralsForWindows\app\MagicTrackpad.exe" -ArgumentList @("--test-globe", "--seconds", "8", "--json", "--output", $probePath) -Wait -PassThru
Get-Content $probePath
$process.ExitCode
```

Press the physical Globe/Fn key while the probe is running. A working install reports `"Observed": true`; a stock Windows keyboard stack usually reports that the Apple Keyboard Filter driver must be installed and bound.

## Local Development Test Signing

For local driver development only, run from elevated PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-keyboard-filter-driver-dev.ps1 -EnableTestSigning
```

If the machine does not have Visual Studio Build Tools and WDK installed, use an existing unsigned GitHub Actions artifact or local package directory:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-keyboard-filter-driver-dev.ps1 -DriverDir .\artifacts\keyboard-filter\AMD64 -SkipBuild -EnableTestSigning -Elevate
```

Or let the helper download the latest successful CI-built unsigned package for the current CPU architecture before it requests elevation:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-keyboard-filter-driver-dev.ps1 -DownloadLatestArtifact -SkipBuild -EnableTestSigning -Elevate
```

To verify only the artifact download path without changing Windows driver or boot settings:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-keyboard-filter-driver-dev.ps1 -DownloadLatestArtifact -DownloadOnly
```

The helper creates or reuses a local code-signing certificate, trusts it on the machine, signs a temporary copy of the catalog, and installs the filter with `-AllowTestSigned`. If the helper has to enable Windows test-signing mode, it stops after changing the boot setting; reboot Windows and run the same command again so it can sign and install the filter after test-signing is active. This path is deliberately not used by the public setup executable.
If Secure Boot is enabled, Windows blocks test-signing mode; disable Secure Boot in UEFI/BIOS before using this no-cost local path.

The driver installer also tries to restart detected Magic Keyboard filter targets once after adding the package. If Windows still reports the filter is not active, reboot or reconnect the keyboard before rerunning the driver status and live Globe/Fn probe.

## GitHub Artifact Build

The `keyboard-driver` GitHub Actions workflow builds unsigned AMD64 and ARM64 driver packages on a Windows runner with WDK 26100:

```powershell
gh workflow run keyboard-driver.yml
```

Those artifacts are for signing/submission only. Do not install them on normal Windows systems until the catalog is trusted by Microsoft attestation/HLK signing or by a local test-signing setup.

The workflow also packages both architecture builds into `AppleKeyboardFilterDriver-attestation-cab-unsigned`. Use that CAB as the input for Hardware Dev Center attestation after signing the CAB with the account's EV or registered code-signing certificate.

Build the CAB locally with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-keyboard-driver-submission-cab.ps1
```

After Partner Center returns the Microsoft-signed package, convert it into the installer-ready zip with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package-signed-keyboard-driver.ps1 -SignedPackage .\path\from-partner-center.cab -RequireMicrosoftSignature
```

## Bundle With Setup

Build the setup `.exe` with the signed keyboard driver package:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 -Runtime win-x64 -KeyboardDriverZip .\artifacts\keyboard-filter\AppleKeyboardFilterDriver-signed.zip -RequireKeyboardDriver
```

When the Microsoft-signed package is bundled, the setup app installs it together with the bundled Magic Trackpad Precision Touchpad driver from the same setup `.exe`.
After installation, setup runs the installed app's `--keyboard-filter-status --require-ready --require-microsoft-signer` check and reports when a reconnect or restart is still needed before Globe/Fn remapping can work.

The GitHub `release` workflow refuses to create an end-user release unless a Microsoft-signed keyboard driver zip and SHA-256 are supplied. See [release.md](release.md).
