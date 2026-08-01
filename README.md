# SpinFOURKAYYY

SpinFOURKAYYY is a live 4K-readability companion for EverQuest Legends on Windows. It scales the running game instantly — no relaunch and no configuration edits. It can keep the UI at native 100% while applying an adjustable clarity pass, or resize the running client window to a lower real render size and scale the complete frame for larger UI and overhead names.

It never writes to `eqclient.ini` or any other saved player file. It also does not replace EverQuest assets, inject code, alter network traffic, install a display driver, or force Windows desktop scaling to 100%.

## Quick start

1. Extract the complete SpinFOURKAYYY release into a normal user-writable folder.
2. Start EverQuest Legends normally (any launcher, any saved settings) and leave it in windowed mode.
3. Run `SpinFOURKAYYY.exe`. If exactly one Legends client is running, live scaling attaches automatically.
4. Choose:
   - **Native clarity (100%)** for the same UI size with a crisper visible world and overhead-name text; or
   - any value from **101% to 200%** for larger UI and larger visible overhead names.
5. Move the slider at any time — the running game follows within a moment. **Clarity strength** adjusts live the same way.

Order does not matter: you can also open SpinFOURKAYYY first and click **Start EverQuest for me**, which opens the normal launcher and attaches live scaling automatically when the game window appears. Nothing is prepared, backed up, or rewritten either way.

No character-profile selection is required. **Current/default/custom UI** remains selected unless the user explicitly chooses the optional strict SpinUI workflow.

## Your saved settings always persist

SpinFOURKAYYY treats the entire EverQuest directory as read-only:

- `eqclient.ini` is never written, prepared, locked, or restored. At most it is read once, to warn when in-game native UI scaling is stacked on top of fullscreen scaling — and even then scaling proceeds.
- UI layouts, hotbars, socials, spell sets, keybinds, and userdata are never backed up, swapped, or rolled back by the scaling flow. Whatever you change and save in game is exactly what the game loads next time.
- The only thing SpinFOURKAYYY changes is the size and position of the running game **window**, and it restores the exact previous window geometry when scaling stops.

If an older SpinFOURKAYYY version left a pre-session profile backup on this machine, the app shows a notice and leaves your current files untouched. **Restore profile** is strictly manual, requires the game to be closed, and preserves your current files in a recovery copy before rolling anything back.

## Native clarity versus larger UI

EverQuest composites the 3D world, UI, UI text, and overhead names into one frame.

| Mode | 4K source | UI size | Intended result |
| --- | ---: | ---: | --- |
| Native clarity | 3840×2160 | 100% | Native world/UI resolution plus an adjustable RCAS clarity treatment |
| Gentle | 3072×1728 | 125% | Larger UI and names while retaining the most world detail |
| Balanced | 2560×1440 | 150% | Strong readability with a lower 3D source resolution |
| Comfort | 1920×1080 | 200% | Maximum UI/name size; world starts from 1080p |

The slider covers every exact 1% step from 100% through 200%. Values above 100% use a smaller real source resolution, so they enlarge the complete frame and necessarily trade some 3D resolution for readability. Values around 110%–133% are the quality-first range on high-resolution and ultrawide monitors.

**Native clarity** does not enlarge the UI or nameplates. Its GPU-vendor-neutral RCAS pass can make already-visible distant names cleaner, but it cannot increase the game's nameplate draw distance, recreate faded or culled text, or enlarge names independently from the UI.

A true native-resolution 3D world with independently larger UI/nameplates would require client-native fractional scaling or a separately maintained UI/XML/font implementation. An external whole-frame scaler cannot honestly provide that split.

## How live scaling works

SpinFOURKAYYY resizes the running client **window** to the selected real source size, then scales that window to borderless fullscreen with the bundled Magpie engine. In windowed mode the client adapts its render surface to the window, so the resize takes effect immediately — the game does not need to be started from this app or restarted for a new size.

