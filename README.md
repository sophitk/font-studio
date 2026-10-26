# FontStudio

A local Windows desktop font-library prototype built with C# / WPF / .NET 10.

## Source edition
This GitHub preparation package uses the existing default read-only build and policy. It browses/searches/previews installed fonts, supports collections/favorites, inspects metadata and reports duplicates. It does not enable font installation or deactivation.

The original project has a separate current-user font-management build; its privileged/mutation launchers, test tokens, runtime reports and bundled binaries are intentionally excluded here.

## Requirements and run
Windows 10/11 and .NET 10 SDK with Windows Desktop support.

    dotnet run --project FontStudio/FontStudio.csproj --configuration Release

Or double-click Start-FontStudio.cmd. No third-party font files are bundled; the app reads fonts installed on the current computer.

## Build and smoke test
    dotnet build FontStudio/FontStudio.csproj --configuration Release
    dotnet run --project FontStudio.SmokeTests/FontStudio.SmokeTests.csproj --configuration Release

Smoke tests inspect local installed fonts and create disposable temporary fixtures. They verify the read-only boundary without installing or deactivating fonts. Application preferences/collections are local user data; inspect screenshots before sharing paths or font names.

## Portfolio focus
Desktop UI, font metadata, local collections, duplicate hashing and guarded mutation architecture. Developed with AI assistance.

## License
MIT — see LICENSE and LICENSE-STATUS.md. Installed fonts remain subject to their own licenses.
