# Third-party notices

## Magpie v0.12.1

SpinFOURKAYYY can distribute and launch a separate, unmodified copy of Magpie v0.12.1.

- Project: <https://github.com/Blinue/Magpie>
- Exact release: <https://github.com/Blinue/Magpie/releases/tag/v0.12.1>
- Copyright: Magpie contributors
- License: GNU General Public License, version 3
- Expected official x64 release archive SHA-256: `8BC8BC233438F546B7996B00B21D7376F4F7D3D8A4940E6A8800BABD2225B2DE`
- Exact source commit used for the bundled release: `664e0f4c8a9aca4e6efb0e37f52be6dc62414f7b`

The packaged distribution includes:

- Magpie in `Engine\Magpie\`;
- the full GPL-3.0 license in `ThirdPartyLicenses\Magpie-GPL-3.0.txt`;
- the corresponding v0.12.1 source archive in `ThirdPartySource\Magpie-v0.12.1-source.zip`; and
- `ThirdPartySource\Magpie-v0.12.1-binary-manifest.json`, which records the official archive digest and every extracted engine file's SHA-256.

The build does not trust or copy a separately extracted engine directory. It first verifies the official release ZIP's pinned digest, extracts directly from that archive with path-containment checks, and compares each staged file with the SHA-256 of its archive entry.

Magpie is launched as a separate program and maintains its own license. SpinFOURKAYYY does not claim ownership of or relicense Magpie.

Magpie is provided without warranty under the terms of GPL-3.0. Consult the included license for the complete terms.

## Microsoft.UI.Xaml 2.8.7

The unmodified official Magpie v0.12.1 release contains `Microsoft.UI.Xaml.dll`. Magpie's pinned build metadata identifies that component as Microsoft.UI.Xaml version 2.8.7.

- Official package: <https://www.nuget.org/packages/Microsoft.UI.Xaml/2.8.7>
- Publisher and copyright: Microsoft Corporation; all rights reserved
- License: Microsoft Software License Terms — Microsoft Windows UI Library
- Official NuGet package SHA-256 used to verify the license provenance: `79207B10FE243EB1A8DCDC29BECEBA2F472F145ED31F147F7AE3F43B0659C9F7`

The complete, unmodified license text from the official 2.8.7 NuGet package is included as `ThirdPartyLicenses\Microsoft.UI.Xaml-2.8.7-LICENSE.txt`. The component remains separately licensed under those terms; neither Magpie's GPL license nor SpinFOURKAYYY's MIT license relicenses it. By using the bundled component, users accept its accompanying Microsoft license terms.

## .NET 9.0.18 Runtime and WindowsDesktop

`SpinFOURKAYYY.exe` is published self-contained and therefore embeds files from these Microsoft runtime packs:

- [Microsoft.NETCore.App.Runtime.win-x64 9.0.18](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/9.0.18)
- [Microsoft.WindowsDesktop.App.Runtime.win-x64 9.0.18](https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/9.0.18)

The runtime and WindowsDesktop components are licensed under the MIT License by the .NET Foundation and contributors. Their authoritative package files are copied into every release as:

- `ThirdPartyLicenses\DotNet-Runtime-9.0.18-MIT.txt`;
- `ThirdPartyLicenses\DotNet-Runtime-9.0.18-THIRD-PARTY-NOTICES.txt`; and
- `ThirdPartyLicenses\DotNet-WindowsDesktop-9.0.18-MIT.txt`.

The release build pins version 9.0.18, obtains these files from the restored official runtime packages, and verifies their known SHA-256 values before packaging. The .NET components and their listed dependencies are provided under the licenses and disclaimers in those included files.

## EverQuest Legends

No EverQuest Legends game assets, client executables, UI files, or proprietary libraries are included with SpinFOURKAYYY. The utility only locates a user-provided installation and, with the user’s explicit action, updates reversible per-user configuration.
