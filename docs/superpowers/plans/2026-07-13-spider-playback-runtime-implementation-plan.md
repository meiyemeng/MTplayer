# Spider and Native Playback Runtime Implementation Plan

## Scope of this implementation cycle

Implement the JavaScript Spider host and the LibVLC playback foundation first. Add Python as a separate helper-process project after the shared Spider protocol is proven. Java/JAR CSP compatibility remains out of scope for this cycle.

## Steps and acceptance checks

1. Add `WebHtv.Spider` contracts, a Jint host, a constrained script context and TVBox JSON result validation.
   - Acceptance: a trusted fixture JS Spider completes search, detail and player calls without CLR access; timeouts and malformed results return site-scoped errors.
2. Add `WebHtv.Playback`, LibVLCSharp.WPF and architecture-specific LibVLC packages.
   - Acceptance: a legal local media URI can be opened through a native player service, including header forwarding and state callbacks.
3. Connect the catalogue dispatcher to native HTTP and JS Spider sources.
   - Acceptance: type-3 `.js` sources use the Spider host; unsupported `.py` and `csp_` sources identify the missing host accurately.
4. Create `WebHtv.PythonHost` as a dedicated helper process using the shared JSON-RPC protocol and Python.NET.
   - Acceptance: the desktop process detects, times out and restarts a failed Python helper without crashing.
5. Integrate the desktop player view, subtitle/speed/progress controls and release packaging.
   - Acceptance: the formal-release playback criteria pass on both CPU architectures before publishing a usable EXE.

## Dependency policy

Pin package versions in project files after confirming them from their official package publishers. Maintain dependency licence/provenance notices before the formal release.
