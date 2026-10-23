# Third-party references

The original Codex Usage Monitor statistics engine is retained. The following MIT projects informed the optional Windows integration added on 2026-10-15. No executable from these reference projects is included. The self-contained v0.2.0 package additionally redistributes the Microsoft .NET runtime described below.

## codex-pet-hud

- Author/project: himomohi, https://github.com/himomohi/codex-pet-hud
- Reviewed commit: `3bb8ff07697aaa092a211da7035f29e36eea82e1`
- Reference files: `platforms/windows/CodexPetLimitRings.Windows/Services/CodexPetStateReader.cs`, `Interop/NativeMethods.cs`, related models/path definitions.
- Adapted ideas: avatar open/bounds metadata, DIP and physical-window geometry correlation, integrated full-height sidecar support. This implementation adds strict OS package identity, unique-root/Image selection and bounded failure handling, without the upstream pointer relay.
- Original license: [codex-pet-hud MIT](licenses/codex-pet-hud-MIT.txt).

## codex-token-overlay

- Author/project: soleillevant0125, https://github.com/soleillevant0125/codex-token-overlay
- Reviewed commit: `b3a38d727fb2e0cf8e8c92ffff3f65da9a592dc5`
- Reference file: `Program.cs`, `CodexIpcActiveThreadMonitor`.
- Adapted ideas: length-prefixed `codex-ipc` initialize and `thread-stream-following-changed` broadcasts. The adapter is experimental, authenticates the OS pipe server, rejects ambiguous routes, and never opens a new log source. The nonpublic protocol may change.
- Original license: [codex-token-overlay MIT](licenses/codex-token-overlay-MIT.txt).

The upstream projects and their authors do not endorse this implementation. Their UI claims are not local acceptance evidence. No code from Xiao-Xiao-hub/XiaoLi is incorporated.

## CodexBar account quota workflow (2026-10-16)

- Author/project: Peter Steinberger / steipete, https://github.com/steipete/CodexBar
- Reviewed commit: `667dd3a58729479eca86562ae89213c97fad8c07`, `Sources/CodexBarCore/UsageFetcher.swift`.
- Reference: launching the official CLI as `codex -s read-only -a never app-server`, initializing JSONL RPC and reading account/rate-limit information through the existing CLI authentication. The C# implementation is independently written, includes Windows native-executable discovery, explicit protocol bounds and teardown, and projects no credentials. No Swift code, auth/JWT decoder, released binary or asset is copied.
- The upstream project uses MIT; this entry credits the workflow reference and does not claim upstream endorsement or local acceptance.

## Microsoft .NET 10 runtime redistribution

The self-contained win-x64 package redistributes Microsoft.NETCore.App.Runtime.win-x64 10.0.13 and Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.13 from Microsoft's official NuGet packages. These runtime libraries are separate from the historical integration references above.

The original package notices are retained byte for byte:

- [.NET runtime MIT license](licenses/dotnet-10.0.13/NETCore-LICENSE.TXT)
- [.NET runtime third-party notices](licenses/dotnet-10.0.13/NETCore-THIRD-PARTY-NOTICES.TXT)
- [Windows Desktop runtime MIT license](licenses/dotnet-10.0.13/WindowsDesktop-LICENSE.txt)

`scripts/runtime-packages.json` records exact versions, Microsoft package/catalog URLs, catalog-derived SHA-512 pins, original archive entry names and notice SHA-256 hashes. The build checks the local runtime feed against those pins and includes these notices in the portable payload. Microsoft and the .NET Foundation do not endorse this application.
