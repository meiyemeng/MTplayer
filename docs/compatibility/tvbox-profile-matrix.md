# TVBox Profile Compatibility Matrix

Reference checkout: `D:\work\webhtv-reference` at the shallow-cloned upstream `main` revision. This document describes compatibility targets; it does not copy Android source code.

| TVBox field or behaviour | Upstream reference | Windows-native target | Status |
|---|---|---|---|
| Root `sites`, `lives`, `parses`, `spider`, `flags`, `rules`, `headers`, `proxy`, `hosts`, `ads`, `doh` | `api/config/VodConfig.java`, `LiveConfig.java` | Typed model with unknown-field preservation | Implemented for import |
| Root `urls` configuration depot | `VodConfig.parseDepot`, `LiveConfig.parseDepot` | Typed depot entries; profile selector before recursive fetch | Model implemented; selector pending |
| Site `key`, `name`, `api`, `ext`, `jar`, search/change flags, categories and headers | `bean/Site.java` | Typed configuration site; native provider dispatch | Model implemented; dispatch pending |
| Live `name`, `url`, `api`, `ext`, headers and player options | `bean/Live.java` | Typed live source and native playlist/provider dispatch | Model implemented; dispatch pending |
| Parser name/type/url/ext/flags/headers | `bean/Parse.java`, `api/SiteApi.java` | Parser selector and native resolver request | Model implemented; resolver pending |
| JS/Python/CSP Spider choice | `api/loader/BaseLoader.java` | Separate native JS/Python adapters; CSP compatibility assessed per runtime | Pending |
| Home/category/detail/search/player operations | `api/SiteApi.java` | Normalised catalogue-provider interface | Pending |
| Playback progress, subtitles and playback failures | Android player stack | LibVLC native playback module | Pending |
| WebHome extensions and local services | `webhome-devkit`, WebHome registry | WebView2 permissioned bridge and local-only services | Pending |

## Import rules implemented now

- The root must be a JSON object; comments and trailing commas are accepted for compatible configuration files.
- Duplicate non-empty site keys are rejected.
- A depot entry without `url` is rejected.
- Unknown root and nested fields survive deserialisation through extension data rather than being discarded by the Windows configuration layer.
- The existing saved configuration remains intact when an import fails validation.
