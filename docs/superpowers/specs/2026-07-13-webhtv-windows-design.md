# WebHomeTV Windows Design

## 1. Purpose and scope

Build a new, publicly publishable Windows desktop application that recreates the usable functionality of the supplied WebHomeTV Android APK. It is a Windows application, not an APK wrapper or Android emulator.

Target platforms:

- Windows 10 x86 and x64
- Windows 11 x64 (the x86 build remains usable on x64 Windows where applicable)

The product starts with no imported Android data and does not ship third-party content sources. Users import and manage their own configuration locally.

The Windows edition retains these local capabilities:

- VOD browsing, live TV, search, details, source switching, parsing, playback, subtitles, speed control, favourites and watch history.
- Configuration and site management, including VOD, live, parser and custom-site configuration.
- JavaScript and Python Spider execution.
- WebHome pages and extensions, including their supported application bridge functions.
- Cloud-drive link checking, site-health scoring, application proxy rules, login-state learning and local diagnostics.
- A local or LAN-enabled management page and local HTTP API. LAN exposure is disabled by default.

The Windows edition deliberately excludes:

- Physical television remote-control UX and remote-control key mapping.
- Remote hosting, remote device management and cross-device synchronisation.

## 2. Technology decision

Use .NET 8, WPF and LibVLCSharp.

- WPF supplies a native Windows UI with keyboard and mouse support and has first-class x86 and x64 publishing paths.
- LibVLCSharp supplies a mature native playback core and is packaged separately for each CPU architecture.
- WebView2 hosts WebHome pages and extensions.
- A managed bridge hosts QuickJS for JavaScript Spider code and an embedded Python runtime for Python Spider code. Both are wrapped behind one Spider contract.
- SQLite stores application-owned local state; configuration files and diagnostic logs are stored beneath the current user profile.

This avoids an Android runtime while preserving the extensibility points that distinguish the original application.

## 3. Architecture

The solution is organised into independently testable projects:

| Project | Responsibility |
|---|---|
| `WebHtv.Desktop` | WPF views, view models, keyboard/mouse interaction, application composition. |
| `WebHtv.Core` | Domain models and interfaces for configuration, catalogues, playback, history and settings. |
| `WebHtv.Configuration` | Import, validation, persistence and site configuration management. |
| `WebHtv.Spider` | Unified Spider contract plus JavaScript and Python runtime adapters. |
| `WebHtv.Playback` | Resolve playable media, subtitle and playback-state management over LibVLC. |
| `WebHtv.WebHome` | WebView2 host, permissioned native bridge and WebHome extension management. |
| `WebHtv.LocalServices` | Local management UI, local HTTP API, diagnostics and optional LAN binding. |
| `WebHtv.Infrastructure` | SQLite repositories, secure credential storage, HTTP client, proxy and filesystem access. |
| `WebHtv.Tests` | Unit, integration and packaging tests. |

All UI features depend on interfaces in `WebHtv.Core`; direct access to the player, file system, runtime engines and network is kept in the feature-specific projects above. This makes individual features replaceable and testable without the WPF process.

## 4. Main data flows

### Configuration to playback

1. The user imports or edits a local configuration.
2. `WebHtv.Configuration` validates and atomically saves it.
3. The UI requests catalogue/search/detail data through `WebHtv.Core`.
4. The configuration engine and Spider adapters return a normalised result.
5. The user chooses an episode/source; the resolver produces a playable URL and optional subtitle information.
6. `WebHtv.Playback` hands the stream to LibVLC and persists playback progress and history in SQLite.

### WebHome bridge

1. WebView2 loads a configured local or remote WebHome page.
2. Page requests are mapped to an explicit bridge method such as search, play, history or permitted network access.
3. The bridge validates arguments and permissions, calls the corresponding Core service, and returns structured results.
4. A page never receives arbitrary shell, registry, unrestricted filesystem or credential access.

### Local administration

1. The desktop app starts the management service on loopback by default.
2. The user may explicitly enable a LAN binding in Settings.
3. The management UI/API uses the same application services and validation rules as the WPF UI.

## 5. Interaction and UI

The user interface is redesigned for desktop keyboard and mouse use, rather than copied from Android Leanback screens. It contains a left navigation rail, searchable content grids/lists, a detail page with source and episode selection, a player window with standard desktop controls, and a settings/management area. Keyboard shortcuts support navigation and player control; physical TV remote support is out of scope.

The empty first-run state explains that the user must import a configuration and provides the import entry point. No third-party source configuration is embedded in the installer.

## 6. Reliability, privacy and safety

- Invalid configuration, Spider exceptions, resolver failures, expired playback URLs, proxy/network errors, WebHome bridge denials and missing native dependencies are translated into actionable, non-crashing UI messages.
- A single site or script failure is isolated from the rest of the app.
- Configuration writes use a temporary file plus atomic replacement. SQLite uses transactions for user state.
- Secrets and login-state material are stored per Windows user and are never exposed through logs or the WebHome bridge.
- Local HTTP/LAN capabilities are opt-in; diagnostic logging is redactable and exportable by the user.

## 7. Packaging and release

Build two self-contained installer EXEs:

- `WebHomeTV-Desktop-x64-Setup.exe`
- `WebHomeTV-Desktop-x86-Setup.exe`

Each installer contains the correctly matched native VLC and runtime dependencies. A missing WebView2 Runtime results in a clear install prompt. The GitHub repository contains source, build scripts, documentation, license notices, CI workflows and release instructions; generated installers are published as GitHub Release assets rather than committed to the repository.

Because the reference project is GPL-3.0, the implementation will undergo a dependency and licence audit before the first public release. Any code derived from it will carry the required notices and source-distribution obligations; independently written code will still document all third-party component licences.

## 8. Verification and acceptance

Automated tests cover configuration parsing/validation, history persistence, Spider contracts, WebHome bridge permissions, resolver behaviour, proxy rules and local API authorisation. Integration tests cover JavaScript and Python Spider fixtures, a local WebHome fixture, playback of legal test media and a local management-page session. Packaging smoke tests install and launch both x64 and x86 builds on the supported Windows matrix.

Acceptance requires:

- A fresh installation can import a user-owned configuration and complete search, details, playback, favourites and history.
- JavaScript/Python Spider, WebHome/extensions, parsing, live TV, proxy, cloud-drive checking, site-health scoring, login-state learning and local management/API work as specified.
- x64 and x86 installers launch correctly with their respective native dependencies.
- The excluded remote-control, remote-hosting and cross-device-sync features are absent.
- The public repository and release artefacts contain the required licensing and source information.

## 9. Delivery sequence

1. Create the solution skeleton, CI and licence inventory.
2. Deliver configuration, core models, desktop shell and local persistence.
3. Deliver catalogues, search, parser integration and LibVLC playback.
4. Deliver Spider runtimes and WebHome bridge/extensions.
5. Deliver local management/API, proxy, login-state learning, cloud-drive checks and health scoring.
6. Package, test and publish x64/x86 release candidates with GitHub documentation.

The milestones are implementation order only; the intended final product contains all in-scope capabilities.
