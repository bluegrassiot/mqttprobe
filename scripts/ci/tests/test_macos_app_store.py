#!/usr/bin/env python3
"""Source-contract tests for the iOS and Mac App Store TestFlight workflows.

Run: python -m unittest discover -s scripts/ci/tests
"""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
WORKFLOWS = ROOT / ".github" / "workflows"
IOS_WORKFLOW = WORKFLOWS / "build-ios.yml"
MAC_WORKFLOW = WORKFLOWS / "build-macos-app-store.yml"
DIRECT_MAC_WORKFLOW = WORKFLOWS / "build-macos.yml"

IOS_JOB = "build-ios-archive"
MAC_JOB = "build-macos-app-store"
VALIDATION_JOB = "validate-source"

PREFLIGHT_STEP = "Preflight required secrets"
KEY_STEP = "Prepare App Store Connect API key"
SUMMARY_STEP = "Summarize"
CLEANUP_STEP = "Clean up signing material"

IOS_SECRETS = (
    "IOS_DIST_CERT_P12",
    "IOS_DIST_CERT_PASSWORD",
    "IOS_PROVISION_PROFILE",
    "NOTARY_KEY_ID",
    "NOTARY_ISSUER_ID",
    "NOTARY_KEY_P8",
)

MAC_SECRETS = (
    "IOS_DIST_CERT_P12",
    "IOS_DIST_CERT_PASSWORD",
    "MAC_APP_STORE_INSTALLER_CERT_P12",
    "MAC_APP_STORE_INSTALLER_CERT_PASSWORD",
    "MAC_APP_STORE_PROVISION_PROFILE",
    "NOTARY_KEY_ID",
    "NOTARY_ISSUER_ID",
    "NOTARY_KEY_P8",
)


def load_job(name, text):
    lines = text.splitlines()
    start = lines.index(f"  {name}:")
    block = [lines[start]]
    for line in lines[start + 1:]:
        if line.startswith("  ") and not line.startswith("   "):
            break
        block.append(line)
    return "\n".join(block)


def load_step(name, job):
    lines = job.splitlines()
    start = lines.index(f"      - name: {name}")
    block = [lines[start]]
    for line in lines[start + 1:]:
        if line.startswith("      - "):
            break
        block.append(line)
    return "\n".join(block)


def step_run(step):
    lines = step.splitlines()
    for marker in ("        run: |", "        run: >-", "        run: >"):
        try:
            start = lines.index(marker)
        except ValueError:
            continue
        return "\n".join(lines[start + 1:])
    raise AssertionError("step has no run command")


def step_names(job):
    return re.findall(r"^      - name: (.+)$", job, re.MULTILINE)


