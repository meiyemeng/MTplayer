# MT Player Android notice (v1.3.3)

This directory contains the MT Player Android client, derived from
[IsayIsee/TVBoxOS](https://github.com/IsayIsee/TVBoxOS).

The complete corresponding source is included in this directory. It remains
licensed under the upstream **GNU Affero General Public License v3.0**; see
[`LICENSE`](LICENSE). MT Player changes include branding, a single-click
membership entry, member configuration/live-source push reception and Android
update prompts. This application does not ship video content or third-party
configuration sources.

## Build

Use JDK 17 and Android SDK platform 33. Create `local.properties` with the SDK
location, then run:

```powershell
./gradlew.bat :app:assembleNormalRelease
```

The generated release APK must be signed with the MT Player release key before
distribution.
