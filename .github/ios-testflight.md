# iOS and Mac Catalyst TestFlight builds

The iOS and Mac Catalyst apps are built by two reusable Apple workflows:

- `.github/workflows/build-ios.yml`
- `.github/workflows/build-macos-app-store.yml`

A tag push to `vX.Y.Z` runs `.github/workflows/release.yml`, which calls both
workflows and uploads each build to App Store Connect for TestFlight. The
GitHub release is created only after both Apple uploads are accepted. Neither
app is submitted for review or released automatically, and neither the IPA nor
the Mac App Store package is published as a workflow artifact or release asset.

For pre-release testing, `.github/workflows/testflight.yml` calls the same two
workflows from a manual run on `main`. It needs no tag and creates no GitHub
release.

Only repository administrators should set this up, create release tags, or
dispatch a run.

## Test before tagging

This is the fastest end-to-end check of a release candidate before any
`vX.Y.Z` tag exists.

1. Merge the changes to `main`.
2. Open **Actions > TestFlight (manual) > Run workflow**.
3. Select the `main` branch.
4. Enter `version` as the intended release version, for example `v1.1.0`. It
   must be a strict `vX.Y.Z` label, and no tag is required.
5. Enter `build_number` as an unused whole number from 1 to 9999 for that
   version, higher than every build already uploaded for it.
6. Run the workflow and wait for both Apple uploads to be accepted.
7. Wait for App Store Connect processing, install the build through TestFlight
   on an iPhone and a Mac, and exercise the real UI before tagging.

This path creates no tag, GitHub release, App Store review submission, or
public store publication. Both Apple jobs build the immutable commit SHA recorded
when the workflow was dispatched. Before any repository script or signing step
runs, they refresh `origin/main` and require that SHA to be an ancestor. A
later push does not change what is built.

Passing the signing, profile, bundle, and package checks proves credentials and
packaging, not app behavior. App Store Connect acceptance is known only from
the upload step, and a usable TestFlight build only after processing and an
install on each platform. A green signing job is not proof that the UI works.

## What a tag release does

A stable `vX.Y.Z` tag drives the release. Lightweight and annotated tags work:

- The tag is the release identity. Do not move, delete, or reuse it. A new
  version needs a new tag.
- Both Apple workflows resolve the tag to its commit and
  independently confirm that commit is in `main` before doing any publishing
  work. The build uses that tag commit, not the current tip of `main`.
- Both apps use `X.Y.Z` as the marketing version.
- Each Apple workflow computes its own build number as
  `github.run_number * 10 + github.run_attempt`. The attempt is bounded to 1
  through 9, and the workflow fails rather than wrap or exceed 9999. If the
  computed value reaches that limit, the numbering policy needs a deliberate
  change.
- The GitHub release waits until both Apple uploads are accepted for
  processing. It does not wait for processing to finish, and processing can
  still fail later in App Store Connect.
- Docker image pushes and other platform artifacts are produced earlier in the
  release run. They are not rolled back if a later Apple upload fails.

To retry a release:

- **Re-run failed jobs** keeps a platform that already uploaded successfully
  and rebuilds only the failed one. Its attempt, and therefore its build
  number, increases.
- **Re-run all jobs** rebuilds both platforms with a higher attempt. The
  marketing version stays the same, but the build numbers can differ from the
  first attempt.

Use the same marketing version for retries. A re-run of the same tag waits for
an in-flight release run instead of cancelling it, because a cancelled Apple
upload may still have reached Apple. Never create a new tag or bump the version
only to escape a build-number problem.

The Photino direct-download macOS build and the existing release assets are
unchanged. The Mac Catalyst build is a separate App Store platform build, not a
replacement for the direct download.

## Why Mac Catalyst

The App Store build uses the existing .NET MAUI Mac Catalyst target so the
store app shares the iOS UI and code path and can be packaged and sandboxed as
a Mac App Store app. The Photino desktop build remains the direct-download
channel and is unchanged. This follows the target decision in issue 273.

## 1. Create the protected environment first

Both Apple workflows use the `ios-signing` environment. Create and protect it
before the first Apple run:

1. Open the repository on GitHub, then **Settings > Environments**.
2. Create an environment named `ios-signing`.
3. Under **Deployment branches and tags**, allow `main` and a `v*` tag rule.
   Tag-driven release runs need the tag policy; manual runs need `main`.
4. Reviewers are optional. Adding them delays every Apple upload until someone
   approves it, which is useful if another maintainer is available.
5. Protect `main` and release tag creation so only maintainers can push the
   commits and tags that start a build.

The environment settings are the real protection. The `if` guards and tag
checks in the workflow YAML are defense in depth, not a security boundary. A
run started before the environment exists creates it without protection. Do
not push a release tag or dispatch an Apple workflow until the environment,
branch policy, and tag policy are in place.

