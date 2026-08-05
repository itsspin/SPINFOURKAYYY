# SpinFOURKAYYY

SpinFOURKAYYY is a 4K-readability companion for EverQuest Legends on Windows. It can preserve the original pixels at native 100%, or render at a lower real resolution and scale the complete frame for larger UI and overhead names through a text-first directional scaler.

For sizes above 100%, SpinFOURKAYYY automatically fits every player's own `UI_<character>_<server>...ini` layout to the chosen render size before the normal launcher opens. It works with default, custom, and character-specific UI layouts—no bundled layout or hardcoded character name is required. It does not replace EverQuest assets, inject code, alter network traffic, install a display driver, or force Windows desktop scaling to 100%.

## Quick start

1. Extract the complete SpinFOURKAYYY release into a normal user-writable folder.
2. Run `SpinFOURKAYYY.exe` and choose:
   - **Native pixels (100%)** for the game's original image with no sharpening; or
   - any value from **101% to 200%** for larger UI and larger visible overhead names.
3. Click **Start EverQuest for me**. The normal Legends launcher opens; patch and sign in normally.
4. Keep SpinFOURKAYYY open while playing. When EverQuest exits, the app saves layout changes for that percentage and returns the live layout files to native geometry automatically.

SpinFOURKAYYY never resizes or attaches to an already-running client. Choose the percentage and any advanced quality options first, then launch through the app. This guarantees that EverQuest starts in a normal (not maximized or fullscreen) window at the exact physical source size Magpie will validate.

No character-profile selection is required. **Current/default/custom UI** remains selected unless the user explicitly chooses the optional strict SpinUI workflow.

## Personal layouts and saved settings

The automatic layout transaction is deliberately narrow and recoverable:

- Only top-level character layout files whose names match EverQuest's `UI_*.ini` convention are converted. Generic `Width` and `Height` geometry is scaled; percent-based anchors, UI skin names, chat settings, unknown sections, line endings, and legacy bytes are preserved.
- Character settings files are never opened by this flow. Macros, hotbuttons, socials, spell sets, keybinds, userdata, and UI asset folders remain untouched.
- Only the minimum `eqclient.ini` video keys needed to launch windowed at the selected source size are updated. The selected launch resolution remains saved for the next managed launch; unrelated client settings are preserved. A verified pre-write backup is retained, but character files are never included in that configuration backup.
- Verified native snapshots and scale-specific profiles live under `%LOCALAPPDATA%\SpinFOURKAYYY\layout-profiles`, not in the game folder. An interrupted prepare rolls back exactly; an interrupted exit restore resumes safely the next time the app opens.
- If a layout changed during play, its scaled version is saved for that percentage and an inverse-converted native version becomes the normal live file. If it did not change, the exact original bytes are restored.

SpinFOURKAYYY stays open until EverQuest exits so this capture-and-restore step cannot be skipped accidentally. If the app or Windows is interrupted, reopen SpinFOURKAYYY to resume recovery. Older pre-session backups from earlier versions remain separate: **Restore profile** is still manual and never runs as part of the new layout flow.

## Native pixels versus larger UI

EverQuest composites the 3D world, UI, UI text, and overhead names into one frame.

| Mode | 4K source | UI size | Intended result |
| --- | ---: | ---: | --- |
| Native pixels | 3840×2160 | 100% | Original world/UI pixels with no sharpening or whole-frame smoothing |
| Gentle | 3072×1728 | 125% | Larger UI and names through one directional NIS scaling pass |
| Balanced | 2560×1440 | 150% | Strong readability with a lower 3D source resolution |
| Comfort | 1920×1080 | 200% | Maximum UI/name size; world starts from 1080p |

The slider covers every exact 1% step from 100% through 200%. Values above 100% use a smaller real source resolution, so they enlarge the complete frame and necessarily trade some 3D resolution for readability. Values around 110%–133% are the quality-first range on high-resolution and ultrawide monitors.

**Native pixels** does not enlarge the UI or nameplates. It intentionally avoids post-process sharpening because a shader cannot recreate glyph samples that the game did not render; at 1:1, the original game pixels are the most faithful result. It cannot increase nameplate draw distance or enlarge names independently from the UI.

A true native-resolution 3D world with independently larger UI/nameplates would require client-native fractional scaling or a separately maintained UI/XML/font implementation. An external whole-frame scaler cannot honestly provide that split.

## How scaling works

Before an above-100% session, SpinFOURKAYYY selects or creates a verified layout profile for the exact native-to-source resolution pair and temporarily prepares the game's windowed launch size. It then opens the normal launcher, binds to the exact resulting EverQuest process and window, and scales that source window to borderless fullscreen with the bundled Magpie engine.

On first use for each app version, the bundled engine is validated and copied to a dedicated runtime under `%LOCALAPPDATA%\SpinFOURKAYYY\engine-runtime`. The running scaler no longer depends on files inside a download, extracted release, or developer build folder that might be replaced while its shaders are loading. Every required NIS, FSR, Lanczos, FXAA, and SMAA asset is checked again immediately before Magpie starts.

The percentage and quality controls are locked for the active session because changing them safely requires a fresh prepared source window. Exit EverQuest, choose new settings, and launch again; the matching layout profile is generated automatically. When scaling stops, EverQuest remains in its ordinary prepared window. When the game exits, layout changes are captured and the native layout transaction is completed.

## Readable UI and text edge detail

**Readable UI** is the recommended and default enlarged mode. It uses NVIDIA Image Scaling's directional reconstruction and adaptive sharpening together in one GPU-vendor-neutral Magpie pass. It never stacks RCAS, SMAA, or FXAA afterward, because those extra whole-frame passes can blur small strokes or turn them into halos and grain.

