# Native Mac releases

`build_release.py` is the signed release path. The older `build_mac.sh` is an
ad-hoc developer build and is not the production packaging command.

Use a fresh private source mirror containing the exact maintained `Mac`, `Core`,
`SharedUI`, `Stage/diskdefs*.cfg` and `LICENSE` inputs, plus their
`PRIVATE-SOURCE.json` inventory. Original disks and ROM images are not inputs.
The inventory contains each relative file path, size and SHA-256. Retain it with
the final build evidence; it is not a public download.

Run on the Mac, using the SDK matching the requested architecture:

```sh
python3 Mac/build_release.py build --rid osx-x64 \
  --out /Volumes/External/ChatGPT/TASK/artifact \
  --dotnet /path/to/x64/dotnet
```

Builds, SDK intermediates, temporary files, caches and qualification profiles
stay on the external volume. The builder publishes once and keeps every
required runtime member. A universal native UI library is allowed when it
contains the requested architecture; the app host and .NET runtime must match
that architecture exactly.

Qualify the actual published assemblies before packaging. The required
`native-checks.json` identifies the application assembly SHA-256, version, RID
and passing native checks. Test image/report/rescue/cancellation behaviour and
the UI layout, themes, settings and Intel/Apple Silicon updater selection.
Use an isolated external test profile; on macOS, `CFFIXED_USER_HOME` is needed
alongside `HOME` to isolate the Foundation application-data path. Preserve the
user's real settings, disks and originals.

Then package the same completed application:

```sh
python3 Mac/build_release.py package --rid osx-x64 \
  --out /Volumes/External/ChatGPT/TASK/artifact \
  --dotnet /path/to/x64/dotnet \
  --sign-identity 'Developer ID Application: YOUR IDENTITY' \
  --notary-profile YOUR_KEYCHAIN_PROFILE
```

This signs the complete bundle, requires Apple notarization, staples the ticket,
checks Gatekeeper, and verifies ZIP extraction including executable permissions
and signatures. Keep the completed receipt and exact archive. Public packages
contain the app and required licenses, without source-code bundles, loose guides,
commercial images or ROM libraries. The self-contained .NET 10 release declares
macOS 14 or newer.

If publishing completed but later envelope validation failed, `build
--resume-publish` checks the complete saved output inventory and original source
identities before resuming. It does not skip native qualification or repeat
unchanged compilation. Preserve failed logs and the completed-publish receipt.

Publish only the requested architecture. Merge that key into freshly fetched
S3 and tracked website feeds, preserving every other platform field. Upload and
verify the immutable archive before promoting feeds or website links. Deploy the
website through its guarded `npm run deploy:production` command, then verify the
actual READY deployment, public bytes and compiled updater without installing.
Rosetta checks are software evidence, not physical Intel or floppy-drive tests.
