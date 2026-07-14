# Spider and Native Playback Runtime Design

## Purpose

Add native Windows execution for trusted TVBox JavaScript and Python Spider scripts, and native playback for resolved media. This design is an approved extension of the native formal-release specification.

## Spider dispatch

`WebHtv.Spider` exposes a provider-independent Spider contract for home, category, detail, search, player and proxy operations. A type-3 site is dispatched by its `api` value:

- URLs ending in `.js` use the JavaScript host.
- URLs ending in `.py` use the Python host.
- `csp_` Java/JAR spiders are reported as unsupported until a separate Java compatibility design is approved; they are never reported as successfully executed.

Spider results are validated as TVBox JSON before crossing into the catalogue layer.

## JavaScript host

The JavaScript host uses Jint in the main Windows process. It does not enable CLR access. The only bridged APIs are explicitly approved HTTP, per-profile cache, logging and configuration helpers. Per-call statement, memory and time limits prevent an imported script from exhausting the desktop process.

## Python host

The Python host is a companion Windows-native helper process built for each CPU architecture. It embeds the bundled CPython runtime through Python.NET and communicates with the desktop app through a local JSON-RPC channel. The desktop app treats configured scripts as user-trusted code, as explicitly approved by the user.

Each request has a deadline. If a Python call hangs or the helper exits, the desktop app terminates/restarts the helper as needed and reports the failure only for the affected site. The helper process and CPython runtime are included with the matching x64/x86 package; users do not install Python separately.

## Playback

`WebHtv.Playback` wraps LibVLCSharp.WPF and receives only a resolved playback request. It applies request headers, offers subtitle selection and playback speed, saves progress for resume, and exposes structured failure states for expired URLs, unsupported codecs, network failures and subtitle errors.

The player does not run Spider code. The data flow is configuration -> native HTTP or Spider provider -> normalised catalogue result -> selected episode -> resolver/direct request -> LibVLC playback.

## Packaging and validation

Both x64 and x86 distributions package architecture-matched LibVLC, Jint, CPython and Python.NET dependencies. Internal validation uses legal fixture scripts and legal test media to exercise search, detail, episode extraction, direct playback, subtitle loading, speed and resume. These checks are release gates only; no user-facing build is called a test or preview release.

Logs redact cookies, tokens and credentials. Failures in a script or media request do not terminate the desktop application.