A solo maintainer cannot use a required reviewer who is someone else, so keep
the protection in the environment and the branch and tag rules instead of
blocking on review.

## 2. Set up the six environment signing secrets

There are nine secret names in total: six environment signing secrets and
three repository API secrets that already exist.

Add the signing secrets under **Settings > Environments > ios-signing >
Environment secrets**. Enter them in the GitHub UI. Do not paste values into
chat, an issue, a pull request, or a commit.

### iOS signing

| Secret | What it contains |
| --- | --- |
| `IOS_DIST_CERT_P12` | Base64 of a password-protected `.p12` with the Apple Distribution certificate and its private key |
| `IOS_DIST_CERT_PASSWORD` | Password for that `.p12` |
| `IOS_PROVISION_PROFILE` | Base64 of the App Store provisioning profile |

### Mac Catalyst signing

| Secret | What it contains |
| --- | --- |
| `MAC_APP_STORE_INSTALLER_CERT_P12` | Base64 of a password-protected `.p12` with the Mac Installer Distribution certificate and its private key |
| `MAC_APP_STORE_INSTALLER_CERT_PASSWORD` | Password for that `.p12` |
| `MAC_APP_STORE_PROVISION_PROFILE` | Base64 of the Mac App Store provisioning profile |

Keep these six at environment scope so the branch and tag rules apply.

### App Store Connect API key (repository scope)

The workflows use the three repository secrets that already exist for macOS
notarization:

| Secret | What it contains |
| --- | --- |
| `NOTARY_KEY_P8` | Raw PEM text of the `.p8` App Store Connect API key |
| `NOTARY_KEY_ID` | Key ID of that team key |
| `NOTARY_ISSUER_ID` | Issuer ID of that team key |

Both Apple workflows reference them directly. Do not copy them into the
environment, create another key, or read their values. The existing team key
works for both TestFlight uploads, and its **Developer** role is suitable.

The API credential stays repository scoped. The `ios-signing` environment
gates the signing job before it runs, but the key itself is not isolated to
the environment. That is the existing repository setting, not a new exposure
created by these workflows.

You do not need the original downloaded `.p8` file to configure this. The key
material is already in `NOTARY_KEY_P8`. If the downloaded file is lost, that is
a backup or rotation concern, not a workflow prerequisite. Do not revoke or
replace the key as part of this setup; handle later rotation separately.

`MACOS_*` certificate and profile secrets belong to the direct Developer ID
macOS build. Those certificates and profiles are not App Store credentials and
cannot sign the Mac Catalyst package. `NOTARY_*` is the API key exception: it
is reused for both TestFlight uploads.

## 3. Reuse the signing credentials

Start from the identities and profiles that already produced an accepted
submission. Reuse them if they are valid and current. Do not create
replacement certificates by default.

### iOS

The iOS workflow expects exactly:

- Certificate common name:
  `Apple Distribution: Bluegrass IOT LLC (XVKMB6CQ72)`
- App Store provisioning profile name: `MQTTProbe App Store`
- Bundle ID: `com.bluegrassiot.mqttprobe`
- Team ID: `XVKMB6CQ72`

The profile must be an App Store profile for that bundle ID and team, include
the same certificate, and cover iOS. If the existing identity or profile
differs, match it deliberately or update both `SIGN_APP_IDENTITY` and
`IOS_PROVISION_PROFILE_NAME` together. Do not create a new certificate only to
satisfy a name mismatch.

### Mac Catalyst

The Mac workflow expects:

- Installer certificate common name:
  `3rd Party Mac Developer Installer: Bluegrass IOT LLC (XVKMB6CQ72)`
  (portal certificate type **Mac Installer Distribution**)
- Mac App Store provisioning profile name: `MQTTProbe Mac App Store`
- Bundle ID: `com.bluegrassiot.mqttprobe`
- Team ID: `XVKMB6CQ72`

The Mac profile must be a **Mac App Store** profile, not an iOS profile and not
a Developer ID profile. It must match the app, team, and Apple Distribution
certificate, not the installer certificate. The Mac build reuses `IOS_DIST_CERT_P12` and
`IOS_DIST_CERT_PASSWORD` for the Apple Distribution app signing; the
`MAC_APP_STORE_INSTALLER_*` secrets are only for signing the package.

The store package also has a legal package profile, `maui-maccatalyst-app-store`.
It records the reviewed NuGet graph for the store publish, which excludes
Velopack through `MqttProbeMacAppStore=true`.
That profile lives under `legal/profiles`; it is not an Apple provisioning
profile, so there is nothing to export, download, or create in the developer
portal. The six environment signing secrets and the three existing `NOTARY_*`
repository secrets stay as they are.

### Export and encode

