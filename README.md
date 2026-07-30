# SpinFOURKAYYY

SpinFOURKAYYY is a reversible 4K-readability companion for EverQuest Legends on Windows. It can keep the UI at native 100% while applying a restrained clarity pass, or start the old client at a lower real render size and scale the complete frame for larger UI and overhead names.

It does not replace EverQuest assets, inject code, alter network traffic, install a display driver, or force Windows desktop scaling to 100%.

## Quick start

1. Exit EverQuest Legends normally. LaunchPad and Options Editor must also be closed during preparation.
2. Extract the complete SpinFOURKAYYY release into a normal user-writable folder.
3. Run `SpinFOURKAYYY.exe`.
4. Confirm the Legends directory and choose:
   - **Native clarity (100%)** for the same UI size with crisper visible world and overhead-name text; or
   - any value from **101% to 200%** for larger UI and larger visible overhead names.
5. Click **Launch** and finish sign-in through the normal Legends launcher.

No character-profile selection is required. **Current/default/custom UI** remains selected unless the user explicitly chooses the optional strict SpinUI workflow.

If the app detects a running generic/custom-UI client, it will not resize it. EverQuest keeps a fixed renderer/backbuffer for that launch, so external live resizing only stretches and blurs the existing frame; it does not produce real UI reflow. Exit Legends normally and use the restart-backed Launch path.

## Native clarity versus larger UI

EverQuest composites the 3D world, UI, UI text, and overhead names into one frame.

| Mode | 4K source | UI size | Intended result |
| --- | ---: | ---: | --- |
| Native clarity | 3840×2160 | 100% | Native world/UI resolution plus a bounded one-pass RCAS clarity treatment |
| Gentle | 3072×1728 | 125% | Larger UI and names while retaining the most world detail |
| Balanced | 2560×1440 | 150% | Strong readability with a lower 3D source resolution |
| Comfort | 1920×1080 | 200% | Maximum UI/name size; world starts from 1080p |

The slider covers every exact 1% step from 100% through 200%. Values above 100% use a smaller real source resolution, so they enlarge the complete frame and necessarily trade some 3D resolution for readability. Values around 110%–133% are the quality-first range on high-resolution and ultrawide monitors.

**Native clarity** does not enlarge the UI or nameplates. Its GPU-vendor-neutral RCAS pass can make already-visible distant names cleaner, but it cannot increase the game's nameplate draw distance, recreate faded or culled text, or enlarge names independently from the UI.

A true native-resolution 3D world with independently larger UI/nameplates would require client-native fractional scaling or a separately maintained UI/XML/font implementation. An external whole-frame scaler cannot honestly provide that split.

## Why restart-backed scaling is required

SpinFOURKAYYY temporarily writes the selected source dimensions before EverQuest starts. That makes the client create its renderer, viewport, UI coordinate system, and input surface at the correct dimensions from the beginning.

Changing only the outer window while the game is already running does not rebuild those systems. It produces the exact failure mode this release avoids: unchanged relative UI size, increasing blur, window/fullscreen transitions, and unreliable expectations about what the slider changed.

The active session is therefore locked. To choose another size:

1. Stop scaling.
2. Exit Legends and LaunchPad normally.
3. Select the next size.
4. Launch again.

## Player-profile protection

Before any prepared launch, SpinFOURKAYYY creates a byte-exact, checksum-verified snapshot of the mutable files that can hold player configuration:

- `eqclient.ini`;
- root `UI_*.ini` character layouts;
- root character/server/account INIs, including hotbuttons, socials, spell loadouts, friends, combat settings, and related character state;
- `_characters.ini`, `eqlsPlayerData.ini`, and `notes.txt`;
- supported `.ini` and `.txt` files beneath `userdata` and `AudioTriggers`; and
- the prior per-game Windows DPI compatibility value.

Static game assets, patcher state, logs, and UI skin assets under `uifiles` are deliberately excluded. SpinFOURKAYYY never installs or replaces an EverQuest UI skin.

After the scaling session ends, restoration waits until EverQuest Legends, LaunchPad, and Options Editor are closed. It then restores and checksum-verifies the complete pre-session snapshot automatically. If the game saved changed layouts, hotbars, macros, spell sets, keybinds, userdata, or settings during the temporary session, those newer copies are first preserved in a separate recovery backup. This is preservation, not a field-by-field merge.

Backups live under:

```text
%LOCALAPPDATA%\SpinFOURKAYYY\backups\<timestamp-and-session-id>\
```

Recovery journals live under:

```text
%LOCALAPPDATA%\SpinFOURKAYYY\sessions\
```

If automatic restoration cannot finish, close Legends and its launcher, reopen SpinFOURKAYYY, and choose **Restore profile**. The journal and verified payload remain available for retry.

Backing up the complete EverQuest Legends directory before any third-party customization is still recommended.

## Fullscreen, Alt+Tab, and mouse behavior

Legends remains in windowed mode underneath a borderless output that fills the selected monitor. Magpie normal mode is used so Alt+Tab is a focus/Z-order change rather than an exclusive-fullscreen transition.

