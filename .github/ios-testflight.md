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
workflows from a manual run on `main` or a single-level `release/*` branch, such
as `release/v1.1.0`, once its definition is registered on `main`. It needs no
tag and creates no GitHub release.

Only repository administrators should set this up, create release tags, or
dispatch a run. Every Apple job stops at the `ios-signing` environment and waits
for approval before it imports a certificate, signs anything, or uploads.

## Test before tagging

This is the fastest end-to-end check of a release candidate before any
`vX.Y.Z` tag exists.

1. Push the candidate to `main`, or to a single-level `release/*` branch such
   as `release/v1.1.0`. A release branch run is useful when the candidate is
   not on `main` yet. Nested paths such as `release/1.1/ios` are not supported.
2. Open **Actions > TestFlight (manual) > Run workflow**. This entry point
   appears only after its workflow definition is on `main`; see below.
3. Select `main` or the `release/*` branch you pushed to. That branch must
   already contain the workflow file. See
   [Before the first manual run](#before-the-first-manual-run).
4. Enter `version` as the intended release version, for example `v1.1.0`. It
   must be a strict `vX.Y.Z` label, and no tag is required.
5. Enter `build_number` as an unused whole number from 1 to 9999 for that
   version, higher than every build already uploaded for it.
6. Run the workflow. Both Apple jobs wait at `ios-signing`. Approve each one,
   then wait for both Apple uploads to be accepted.
7. Wait for App Store Connect processing, install the build through TestFlight
   on an iPhone and a Mac, and exercise the real UI before tagging.

This path creates no tag, GitHub release, App Store review submission, or
public store publication. Both Apple jobs build the immutable commit SHA recorded
when the workflow was dispatched. Before any repository script or signing step
runs, they refresh the branch that started the run and require that SHA to be an
ancestor of it. A later push does not change what is built. A stable `v*` tag
run is still checked against `main` only.

Passing the signing, profile, bundle, and package checks proves credentials and
packaging, not app behavior. App Store Connect acceptance is known only from
the upload step, and a usable TestFlight build only after processing and an
install on each platform. A green signing job is not proof that the UI works.

## Before the first manual run

GitHub lists a workflow in the **Run workflow** menu only after the workflow
file exists on the default branch, `main`. A definition that exists only on
`release/v1.1.0` has no manual Run button there, even though the branch holds
the full file. A manual run on `release/v1.1.0` also needs the definition on
`main` first; the selected branch must contain it too.

What is registered on `main` today:

- `build-ios.yml` is already registered. Its version on the selected branch
  determines what it builds and uploads.
- `testflight.yml` and `build-macos-app-store.yml` are not there yet, so
  **TestFlight (manual)** and **Build macOS (Mac App Store)** do not appear in
  the Run workflow menu.

A two-platform manual run needs the coordinator definition on `main` before it
can start. Its relative calls load both Apple workflows from the selected
branch's commit, so those reusable definitions need not also be on `main`.
Merging into `release/v1.1.0` alone does not register the coordinator. Direct
single-platform Mac dispatch separately requires `build-macos-app-store.yml`
on the default branch. No bootstrap trigger has been added for this.

`gh workflow run --ref` does not bypass this registration requirement. No
temporary push-triggered signing path is provided. Once the coordinator is on
`main`, a manual run can select `main` or a `release/*` branch that contains all
three workflows. No other merge is needed for the workflow to run.

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

Both Apple workflows use the `ios-signing` environment. Its rules must exist
before the first Apple run:

1. Open the repository on GitHub, then **Settings > Environments**.
2. Create an environment named `ios-signing`.
3. Under **Deployment branches and tags**, allow `main`, a `release/*` rule,
   and a `v*` tag rule. Tag-driven release runs need the tag policy; manual runs
   need `main` or the release branch.
4. Under **Environment protection rules**, add `bluegrassiot` (GitHub user ID
   123216319) as a required reviewer.
5. Leave **Prevent self-review** off, so the account that dispatches a run can
   approve it too.
6. Leave **Allow administrators to bypass** off, so the approval applies to
   administrators as well.
7. Protect `main`, `release/*`, and release tag creation so only maintainers can
   push the commits and tags that start a build.

The required reviewer is a deliberate speed bump. It makes the signing and
upload step an explicit action instead of a side effect of a dispatch, and it
does not add an independent code review. With self-review off, a solo
maintainer approves their own run and is not blocked waiting for a second
person. Turning self-review on would leave that maintainer unable to approve at
all.

The environment settings are the real protection. The `if` guards and tag
checks in the workflow YAML are defense in depth, not a security boundary. A
run started before the environment exists creates it without protection. Do not
push a release tag or dispatch an Apple workflow until the environment, branch
policy, and tag policy are in place.

## 2. Set up the six environment signing secrets

There are nine secret names in total: six environment signing secrets and
three repository API secrets that already exist.

Add the signing secrets under **Settings > Environments > ios-signing >
Environment secrets**. If they are already present, check them instead of
entering them again. Enter values in the GitHub UI only. Do not paste them into
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

This step is only for replacing a missing or invalid secret. If the existing
secrets still match the credentials above, leave them in place.

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

There are two ways to run a manual upload. Both work from `main` or a
single-level `release/*` branch:

- **TestFlight (manual)** runs both Apple platforms and is the recommended
  pre-release check.
- Directly dispatching **Build iOS (App Store archive)** or **Build macOS
  (Mac App Store)** uploads that one platform only.

All three use the same inputs:

1. Select `main` or a `release/*` branch that contains the workflow file.
2. Enter `version` as the intended strict `vX.Y.Z` label, for example
   `v1.2.3`. No tag is required or created.
3. Enter `build_number` as a whole number from 1 to 9999, higher than every
   build already uploaded for that version.
4. Approve each job when `ios-signing` asks. The two TestFlight jobs ask
   separately; a direct single-platform run asks once, and a tag release reaches
   the same approval before its uploads.

Manual runs build the immutable commit SHA recorded at dispatch. Before any
repository script or signing step runs, the workflow refreshes the branch that
started the run and proves that SHA is an ancestor of it, so a later push does
not change what is built. A stable `v*` tag is checked against `main` only, and
no branch rule relaxes that.

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

The `ios-signing` environment is configured and verified: required reviewer
`bluegrassiot` (GitHub user ID 123216319), self-review allowed, administrator
bypass disabled, and deployment branches `main`, `release/*`, and `v*` tags. The
six signing secrets stay at environment scope, and the three `NOTARY_*`
repository secrets stay as they are. Reuse the existing certificates and
profiles in place; no export or profile recreation is part of this setup.

Workflow registration is not complete. `build-ios.yml` is already on `main`, but
the coordinator `testflight.yml` is not, so the two-platform manual run is not
available yet. See
[Before the first manual run](#before-the-first-manual-run).

Registering the coordinator needs no tag or upload. Once it is on `main`, it
can call both reusable workflows from the selected release branch. Until then,
only the already-registered single-platform iOS dispatch is available.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| No **TestFlight (manual)** or **Build macOS (Mac App Store)** entry point | The coordinator or Mac App Store workflow definition is not on `main` yet. Register it there first; there is no bootstrap trigger |
| Job waits on **Waiting for approval** | `ios-signing` has a required reviewer and this run is not approved. Approve it; approving your own run is allowed while Prevent self-review is disabled |
| Manual run on a `release/*` branch is refused | The branch is nested deeper than one level, or the branch is not in the environment deployment policy |
| Signing secret is not set in `ios-signing` | Missing or empty environment secret. Add it at environment scope so the protection rules apply |
| `NOTARY_*` secret is not set | The existing repository API key secret is missing. Restore it rather than creating a second key |
| Workflow cannot access the environment on a tag run | The policy is missing the `v*` tag rule. Allow it alongside `main` and `release/*` |
| Tag is not stable, is not in `main`, or moved | Use a stable `vX.Y.Z` tag on a commit contained in `main`. Do not retag |
| Apple Distribution identity not found | The `.p12` holds only a Development certificate, or it was exported without its private key |
| Profile name, team, bundle ID, or platform mismatch | Wrong profile uploaded, or one created for another app, platform, or type |
| Certificate digest mismatch | The profile was created with a different certificate than the one in the `.p12` |
| Mac profile rejected as the wrong type | A Developer ID or iOS profile was uploaded instead of a Mac App Store profile |
| Bundle version already used | Use a higher `build_number` for a manual run, or re-run the release so the attempt increases |
| Run fails near build 9999 | The computed number would exceed 9999. Decide a new numbering policy instead of forcing or moving the tag |
| Upload permission error | The `NOTARY_*` team key role or key details are wrong. Confirm the requirement before changing the role |