In **Keychain Access > My Certificates**, export each certificate with its
private key as a password-protected `.p12`. Confirm the private key is present
before exporting. Download each provisioning profile from the developer portal:
iOS profiles are `.mobileprovision` files and Mac App Store profiles are
`.provisionprofile` files.

Base64-encode one file, paste it into GitHub, then encode the next. `pbcopy`
replaces the clipboard each time.

Certificate:

```bash
base64 -i distribution.p12 | pbcopy
```

iOS profile:

```bash
base64 -i MQTTProbe_App_Store.mobileprovision | pbcopy
```

Mac profile:

```bash
base64 -i MQTTProbe_Mac_App_Store.provisionprofile | pbcopy
```

Repeat the certificate step for the Mac installer certificate. On Linux, use
`base64 -w 0 <file>`.

Delete the local `.p12` and profile files after adding the secrets, or move them
to encrypted storage. The `.p12` files contain private keys.

## 4. Set up the Mac app in App Store Connect

The Mac Catalyst build uses the existing iOS app record with universal
purchase. Before the first Mac upload:

- The App Store Connect app for `com.bluegrassiot.mqttprobe` must already exist
  for iOS.
- Add the macOS platform to that existing app record.
- Enable Mac Catalyst for the existing App ID in the Apple Developer portal.

The Mac app gets its own App Store review. The workflows do not submit either
App Store listing for review and do not release either app.

## 5. Manual uploads without a tag

There are two ways to run a manual upload, both dispatched from `main`:

- **TestFlight (manual)** runs both Apple platforms and is the recommended
  pre-release check.
- Directly dispatching **Build iOS (App Store archive)** or **Build macOS
  (Mac App Store)** uploads that one platform only.

All three use the same inputs:

1. Select the `main` branch.
2. Enter `version` as the intended strict `vX.Y.Z` label, for example
   `v1.2.3`. No tag is required or created.
3. Enter `build_number` as a whole number from 1 to 9999, higher than every
   build already uploaded for that version.
4. Approve the deployment if the environment has required reviewers.

Manual runs build the immutable commit SHA recorded at dispatch. Before any
repository script or signing step runs, the workflow refreshes `origin/main`
and proves that SHA is an ancestor, so a later push does not change what is
built.

Manual runs do not create a tag or GitHub release, do not submit an app for
review, and do not unblock a tag release that is waiting on the other Apple
upload. Push the release tag when the candidate has passed on both platforms.

Builds use the shared Xcode and .NET workload setup pinned by the repository.
Do not change target frameworks or SDK requirements to work around a runner
failure; fix the runner or the shared setup instead.

## 6. Finish in App Store Connect

An accepted upload is not a finished release:

- **Processing** can take from a few minutes to a few hours for each platform.
  A build appears in TestFlight only after processing succeeds.
- **Export compliance** may need an answer for each platform. Answer it for the
  build, or set the compliance information at the app level if it applies to
  every build.
- **Internal testing** uses team members and does not need Beta App Review.
- **External testing** needs Beta App Review, submitted manually.
- **Mac App Store release** needs its own App Review and manual release.

Submitting either app for review and releasing it remain manual. Check
processing in App Store Connect before relying on a build; a GitHub release can
exist even if TestFlight processing later fails.

## Current status

The code change that adds these workflows is still a pending pull request and
has not yet run in live CI or uploaded a Mac build. The six environment signing
secrets are present, the macOS platform is added to the existing App Store
Connect app record, and the signing profiles match. Merge first, then use
**TestFlight (manual)** for the first live check.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Signing secret is not set in `ios-signing` | Missing or empty environment secret. Add it at environment scope so the protection rules apply |
| `NOTARY_*` secret is not set | The existing repository API key secret is missing. Restore it rather than creating a second key |
| Workflow cannot access the environment on a tag run | The environment policy allows `main` only. Add the `v*` tag rule |
| Tag is not stable, is not in `main`, or moved | Use a stable `vX.Y.Z` tag on a commit contained in `main`. Do not retag |
| Apple Distribution identity not found | The `.p12` holds only a Development certificate, or it was exported without its private key |
| Profile name, team, bundle ID, or platform mismatch | Wrong profile uploaded, or one created for another app, platform, or type |
| Certificate digest mismatch | The profile was created with a different certificate than the one in the `.p12` |
| Mac profile rejected as the wrong type | A Developer ID or iOS profile was uploaded instead of a Mac App Store profile |
| Bundle version already used | Use a higher `build_number` for a manual run, or re-run the release so the attempt increases |
| Run fails near build 9999 | The computed number would exceed 9999. Decide a new numbering policy instead of forcing or moving the tag |
| Upload permission error | The `NOTARY_*` team key role or key details are wrong. Confirm the requirement before changing the role |