The app binds the session to the exact EverQuest executable, process, window handle, window class, source size, target monitor, and owned Magpie process. It verifies the physical source/destination cursor map at the corners, center, and round trip. A session is not reported safe merely because a picture appeared.

If the output, source identity, source resolution, native `UIScale=0`, target monitor, or mouse map becomes uncertain, SpinFOURKAYYY stops only its exact owned scaling session. If shutdown cannot yet be confirmed, it retains cleanup ownership and blocks another scaling attempt.

Do not use Alt+Enter as part of the workflow.

## Optional SpinUI compatibility

[SpinUI](https://github.com/itsspin/spinips) is optional. **Current/default/custom UI** remains the normal choice even if SpinUI assets exist for another character.

Strict SpinUI mode offers only validated source resolutions and requires the user to apply the matching SpinUI layout with EverQuest closed. SpinFOURKAYYY detects saved skin names for compatibility, but it never installs, auto-selects, or rewrites the SpinUI XML/TGA/DDS asset tree.

Character layout/profile INIs are included in the safety snapshot regardless of which UI skin is active. Restoration returns the pre-session versions after preserving any session-era copies.

## Filters and performance

- **Native clarity** uses one RCAS pass at 100% source size.
- **Adaptive FSR** is the default vendor-neutral upscaling path for fractional enlargement.
- **Lanczos** is a lighter fallback for enlarged modes.
- **Exact pixels** is intended only for a true 2× Comfort plan.

The shaders run on the GPU and work with AMD, NVIDIA, and compatible Intel adapters supported by Magpie. No frame generation is used. Lower source resolutions can reduce the game's 3D workload; the external scaling pass adds a small GPU cost.

## Download and verify

SpinFOURKAYYY 1.0.1 is an unsigned prototype distributed as a ZIP plus a neighboring `.zip.sha256` file.

```powershell
(Get-FileHash -Algorithm SHA256 .\SpinFOURKAYYY-1.0.1-win-x64.zip).Hash
Get-Content .\SpinFOURKAYYY-1.0.1-win-x64.zip.sha256
```

The hexadecimal values must match. Extract the ZIP completely and run the executable from the extracted folder, not from inside the archive, `Program Files`, the EverQuest directory, or an elevated administrator session.

The Windows x64 release is self-contained. Users do not need to install .NET or Magpie separately. Keep `SpinFOURKAYYY.exe`, `Engine`, and the notice/license files together.

Because the prototype is not code-signed, Microsoft Defender SmartScreen may display **Windows protected your PC**. Continue only when the checksum matches a trusted published value and the release came from the expected source. Do not disable SmartScreen globally.

## Compatibility and rules

- Windows 10 version 1903 or newer, or Windows 11.
- The game, SpinFOURKAYYY, and the bundled engine must run at the same privilege level. Administrator rights are intentionally not requested.
- Close any separately running Magpie instance first; Magpie permits one instance per Windows session.
- External capture/scaling tools are non-intrusive, but multiplayer/server policies still apply. Confirm that their use is acceptable under the EverQuest Legends rules.

## Build from source

Requirements:

- Windows x64;
- .NET SDK 9.x;
- PowerShell 7 or Windows PowerShell 5.1;
- Git; and
- internet access on the first build.

Run:

```powershell
.\build.ps1 -Version 1.0.1
```

The first build downloads the official pinned Magpie v0.12.1 release, verifies its SHA-256 before use, and checks out its exact audited source commit. The build then restores dependencies, compiles with warnings treated as errors, runs the deterministic self-test suite, publishes a self-contained single-file executable, verifies licenses, stages corresponding source, and creates the release ZIP plus SHA-256 sidecar under `artifacts`.

## Continuous integration and releases

Two GitHub Actions workflows run the same `build.ps1` pipeline on `windows-latest`:

- **CI** (`.github/workflows/ci.yml`) builds, runs the deterministic self-test suite, and packages the ZIP plus checksum sidecar on every push and pull request against `main`, uploading them as workflow artifacts.
- **Release** (`.github/workflows/release.yml`) runs when a `v<major>.<minor>.<patch>` tag is pushed. It refuses to continue unless the tag matches the `<Version>` in `src/SpinFourKay.App/SpinFourKay.App.csproj`, rebuilds and re-verifies the packaged checksum, then publishes a GitHub Release containing the ZIP, its `.zip.sha256` sidecar, and verification instructions. Tags with a prerelease suffix (for example `v0.6.0-rc.1`) are published as prereleases.

To cut a release: update the `<Version>`, `<FileVersion>`, and `<AssemblyVersion>` values in the app project, merge to `main`, then create and push the matching tag (for example `git tag v0.5.2 && git push origin v0.5.2`).

## Third-party and trademark notice

The program source is MIT-licensed. The bundled Magpie engine is distributed under GPL-3.0 with its corresponding source and license. See `THIRD_PARTY_NOTICES.md` and `ThirdPartyLicenses` in the release.

EverQuest and related assets remain the property of their respective owners. SpinFOURKAYYY does not include or modify EverQuest game assets.