The **Text edge detail** slider is deliberately limited to **0–30%** and defaults to **20%**:

- **0%** uses NIS reconstruction without added adaptive sharpening.
- **20%** is the recommended balance for small lettering and UI artwork.
- **30%** is the guarded maximum; stronger values are intentionally unavailable.

The control is disabled for Native pixels, Lanczos, and Exact pixels. The optional **Smooth world · FSR** compatibility filter maps the same bounded value to its one RCAS pass. No mode uses two sharpening passes. Choose the setting before launching; it remains locked during the session so SpinFOURKAYYY never has to stop and re-attach Magpie to a running EverQuest window.

## Optional anti-aliasing

The advanced compatibility controls include whole-frame post-process anti-aliasing:

- **Off** is the default and preserves the sharpest small UI text.
- **SMAA High** provides cleaner edges while retaining more fine detail.
- **FXAA High** is a lighter, softer alternative.

Anti-aliasing smooths the complete composed frame rather than changing EverQuest's internal 3D anti-aliasing setting, so it also affects UI text and artwork. Readable UI always forces it Off and disables this control. Other compatibility filters can use it; choose the option before launching and use Off if small text looks too soft.

## Fullscreen, Alt+Tab, and mouse behavior

Legends remains in windowed mode underneath a borderless output that fills the selected monitor. Magpie normal mode is used so Alt+Tab is a focus/Z-order change rather than an exclusive-fullscreen transition.

The app binds the session to the exact EverQuest executable, process, window handle, window class, source size, target monitor, and owned Magpie process. It verifies the physical source/destination cursor map at the corners, center, and round trip. A session is not reported safe merely because a picture appeared.

If the output, source identity, source resolution, target monitor, or mouse map becomes uncertain, SpinFOURKAYYY stops only its exact owned scaling session. If shutdown cannot yet be confirmed, it retains cleanup ownership and blocks another scaling attempt.

Do not use Alt+Enter as part of the workflow.

## Optional SpinUI compatibility

[SpinUI](https://github.com/itsspin/spinips) is optional. **Current/default/custom UI** remains the normal choice even if SpinUI assets exist for another character.

Strict SpinUI mode offers only validated source resolutions and requires the user to apply the matching SpinUI layout with the SpinUI installer. SpinFOURKAYYY detects saved skin names for compatibility, but it never installs, auto-selects, or rewrites the SpinUI XML/TGA/DDS asset tree, and it validates — never resizes — a strict SpinUI client window.

## Filters and performance

- **Native pixels** uses an exact 1:1 nearest pass with no sharpening at 100% source size.
- **Readable UI** is the default fractional enlargement path: one NIS directional scaling pass at a guarded detail strength.
- **Smooth world · FSR** remains an optional compatibility path with one bounded RCAS pass.
- **Lanczos** is a lighter fallback for enlarged modes.
- **Exact pixels** is intended only for a true 2× Comfort plan.

The shaders run on the GPU and work with AMD, NVIDIA, and compatible Intel adapters supported by Magpie. No frame generation is used. Lower source resolutions can reduce the game's 3D workload; the external scaling pass adds a small GPU cost.

## Download and verify

SpinFOURKAYYY 1.0.4 is an unsigned prototype distributed as a ZIP plus a neighboring `.zip.sha256` file.

```powershell
(Get-FileHash -Algorithm SHA256 .\SpinFOURKAYYY-1.0.4-win-x64.zip).Hash
Get-Content .\SpinFOURKAYYY-1.0.4-win-x64.zip.sha256
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
.\build.ps1 -Version 1.0.4
```

The first build downloads the official pinned Magpie v0.12.1 release, verifies its SHA-256 before use, and checks out its exact audited source commit. The build then restores dependencies, compiles with warnings treated as errors, runs the deterministic self-test suite, publishes a self-contained single-file executable, verifies licenses, stages corresponding source, and creates the release ZIP plus SHA-256 sidecar under `artifacts`.

## Continuous integration and releases

Two GitHub Actions workflows run the same `build.ps1` pipeline on `windows-latest`:

- **CI** (`.github/workflows/ci.yml`) builds, runs the deterministic self-test suite, and packages the ZIP plus checksum sidecar on every push and pull request against `main`, uploading them as workflow artifacts.
- **Release** (`.github/workflows/release.yml`) runs when a `v<major>.<minor>.<patch>` tag is pushed, or on demand from the Actions tab. It refuses to continue unless the tag matches the `<Version>` in `src/SpinFourKay.App/SpinFourKay.App.csproj`, rebuilds and re-verifies the packaged checksum, then publishes a GitHub Release containing the ZIP, its `.zip.sha256` sidecar, and verification instructions. Tags with a prerelease suffix (for example `v0.6.0-rc.1`) are published as prereleases.

To cut a release:

1. Update the `<Version>`, `<FileVersion>`, and `<AssemblyVersion>` values in the app project and merge to `main`.
2. Either push the matching tag (`git tag v1.0.4 && git push origin v1.0.4`), or open **Actions → Release → Run workflow** on `main` and enter the tag name. If the tag does not exist yet, the workflow verifies the project version first and then creates the tag itself; if it does exist, that exact tag is rebuilt and the release's assets and notes are refreshed in place.

## Third-party and trademark notice

The program source is MIT-licensed. The bundled Magpie engine is distributed under GPL-3.0 with its corresponding source and license. See `THIRD_PARTY_NOTICES.md` and `ThirdPartyLicenses` in the release.

EverQuest and related assets remain the property of their respective owners. SpinFOURKAYYY does not include or modify EverQuest game assets.
