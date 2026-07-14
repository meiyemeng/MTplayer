# WebHomeTV Windows Native Formal Release Design

## Product decision

The deliverable is a native Windows application for Windows 10/11 x64 and x86. It does not launch an APK, BlueStacks, WSA or any other Android runtime. The TVBoxOS-Build and WebHomeTV projects are behavioural and configuration-compatibility references only; their Android build workflow is not part of the Windows application.

The product must not be presented as a user-facing test, demo or preview build. A release EXE is produced only when the functional acceptance criteria below pass with real user-provided configuration data.

## User-visible scope

The formal release contains:

- Chinese desktop UI with mouse and keyboard interaction.
- TVBox-compatible local and HTTP(S) JSON configuration import, preservation and management.
- VOD, live TV, search, detail, episode selection, source switching, parser resolution, favourites and local watch history.
- Native playback with subtitles, speed control, resume behaviour and actionable failure messages.
- A poster-wall home page populated only from real configuration results and their actual cover images. No fabricated sample titles or demo posters are shown in the release.
- Common JavaScript and Python Spider support, followed by WebHome pages/extensions, site health, cloud-drive checks, proxy rules, login-state learning, local management and local HTTP API.

Physical TV remote-control UX, remote hosting and cross-device synchronisation remain explicitly out of scope.

## Native architecture

The application remains a .NET 8 WPF solution and is split into the following Windows-native modules:

| Module | Responsibility |
|---|---|
| Desktop | Chinese WPF views, navigation, poster wall, settings and desktop interaction. |
| Configuration | TVBox JSON import/validation, profiles, configuration editing and atomic persistence. |
| Catalogue | Unified VOD/live/search/detail/episode/source models independent of the provider. |
| Spider | TVBox-compatible contracts and separate JavaScript/Python host adapters. |
| Resolver | Parser selection, source switching and conversion of a catalogue item into a playable request. |
| Playback | LibVLC integration, subtitle selection, speed, progress and resilient player errors. |
| WebHome | WebView2 host and a permissioned native bridge for supported WebHome methods. |
| Local Services | Opt-in local management page/API, diagnostics, proxy and login-state services. |

Configuration data flows from a local file or HTTP(S) address through strict validation into a profile store. Catalogue operations use the profile, Spider and parser modules to return normalised data. Playback obtains a resolved URL from that flow and persists progress locally. The poster wall reads the same normalised catalogue/history data and never invents content.

## Formal release quality bar

Before an EXE is called a release:

1. A fresh installation imports a real compatible configuration by file and HTTP(S) URL.
2. At least one configured VOD source can search, open a detail page, select an episode, resolve a source and play successfully.
3. Live TV, source switching, favourites, history, resume and persistent poster-wall preferences work.
4. JS/Python Spider support and the declared WebHome/local-service capabilities pass their compatibility fixtures.
5. Both x64 and x86 self-contained builds pass startup and playback smoke checks.
6. The public source tree contains dependency/provenance and licence notices required for all incorporated components.

Internal automated tests and controlled fixture data are required to meet this bar, but they are not shipped or labelled as a user-facing test product.

## Implementation sequence

1. Replace the current configuration envelope with a TVBox JSON model, preserving imported source text and profile-level settings.
2. Implement VOD/live catalogue contracts, TVBox provider execution and parser resolution.
3. Replace the placeholder poster wall with data-bound real results and cover-image caching.
4. Add LibVLC playback and progress persistence, then complete core acceptance checks.
5. Add Spider runtimes, WebHome bridge and remaining local-only advanced services.
6. Conduct a licence audit, package both architectures and create the first formal release EXEs only after the quality bar passes.