Moving the size slider during an active session stops the owned output, resizes the exact same game window, and immediately re-establishes borderless fullscreen itself — bringing the game back to the foreground and re-verifying the output and mouse map before reporting success. You never need to click back into the game to restore fullscreen after an adjustment. When scaling stops (or the app closes), the game window is returned to its exact previous size and position and the game keeps running normally.

## Adjustable clarity

The **Clarity strength** slider (0–200%, default 110%) controls the sharpening chain applied to the visible frame in every mode, including Lanczos:

- **0%** disables sharpening entirely.
- **1–100%** maps to one RCAS pass of increasing strength.
- **101–200%** layers Magpie’s bundled AdaptiveSharpen pass on top with increasing strength. Its adaptive curve and overshoot compression add visible punch to the whole frame without the halo artifacts of simply stacking a second plain sharpener.

Clarity changes apply to a live session automatically: the engine restarts its fullscreen output with the new strength in a few seconds, without touching the game.

Sharpening operates on the rendered frame, so it makes existing detail — including already-visible overhead names — crisper; it cannot re-render text the game drew tiny or did not draw at all. For genuinely larger, easier-to-read names and UI at a distance, use the size slider above 100%: the whole frame, names included, is enlarged.

## Fullscreen, Alt+Tab, and mouse behavior

Legends remains in windowed mode underneath a borderless output that fills the selected monitor. Magpie normal mode is used so Alt+Tab is a focus/Z-order change rather than an exclusive-fullscreen transition.

The app binds the session to the exact EverQuest executable, process, window handle, window class, source size, target monitor, and owned Magpie process. It verifies the physical source/destination cursor map at the corners, center, and round trip. A session is not reported safe merely because a picture appeared.

If the output, source identity, source resolution, target monitor, or mouse map becomes uncertain, SpinFOURKAYYY stops only its exact owned scaling session. If shutdown cannot yet be confirmed, it retains cleanup ownership and blocks another scaling attempt.

Do not use Alt+Enter as part of the workflow.

## Optional SpinUI compatibility

[SpinUI](https://github.com/itsspin/spinips) is optional. **Current/default/custom UI** remains the normal choice even if SpinUI assets exist for another character.

Strict SpinUI mode offers only validated source resolutions and requires the user to apply the matching SpinUI layout with the SpinUI installer. SpinFOURKAYYY detects saved skin names for compatibility, but it never installs, auto-selects, or rewrites the SpinUI XML/TGA/DDS asset tree, and it validates — never resizes — a strict SpinUI client window.

## Filters and performance

- **Native clarity** uses the adjustable RCAS clarity path at 100% source size.
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
- **Release** (`.github/workflows/release.yml`) runs when a `v<major>.<minor>.<patch>` tag is pushed, or on demand from the Actions tab. It refuses to continue unless the tag matches the `<Version>` in `src/SpinFourKay.App/SpinFourKay.App.csproj`, rebuilds and re-verifies the packaged checksum, then publishes a GitHub Release containing the ZIP, its `.zip.sha256` sidecar, and verification instructions. Tags with a prerelease suffix (for example `v0.6.0-rc.1`) are published as prereleases.

To cut a release:

1. Update the `<Version>`, `<FileVersion>`, and `<AssemblyVersion>` values in the app project and merge to `main`.
2. Either push the matching tag (`git tag v1.0.2 && git push origin v1.0.2`), or open **Actions → Release → Run workflow** on `main` and enter the tag name. If the tag does not exist yet, the workflow verifies the project version first and then creates the tag itself; if it does exist, that exact tag is rebuilt and the release's assets and notes are refreshed in place.

## Third-party and trademark notice

The program source is MIT-licensed. The bundled Magpie engine is distributed under GPL-3.0 with its corresponding source and license. See `THIRD_PARTY_NOTICES.md` and `ThirdPartyLicenses` in the release.

EverQuest and related assets remain the property of their respective owners. SpinFOURKAYYY does not include or modify EverQuest game assets.
