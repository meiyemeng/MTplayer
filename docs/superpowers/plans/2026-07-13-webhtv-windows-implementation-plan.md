# WebHomeTV Windows Implementation Plan

## Completion target

Deliver a publicly publishable Windows 10/11 desktop application with independent x64 and x86 installer paths. All functions approved in the design remain in scope; the excluded physical remote-control, remote-hosting and cross-device-sync features are not implemented.

## Milestones and acceptance checks

1. **Repository foundation**
   - Create a .NET 8 WPF solution, solution-level documentation, Git ignore rules, build conventions and licence inventory placeholder.
   - Acceptance: `dotnet build` succeeds for x64 and x86 configurations; no user configuration or build output is tracked.

2. **Core state and configuration**
   - Implement domain contracts, SQLite-backed local state, atomic settings/configuration writes, configuration import and validation.
   - Acceptance: malformed imports are rejected without modifying saved data; valid imports survive restart.

3. **Desktop shell and catalogue flow**
   - Implement keyboard/mouse WPF shell, first-run empty state, source selection, search, detail, favourites and history views.
   - Acceptance: the UI is usable with a fixture configuration without Android or television-remote dependencies.

4. **Playback and resolvers**
   - Add LibVLC-based playback, stream parsing, source/episode switching, subtitles, speed control and progress persistence.
   - Acceptance: legal local and HTTP test media play in both architecture builds; faults remain in-app and actionable.

5. **Extensibility**
   - Add JavaScript Spider, Python Spider, WebHome/extension WebView2 bridge, allowed network services and proxy rules.
   - Acceptance: fixture scripts and WebHome fixtures complete documented bridge calls; unapproved bridge calls are rejected.

6. **Advanced local services**
   - Add site-health scoring, cloud-drive checks, login-state learning, diagnostics, local HTTP API and opt-in LAN management.
   - Acceptance: loopback is the default; LAN access requires an explicit setting; sensitive values are redacted from diagnostics.

7. **Packaging and publication readiness**
   - Add x64/x86 installer projects, GitHub Actions CI/release workflows, dependency/licence audit and public contributor/release documentation.
   - Acceptance: clean installation smoke tests pass for both installer architectures and GitHub Release assets can be produced from a tag.

## Execution order for this iteration

Implement milestone 1 and the minimum reusable contracts for milestone 2. Run the project build and architecture-specific checks before reporting. Later milestones are not represented as completed until their acceptance checks pass.
