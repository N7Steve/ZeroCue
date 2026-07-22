# Third-party notices

ZeroCue distributes or uses the following third-party components. Their licenses
apply to those components; they do not license ZeroCue itself.

## libusb

The portable package includes `libusb-1.0.dll` from libusb 1.0.30.

- Project: https://github.com/libusb/libusb
- Release: https://github.com/libusb/libusb/releases/tag/v1.0.30
- License: GNU Lesser General Public License 2.1 or later
- License text: `licenses/libusb-LGPL-2.1.txt`

The checked-in DLL has SHA-256
`7CBF37E76DAE9C840C7E8DBF7348EE8897DCC86C8BA45E46ADA60B89411569F7`.

## libwdi / wdi-simple

The driver installation helper `Assets/wdi-simple.exe` incorporates libwdi.

- Project: https://github.com/pbatard/libwdi
- License: GNU Lesser General Public License 3.0 or later
- License text: `licenses/libwdi-LGPL-3.0.txt`

The currently checked-in helper has SHA-256
`8C22510BD4431152E7DF787D7135A00875AF7BB3AFC090D2AB72E859EFB25E33`.
Its exact source revision and build recipe have not yet been reconstructed. It
is retained unchanged for the initial alpha by an explicit project decision and
is pinned by hash so any binary change fails CI. This is a documented provenance
and code-signing limitation of the alpha, not a claim that the binary is an
official upstream libwdi release.

## Super Input Prompt icon pack

Controller and keyboard prompt images are derived from Julio Cacko's Free Super
Input Prompt Icon Pack, released under CC0 1.0.

- Project page: https://juliocacko.artstation.com/projects/OvaZqk
- License: https://creativecommons.org/publicdomain/zero/1.0/

## Material Icons

The `Material.Icons` and `Material.Icons.Avalonia` packages are MIT-licensed.
Their icon data is parsed from Pictogrammers' Material Design Icons collection,
which is available under Apache License 2.0.

- Package project: https://github.com/SKProCH/Material.Icons
- Icon collection: https://github.com/Templarian/MaterialDesign

## Managed dependencies and the .NET runtime

Managed dependencies and exact versions are recorded in
`ZeroCue.DataProbe/packages.lock.json`. Every portable build generates
`resources/licenses/managed-dependencies.md` from the complete lockfile closure,
including package authors, copyright metadata, SPDX expressions, repositories,
and every license or third-party notice bundled in the NuGet packages.

The same build copies license and third-party notice files from the exact
`win-x64` .NET runtime packs selected by the SDK. The audit fails if a dependency
has no license metadata, introduces an unreviewed license expression, or a
declared license file cannot be found.