class AppleWorkflowInputTests(unittest.TestCase):
    def setUp(self):
        self.workflows = {
            IOS_WORKFLOW: IOS_WORKFLOW.read_text(encoding="utf-8"),
            MAC_WORKFLOW: MAC_WORKFLOW.read_text(encoding="utf-8"),
        }

    def test_workflows_accept_manual_and_release_call_triggers_only(self):
        for path, text in self.workflows.items():
            with self.subTest(workflow=path.name):
                self.assertIn("  workflow_dispatch:", text)
                self.assertIn("  workflow_call:", text)
                for trigger in (
                    "push:",
                    "pull_request:",
                    "pull_request_target:",
                    "schedule:",
                    "release:",
                    "workflow_run:",
                ):
                    self.assertNotIn(f"\n  {trigger}", text)

    def test_manual_inputs_stay_required(self):
        for path, text in self.workflows.items():
            with self.subTest(workflow=path.name):
                dispatch = text.split("  workflow_dispatch:", 1)[1].split(
                    "  workflow_call:", 1
                )[0]
                self.assertIn("      version:", dispatch)
                self.assertIn("      build_number:", dispatch)
                self.assertEqual(dispatch.count("required: true"), 2)

    def test_release_call_accepts_version_and_optional_build_override(self):
        for path, text in self.workflows.items():
            with self.subTest(workflow=path.name):
                call = text.split("  workflow_call:", 1)[1].split("\nenv:\n", 1)[0]
                self.assertIn("      version:", call)
                self.assertIn("        required: true", call)
                self.assertIn("      build_number:", call)
                self.assertIn("        required: false", call)
                self.assertIn("        default: ''", call)
                self.assertNotIn("    secrets:", call)
                self.assertIn("    outputs:", call)
                for output in ("source_sha", "tag", "marketing"):
                    self.assertIn(
                        f"value: ${{{{ jobs.validate-source.outputs.{output} }}}}",
                        call,
                    )
                self.assertIn(
                    "value: ${{ jobs.build-ios-archive.outputs.build }}"
                    if path == IOS_WORKFLOW
                    else "value: ${{ jobs.build-macos-app-store.outputs.build }}",
                    call,
                )

    def test_validation_job_is_secret_free_and_cheap(self):
        for path, text in self.workflows.items():
            with self.subTest(workflow=path.name):
                job = load_job(VALIDATION_JOB, text)
                self.assertIn("runs-on: ubuntu-24.04", job)
                self.assertNotIn("environment:", job)
                self.assertNotIn("${{ secrets.", job)
                self.assertIn("fetch-depth: 0", job)
                self.assertIn("persist-credentials: false", job)
                self.assertNotIn("submodules:", job)
                self.assertIn("permissions:\n      contents: read", job)

    def test_validation_pins_tag_to_main_commit(self):
        for path, text in self.workflows.items():
            with self.subTest(workflow=path.name):
                job = load_job(VALIDATION_JOB, text)
                step = load_step("Validate tag and source", job)
                body = step_run(step)
                self.assertIn("GIT_TERMINAL_PROMPT: '0'", step)
                for marker in (
                    "refs/tags/$INPUT_VERSION",
                    "^{commit}",
                    "refs/heads/main:refs/remotes/origin/main",
                    "--is-shallow-repository",
                    "if ! git fetch --no-tags --prune origin",
                    "MAIN_SHA",
                    "merge-base --is-ancestor",
                    '"push"',
                    '"workflow_dispatch"',
                    '"refs/heads/main"',
                    "^refs/heads/release/[^/]+$",
                    "BASE_REF",
                    "refs/heads/$BASE_REF:refs/remotes/origin/$BASE_REF",
                    "BASE_SHA",
                    'SOURCE_SHA="$DISPATCH_SHA"',
                    "REF_NAME",
                    "source_sha",
                    "marketing=${INPUT_VERSION#v}",
                ):
                    self.assertIn(marker, body)
                self.assertIn(
                    "^v(0|[1-9][0-9]{0,3})\\.(0|[1-9][0-9]{0,3})\\.(0|[1-9][0-9]{0,3})$",
                    body,
                )
                self.assertIn('"$INPUT_VERSION" != "v0.0.0"', body)
                self.assertNotIn("apple-build-number.py", body)
                self.assertNotIn("scripts/ci/", body)
                self.assertNotIn("git rev-parse --verify --quiet origin/main", body)
                self.assertNotIn("|| true", body)
                self.assertNotIn("::warning::", body)
                self.assertNotIn("Validate version and build number", job)

    def test_signing_job_uses_validated_source(self):
        for path, text, job_name in (
            (IOS_WORKFLOW, self.workflows[IOS_WORKFLOW], IOS_JOB),
            (MAC_WORKFLOW, self.workflows[MAC_WORKFLOW], MAC_JOB),
        ):
            with self.subTest(workflow=path.name):
                job = load_job(job_name, text)
                self.assertIn("needs: validate-source", job)
                self.assertIn("environment: ios-signing", job)
                self.assertNotIn("if: github.ref == 'refs/heads/main'", job)
                self.assertIn(
                    "ref: ${{ needs.validate-source.outputs.source_sha }}", job
                )
                self.assertIn("submodules: recursive", job)
                self.assertIn("persist-credentials: false", job)
                self.assertIn("permissions:\n      contents: read", job)

    def test_signing_job_recomputes_build_on_each_attempt(self):
        for path, text, job_name in (
            (IOS_WORKFLOW, self.workflows[IOS_WORKFLOW], IOS_JOB),
            (MAC_WORKFLOW, self.workflows[MAC_WORKFLOW], MAC_JOB),
        ):
            with self.subTest(workflow=path.name):
                job = load_job(job_name, text)
                step = load_step(
                    "Derive and validate version and build number", job
                )
                body = step_run(step)
                self.assertIn("RUN_ATTEMPT", step)
                self.assertIn("apple-build-number.py", body)
                self.assertIn("--run-attempt", body)
                self.assertIn("ios-version.py", body)
                self.assertIn(
                    "marketing: ${{ steps.version.outputs.marketing }}", job
                )
                self.assertIn("build: ${{ steps.version.outputs.build }}", job)
                self.assertLess(
                    job.index("Derive and validate version and build number"),
                    job.index(
                        "Build signed archive"
                        if job_name == IOS_JOB
                        else "Build signed Mac App Store package"
                    ),
                )

    def test_no_public_artifact_or_release_publish_step(self):
        for path, text in self.workflows.items():
            with self.subTest(workflow=path.name):
                for forbidden in (
                    "actions/upload-artifact",
                    "action-gh-release",
                    "upload-release-asset",
                    "gh release",
                    "publish/ios",
                    "Transporter",
                ):
                    self.assertNotIn(forbidden, text)

    def test_summaries_record_tag_sha_and_upload_uncertainty(self):
        for path, text in self.workflows.items():
            with self.subTest(workflow=path.name):
                job = load_job(
                    IOS_JOB if path == IOS_WORKFLOW else MAC_JOB, text
                )
                step = load_step(SUMMARY_STEP, job)
                self.assertIn("SOURCE_TAG: ${{ needs.validate-source.outputs.tag }}", step)
                self.assertIn(
                    "SOURCE_SHA: ${{ needs.validate-source.outputs.source_sha }}", step
                )
                self.assertIn(
                    "MARKETING_VERSION: ${{ steps.version.outputs.marketing }}", step
                )
                self.assertIn(
                    "BUILD_NUMBER: ${{ steps.version.outputs.build }}", step
                )
                self.assertIn("UPLOAD_RESULT: ${{ steps.upload.outcome }}", step)
                body = step_run(step)
                for outcome in ("success)", "skipped)", "cancelled)", "*)"):
                    self.assertIn(outcome, body)
                self.assertIn("Upload did not complete successfully", body)
                self.assertIn("Apple may have received the build", body)
                self.assertIn("Processing is automatic", body)
                self.assertIn("not published as a workflow artifact", body)


class IosWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.text = IOS_WORKFLOW.read_text(encoding="utf-8")
        self.job = load_job(IOS_JOB, self.text)

    def test_preflight_checks_every_required_secret_before_the_build(self):
        step = load_step(PREFLIGHT_STEP, self.job)
        body = step_run(step)
        for secret in IOS_SECRETS:
            self.assertIn(f"{secret}: ${{{{ secrets.{secret} }}}}", step)
            self.assertIn(secret, body)
        self.assertIn("openssl pkey -noout", body)
        self.assertLess(
            self.job.index(PREFLIGHT_STEP),
            self.job.index("Build signed archive"),
        )

    def test_preflight_validates_api_key_id_and_issuer_shape(self):
        body = step_run(load_step(PREFLIGHT_STEP, self.job))
        self.assertIn("^[A-Za-z0-9]+$", body)
        self.assertIn(
            "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            body,
        )
        for name in (
            "NOTARY_KEY_ID",
            "NOTARY_ISSUER_ID",
            "NOTARY_KEY_P8",
        ):
            self.assertNotIn(f'echo "${name}', body)

    def test_step_scripts_use_env_vars_for_secrets(self):
        for name in (PREFLIGHT_STEP, KEY_STEP, "Upload to TestFlight"):
            self.assertNotIn("${{ secrets.", step_run(load_step(name, self.job)))

    def test_shared_toolchain_and_legal_notices(self):
        self.assertIn("uses: ./.github/actions/select-xcode", self.job)
        self.assertIn("workload: maui-ios", self.job)
        self.assertIn("scripts/legal/notices.py profiles-check --scenario maui-ios-arm64", self.job)

    def test_api_key_is_written_to_isolated_runner_temp_directory(self):
        step = load_step(KEY_STEP, self.job)
        self.assertIn('API_KEY_DIR="$RUNNER_TEMP/', step)
        self.assertIn("umask 077", step)
        self.assertIn("AuthKey_$API_KEY_ID.p8", step)
        self.assertIn("chmod 600", step)
        self.assertNotIn('echo "$API_PRIVATE_KEY', step)

    def test_upload_pins_legacy_altool_with_api_key_not_password(self):
        upload = load_step("Upload to TestFlight", self.job)
        self.assertIn("xcrun altool --use-old-altool --upload-app --type ios", upload)
        self.assertIn('--apiKey "$API_KEY_ID"', upload)
        self.assertIn('--apiIssuer "$API_ISSUER_ID"', upload)
        self.assertIn(
            "API_PRIVATE_KEYS_DIR: ${{ steps.api-key.outputs.API_KEY_DIR }}",
            upload,
        )
        self.assertNotIn("--password", upload)
        self.assertNotIn("APPLE_ID", self.text)
        for forbidden in (
            "apple-actions/upload-testflight-build",
            "fastlane",
            "pilot",
        ):
            self.assertNotIn(forbidden, self.text)

    def test_upload_is_bounded_and_fails_without_suppression(self):
        upload = load_step("Upload to TestFlight", self.job)
        self.assertIn("timeout-minutes: 20", upload)
        self.assertNotIn("continue-on-error", self.text)
        self.assertNotIn("|| true", step_run(upload))

    def test_upload_runs_after_verification(self):
        upload_at = self.job.index("Upload to TestFlight")
        for name in (
            "Verify archive metadata and signature",
            "Verify privacy manifest",
            KEY_STEP,
        ):
            self.assertLess(self.job.index(name), upload_at)

    def test_cleanup_runs_always_and_removes_key_and_ipa(self):
        step = load_step(CLEANUP_STEP, self.job)
        self.assertIn("if: always()", step)
        self.assertIn('"$RUNNER_TEMP/appstoreconnect-keys"', step)
        self.assertIn('"$RUNNER_TEMP/ios-archive"', step)
        self.assertIn('rm -f "$IPA_PATH"', step)
        self.assertIn('rm -f "$SOURCE_IPA_PATH"', step)
        self.assertIn("security delete-keychain", step)
        self.assertIn("Provisioning Profiles/", step)


class MacWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.text = MAC_WORKFLOW.read_text(encoding="utf-8")
        self.job = load_job(MAC_JOB, self.text)

    def test_preflight_checks_every_required_secret_before_the_build(self):
        step = load_step(PREFLIGHT_STEP, self.job)
        body = step_run(step)
        for secret in MAC_SECRETS:
            self.assertIn(f"{secret}: ${{{{ secrets.{secret} }}}}", step)
            self.assertIn(secret, body)
        self.assertIn("openssl pkey -noout", body)
        self.assertLess(
            self.job.index(PREFLIGHT_STEP),
            self.job.index("Build signed Mac App Store package"),
        )

    def test_uses_shared_app_cert_and_new_installer_profile_secrets(self):
        for name in (
            "IOS_DIST_CERT_P12",
            "IOS_DIST_CERT_PASSWORD",
            "MAC_APP_STORE_INSTALLER_CERT_P12",
            "MAC_APP_STORE_INSTALLER_CERT_PASSWORD",
            "MAC_APP_STORE_PROVISION_PROFILE",
        ):
            self.assertIn(f"{name}: ${{{{ secrets.{name} }}}}", self.text)
        for mapping in (
            "API_KEY_ID: ${{ secrets.NOTARY_KEY_ID }}",
            "API_PRIVATE_KEY: ${{ secrets.NOTARY_KEY_P8 }}",
            "API_ISSUER_ID: ${{ secrets.NOTARY_ISSUER_ID }}",
        ):
            self.assertIn(mapping, self.text)
        for forbidden in ("MACOS_CERT_P12", "MACOS_INSTALLER_CERT_P12",
                          "MACOS_CERT_PASSWORD", "APP_STORE_CONNECT_KEY_ID",
                          "APP_STORE_CONNECT_ISSUER_ID", "APP_STORE_CONNECT_PRIVATE_KEY"):
            self.assertNotIn(forbidden, self.text)

    def test_expected_signing_identities(self):
        self.assertIn(
            'SIGN_APP_IDENTITY: "Apple Distribution: Bluegrass IOT LLC (XVKMB6CQ72)"',
            self.text,
        )
        self.assertIn(
            'SIGN_INSTALL_IDENTITY: "3rd Party Mac Developer Installer: Bluegrass IOT LLC (XVKMB6CQ72)"',
            self.text,
        )
        identities = step_run(load_step("Verify signing identities", self.job))
        self.assertIn("Apple Distribution", identities)
        self.assertIn("3rd Party Mac Developer Installer", identities)

    def test_profile_is_validated_for_macos_and_installed(self):
        step = load_step("Install and verify provisioning profile", self.job)
        self.assertIn("--platform macos", step)
        self.assertIn('--name "$MAC_PROVISION_PROFILE_NAME"', step)
        self.assertIn("--certificate-sha256", step)
        self.assertIn(".provisionprofile", step)
        self.assertIn('MAC_PROVISION_PROFILE_NAME: "MQTTProbe Mac App Store"', self.text)

    def test_build_is_signed_universal_store_package(self):
        build = step_run(load_step("Build signed Mac App Store package", self.job))
        for marker in (
            "-f:net10.0-maccatalyst",
            "-p:MqttProbeMauiMacTargetFrameworksOverride=net10.0-maccatalyst",
            "maccatalyst-x64;maccatalyst-arm64",
            "-p:MqttProbeMacAppStore=true",
            "-p:CreatePackage=true",
            "-p:EnableCodeSigning=true",
            "-p:EnablePackageSigning=true",
            "-p:UseHardenedRuntime=false",
            "-p:CodesignEntitlements=Platforms/MacCatalyst/AppStoreEntitlements.plist",
            "-p:PackageSigningKey=",
            "ApplicationDisplayVersion=${{ steps.version.outputs.marketing }}",
            "ApplicationVersion=${{ steps.version.outputs.build }}",
        ):
            self.assertIn(marker, build)
        self.assertNotIn("MtouchLink", build)
        self.assertNotIn("UseHardenedRuntime=true", build)
        self.assertNotIn("vpk pack", self.text)
        self.assertNotIn("notarytool", self.text)

    def test_legal_checks_use_the_store_package_scenario(self):
        legal = step_run(load_step("Verify legal notices", self.job))
        commands = [line.strip() for line in legal.splitlines() if line.strip()]
        self.assertEqual(
            commands,
            [
                "python3 scripts/legal/notices.py profiles-check --scenario maui-maccatalyst-app-store",
                "python3 scripts/legal/notices.py verify --scenario maui-maccatalyst-app-store",
            ],
        )
        self.assertNotIn(
            "continue-on-error", load_step("Verify legal notices", self.job)
        )

    def test_store_publish_is_immediately_followed_by_legal_checks(self):
        names = step_names(self.job)
        build_at = names.index("Build signed Mac App Store package")
        self.assertEqual(names[build_at + 1], "Verify legal notices")
        build = step_run(load_step("Build signed Mac App Store package", self.job))
        self.assertIn("-p:MqttProbeMacAppStore=true", build)

    def test_direct_catalyst_workflow_keeps_default_scenario(self):
        text = DIRECT_MAC_WORKFLOW.read_text(encoding="utf-8")
        job = load_job("build-maui-macos", text)
        legal = step_run(load_step("Verify legal notices", job))
        commands = [line.strip() for line in legal.splitlines() if line.strip()]
        self.assertEqual(
            commands,
            [
                "python3 scripts/legal/notices.py profiles-check --scenario maui-maccatalyst",
                "python3 scripts/legal/notices.py verify --scenario maui-maccatalyst",
            ],
        )
        publish_at = job.index("Publish (unsigned")
        legal_at = job.index("Verify legal notices")
        self.assertNotIn("MqttProbeMacAppStore", job[publish_at:legal_at])

    def test_package_verification_covers_signature_entitlements_and_slices(self):
        build = step_run(load_step("Build signed Mac App Store package", self.job))
        self.assertIn("MqttProbeMacAppStore=true", build)
        verify = step_run(load_step("Verify package and app signature", self.job))
        for marker in (
            'pkgutil --check-signature "$PKG_PATH"',
            'pkgutil --expand-full "$PKG_PATH" "$UNPACK"',
            "codesign --verify --deep --strict",
            "Authority=$SIGN_APP_IDENTITY",
            "TeamIdentifier=$APPLE_TEAM_ID",
            "com.apple.security.app-sandbox",
            "com.apple.security.network.client",
            "com.apple.security.files.user-selected.read-write",
            "get-task-allow",
            "com.apple.security.get-task-allow",
            "debug entitlements are not false",
            "conflicting application-identifier entitlements",
            "plutil -convert json",
            "com.apple.developer.team-identifier",
            "embedded.provisionprofile",
            "--platform macos",
            "lipo -archs",
            "x86_64",
            "arm64",
            "Velopack/update artifact",
        ):
            self.assertIn(marker, verify)
        self.assertNotIn('pkgutil --verify "$PKG_PATH"', verify)
        self.assertNotIn('mkdir -p "$UNPACK"', verify)
        self.assertLess(
            verify.index('rm -rf "$UNPACK"'),
            verify.index('pkgutil --expand-full "$PKG_PATH" "$UNPACK"'),
        )
        privacy = step_run(load_step("Verify privacy manifest", self.job))
        self.assertIn("ios-privacy-manifest.py", privacy)
        self.assertIn("PrivacyInfo.xcprivacy", privacy)

    def test_signed_app_entitlement_script_rejects_debug_and_conflicting_ids(self):
        verify = step_run(load_step("Verify package and app signature", self.job))
        start = verify.index("printf '%s\\n'")
        end = verify.index('} > "$RUNNER_TEMP/check-entitlements.py"')
        script = "\n".join(
            re.findall(r"^\s*'(.*)'\s*\\?$", verify[start:end], re.MULTILINE)
        )
        self.assertIn("com.apple.security.get-task-allow", script)
        self.assertIn("conflicting application-identifier entitlements", script)

        def run(payload):
            return subprocess.run(
                [sys.executable, "-I", "-c", script, "XVKMB6CQ72.com.bluegrassiot.mqttprobe"],
                input=json.dumps(payload),
                capture_output=True,
                text=True,
            )

        accepted = run(
            {
                "get-task-allow": False,
                "com.apple.application-identifier": "XVKMB6CQ72.com.bluegrassiot.mqttprobe",
            }
        )
        self.assertEqual(accepted.returncode, 0, accepted.stderr)

        for payload in (
            {"com.apple.security.get-task-allow": True},
            {"get-task-allow": 0},
            {
                "com.apple.application-identifier": "XVKMB6CQ72.com.bluegrassiot.mqttprobe",
                "application-identifier": "XVKMB6CQ72.com.example.other",
            },
        ):
            with self.subTest(payload=payload):
                result = run(payload)
                self.assertEqual(result.returncode, 1, result.stderr)

    def test_upload_uses_legacy_altool_for_macos(self):
        upload = load_step("Upload to TestFlight", self.job)
        self.assertIn("xcrun altool --use-old-altool --upload-app --type macos", upload)
        self.assertIn('--apiKey "$API_KEY_ID"', upload)
        self.assertIn('--apiIssuer "$API_ISSUER_ID"', upload)
        self.assertIn(
            "API_PRIVATE_KEYS_DIR: ${{ steps.api-key.outputs.API_KEY_DIR }}",
            upload,
        )
        self.assertNotIn("--password", upload)

    def test_upload_runs_after_package_and_privacy_verification(self):
        upload_at = self.job.index("Upload to TestFlight")
        for name in (
            "Verify package and app signature",
            "Verify privacy manifest",
            KEY_STEP,
        ):
            self.assertLess(self.job.index(name), upload_at)

    def test_cleanup_runs_always_and_removes_key_and_package(self):
        step = load_step(CLEANUP_STEP, self.job)
        self.assertIn("if: always()", step)
        self.assertIn('"$RUNNER_TEMP/appstoreconnect-keys"', step)
        self.assertIn('"$RUNNER_TEMP/macos-package"', step)
        self.assertIn('rm -f "$PKG_PATH"', step)
        self.assertIn('rm -f "$SOURCE_PKG_PATH"', step)
        self.assertIn(".provisionprofile", step)
        self.assertIn("security delete-keychain", step)


if __name__ == "__main__":
    unittest.main()
