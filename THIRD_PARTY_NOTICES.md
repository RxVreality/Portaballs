# Third-party notices

## In this repository

**OpenVR** (`helper/thirdparty/openvr/`: `openvr_api.cs` and `openvr_api.dll`), by Valve Corporation,
BSD 3-Clause licence. The full text is in `helper/thirdparty/openvr/LICENSE` and is copied next to the
helper when it's built. Source: https://github.com/ValveSoftware/openvr

**s&box engine source files** (`engine-patches/files/` and the `.patch` files), by Facepunch Studios Ltd,
MIT licence, changed for Portaballs. See `engine-patches/LICENSE-sbox.md`.

## Downloaded on your PC by the installer, not included here

- **s&box** source from https://github.com/Facepunch/sbox-public (MIT), and its native engine files from
  Facepunch (https://artifacts.sbox.game) under Facepunch's terms:
  https://store.steampowered.com/eula/590830_eula_0. s&box's own third-party notices are in
  `C:\Portaballs\sbox\game\thirdpartylegalnotices` after install.
- **.NET 10 SDK** and runtime, by Microsoft (MIT), if you don't have them already. The helper is built
  self-contained, so it carries a copy of the .NET runtime.
- NuGet packages used to build and test: xUnit (Apache 2.0) and Microsoft.NET.Test.Sdk (MIT), for the tests
  only.
