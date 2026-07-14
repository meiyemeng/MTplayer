# Native Formal Release Implementation Plan

## Principle

No release EXE is labelled or handed off as usable until it satisfies the native formal-release quality bar in the approved design. Each step below is verified against a real compatibility fixture before the next user-visible release is produced.

## Work sequence

1. **Reference-model extraction**
   - Read the declared upstream WebHomeTV/TVBox source and documentation without copying Android-specific runtime code.
   - Record the exact configuration fields, provider contracts, Spider entry points and HTTP/API semantics required for compatibility.
   - Acceptance: a compatibility matrix maps every supported Windows model to its upstream source reference.

2. **TVBox configuration foundation**
   - Replace the current generic JSON envelope with typed TVBox profile, site, live, parser, rule and player-setting models while retaining unknown fields for forward compatibility.
   - Add local and HTTP(S) profile import, validation, safe redirects/content limits and atomic persistence.
   - Acceptance: representative compatible configurations load, round-trip and reject invalid/malicious inputs without overwriting the last valid profile.

3. **Catalogue and parser contracts**
   - Define provider, search, detail, episode, live-channel and resolver interfaces independent of the WPF UI.
   - Implement the native configuration-driven provider path and parser selection/flag/rule handling.
   - Acceptance: fixture providers return normalised search/detail/episode/resolution results.

4. **Real desktop experience**
   - Remove placeholder poster cards. Bind the Chinese poster wall to profile data, search results, favourites and history; download/cache actual cover images safely.
   - Acceptance: empty state contains no fabricated media, while a real configuration drives real covers and navigable content.

5. **Native playback**
   - Integrate LibVLC, subtitles, source switching, speed, retries, progress/history and actionable playback diagnostics.
   - Acceptance: real configured VOD and live sources complete the formal-release playback path on both architectures.

6. **Compatibility extensions**
   - Add JS/Python Spider hosts, then WebHome bridge/extensions, proxy, health scoring, cloud-drive checks, login state and local services.
   - Acceptance: each capability passes an isolated fixture plus its declared compatibility scenario.

7. **Release engineering**
   - Complete licence/provenance inventory, x64/x86 packaging, CI, installer tests and release documentation.
   - Acceptance: a clean machine installation passes the formal-release quality bar before any EXE is called a release.

## Current implementation target

Execute steps 1 and 2 only: obtain the reference sources, create the compatibility matrix, and implement the typed configuration model with its verification fixtures. Do not publish a new user-facing EXE at this point.
