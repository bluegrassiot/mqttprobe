#!/usr/bin/env python3
"""
Verifies that the app version is correctly embedded in build artifacts.

Publishes each target with the given --version, reads the embedded metadata
from the output DLL/EXE/APK, and asserts it matches. Used locally before
tagging a release to catch packaging regressions early.

By default, publish outputs are written to auto-deleted temp directories.
Use --keep-output to persist them under publish/verify-app-version/ so you
can inspect or launch the built artifacts after the script finishes.

Usage:
  python scripts/packaging/verify-app-version.py --version 1.0.4
  python scripts/packaging/verify-app-version.py --version 1.0.4 --target web --target desktop
  python scripts/packaging/verify-app-version.py --version 1.0.4 --target docker
  python scripts/packaging/verify-app-version.py --version 1.0.4 --target maui-windows
  python scripts/packaging/verify-app-version.py --version 1.0.4 --target android
  python scripts/packaging/verify-app-version.py --version 1.0.4 --target all
  python scripts/packaging/verify-app-version.py --version 1.0.4 --also-negative
  python scripts/packaging/verify-app-version.py --version 1.0.4 --target maui-windows --keep-output

With --keep-output, publish outputs are kept under:
  publish/verify-app-version/{version}/{target}/
e.g. publish/verify-app-version/1.0.4/maui-windows/

Without --keep-output, outputs are deleted after checks complete.
"""

import argparse
import contextlib
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


def find_repo_root() -> Path:
    here = Path(__file__).resolve().parent
    for d in [here, *here.parents]:
        if (d / "MqttProbe.slnx").is_file():
            return d
    raise SystemExit("Could not find repo root (MqttProbe.slnx)")


ROOT = find_repo_root()

ALL_TARGETS = ["web", "desktop", "docker", "maui-windows", "android"]
DEFAULT_TARGETS = ["web", "desktop"]


# ── Version helpers ───────────────────────────────────────────────────────────

def normalize_version(s: str) -> str:
    """Strip +build suffix and trailing .0 for comparison."""
    s = s.strip()
    if "+" in s:
        s = s[: s.index("+")]
    # Collapse X.Y.Z.0 → X.Y.Z (but not X.Y.0 → X.Y)
    if re.fullmatch(r"\d+\.\d+\.\d+\.0", s):
        s = s[:-2]
    return s


def versions_match(actual: str, expected: str) -> bool:
    """Compare two versions after normalization."""
    return normalize_version(actual) == normalize_version(expected)


def application_version_code(version: str) -> int:
    """Parse X.Y.Z → major*10000 + minor*100 + patch (matches release.yml)."""
    parts = version.split(".")
    major, minor, patch = int(parts[0]), int(parts[1]), int(parts[2])
    return major * 10000 + minor * 100 + patch


def _find_shell() -> list[str] | None:
    """Find an available PowerShell executable."""
    for name in ("powershell", "pwsh"):
        if shutil.which(name):
            return [name]
    return None


@contextlib.contextmanager
def _publish_dir(keep_output: bool, version: str, target: str):
    """Yield a publish output directory.

    With keep_output=False, yields a temporary directory that is deleted on exit.
    With keep_output=True, yields ROOT/publish/verify-app-version/{version}/{target}/,
    cleaning it first if it exists.
    """
    if keep_output:
        out = ROOT / "publish" / "verify-app-version" / version / target
        if out.exists():
            shutil.rmtree(out)
        out.mkdir(parents=True)
        yield out
    else:
        with tempfile.TemporaryDirectory(dir=ROOT, prefix=f"publish-verify-{target}-") as tmp:
            yield Path(tmp)


def read_assembly_versions(assembly_path: Path) -> dict[str, str]:
    """Read version metadata from a .NET DLL/EXE cross-platform.

    Tries powershell/pwsh first (reads ProductVersion + Assembly version),
    then falls back to a temp dotnet project with MetadataLoadContext.
    """
    # Try PowerShell approach (fast, no build needed)
    shell = _find_shell()
    if shell:
        info = _read_via_powershell(shell, assembly_path)
        pv = (info.get("ProductVersion") or "").strip()
        av = (info.get("AssemblyVersion") or "").strip()
        # If PS got both, we're done
        if pv and av and "Warning" not in av:
            return info
        # If PS got ProductVersion but not AssemblyVersion, merge with MLC
        if pv and not av:
            mlc_info = _read_via_metadata_load_context(assembly_path)
            mlc_info["ProductVersion"] = pv  # PS ProductVersion is authoritative
            return mlc_info

    # Fallback: dotnet MetadataLoadContext (cross-platform, no deps needed)
    return _read_via_metadata_load_context(assembly_path)


def _read_via_powershell(shell: list[str], assembly_path: Path) -> dict[str, str]:
    """Read versions using PowerShell reflection."""
    # ponytail: inline PS script; Assembly.LoadFrom works when deps are in the same dir (publish output)
    ps = (
        f'$fi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo("{assembly_path}");'
        '"ProductVersion=" + $fi.ProductVersion; '
        '"FileVersion=" + $fi.FileVersion; '
        "try { "
        f'$asm = [System.Reflection.Assembly]::LoadFrom("{assembly_path}"); '
        '"AssemblyVersion=" + $asm.GetName().Version; '
        "$iv = $asm.GetCustomAttribute([System.Reflection.AssemblyInformationalVersionAttribute]); "
        '"InformationalVersion=" + $iv.InformationalVersion '
        "} catch { "
        '"AssemblyVersion="; '
        '"InformationalVersion=" '
        "}"
    )
    result = subprocess.run(
        shell + ["-NoProfile", "-Command", ps],
        capture_output=True, text=True,
    )
    info: dict[str, str] = {}
    for line in result.stdout.strip().splitlines():
        if "=" in line:
            key, _, value = line.partition("=")
            info[key.strip()] = value.strip()
    return info


_MLC_CACHE_DIR: Path | None = None


def _read_via_metadata_load_context(assembly_path: Path) -> dict[str, str]:
    """Read versions using a temp dotnet project with MetadataLoadContext."""
    global _MLC_CACHE_DIR
    if _MLC_CACHE_DIR is None:
        _MLC_CACHE_DIR = Path(tempfile.mkdtemp(prefix="read-asm-ver-"))

    proj_dir = _MLC_CACHE_DIR / "ReadAssemblyVersion"
    proj_dir.mkdir(parents=True, exist_ok=True)

    csproj = proj_dir / "ReadAssemblyVersion.csproj"
    program = proj_dir / "Program.cs"

    if not csproj.exists():
        csproj.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            "<OutputType>Exe</OutputType>"
            "<TargetFramework>net10.0</TargetFramework>"
            "<ImplicitUsings>enable</ImplicitUsings>"
            "</PropertyGroup><ItemGroup>"
            '<PackageReference Include="System.Reflection.MetadataLoadContext" Version="10.0.0" />'
            "</ItemGroup></Project>",
            encoding="utf-8",
        )
        program.write_text(
            "using System.Diagnostics;\n"
            "using System.Reflection;\n"
            "using System.Runtime.InteropServices;\n"
            "var p = args[0];\n"
            'Console.WriteLine("ProductVersion=" + FileVersionInfo.GetVersionInfo(p).ProductVersion);\n'
            "try {\n"
            "  var paths = new List<string> { p, typeof(object).Assembly.Location };\n"
            '  paths.AddRange(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));\n'
            "  var resolver = new PathAssemblyResolver(paths);\n"
            "  using var c = new MetadataLoadContext(resolver);\n"
            "  var a = c.LoadFromAssemblyPath(p);\n"
            '  Console.WriteLine("AssemblyVersion=" + a.GetName().Version);\n'
            "  var infoVer = a.GetCustomAttributesData()\n"
            '    .FirstOrDefault(ca => ca.AttributeType.Name == "AssemblyInformationalVersionAttribute")\n'
            "    ?.ConstructorArguments.FirstOrDefault().Value?.ToString();\n"
            '  Console.WriteLine("InformationalVersion=" + (infoVer ?? ""));\n'
            "} catch (Exception e) {\n"
            '  Console.Error.WriteLine("Error: " + e.Message);\n'
            '  Console.WriteLine("AssemblyVersion=");\n'
            '  Console.WriteLine("InformationalVersion=");\n'
            "}\n",
            encoding="utf-8",
        )
        subprocess.run(["dotnet", "restore", str(csproj)], capture_output=True, text=True)

    result = subprocess.run(
        ["dotnet", "run", "--project", str(csproj), "--", str(assembly_path)],
        capture_output=True, text=True,
    )
    info: dict[str, str] = {}
    for line in result.stdout.strip().splitlines():
        if "=" in line:
            key, _, value = line.partition("=")
            info[key.strip()] = value.strip()
    return info


# ── Target runners ────────────────────────────────────────────────────────────

def run_dotnet_publish(csproj: str, output_dir: Path, version: str | None = None,
                       extra_args: list[str] | None = None) -> bool:
    """Run dotnet publish and return True on success."""
    args = ["dotnet", "publish", str(ROOT / csproj), "-c", "Release", "-o", str(output_dir)]
    if version:
        args.extend(["-p:Version=" + version])
    if extra_args:
        args.extend(extra_args)
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode != 0:
        # Show last few lines of stderr for diagnostics
        err = (result.stderr or result.stdout).strip().splitlines()
        for line in err[-5:]:
            print(f"    {line}")
    return result.returncode == 0


def verify_web(version: str, also_negative: bool, keep_output: bool = False) -> list[tuple]:
    """Verify web target. Returns list of (PASS|FAIL|SKIP, reason [, kept_path])."""
    results: list[tuple] = []
    with _publish_dir(keep_output, version, "web") as out:
        if not run_dotnet_publish("src/MqttProbe.Web/MqttProbe.Web.csproj", out, version):
            results.append(("FAIL", "web: dotnet publish failed"))
            return results

        dll = out / "MqttProbe.Web.dll"
        if not dll.exists():
            results.append(("FAIL", "web: MqttProbe.Web.dll not found after publish"))
            return results

        # Web AppInfoService: ProductVersion (via FileVersionInfo), then InformationalVersion
        info = read_assembly_versions(dll)
        pv = info.get("ProductVersion", "")
        iv = info.get("InformationalVersion", "")
        matched = False
        kept = str(out.relative_to(ROOT)) if keep_output else None
        if versions_match(pv, version):
            results.append(("PASS", f"web: ProductVersion={pv}", kept))
            matched = True
        if versions_match(iv, version):
            results.append(("PASS", f"web: InformationalVersion={iv}", kept))
            matched = True
        if not matched:
            results.append(("FAIL",
                f"web: expected {version}, got ProductVersion={pv} InformationalVersion={iv}"))

        if also_negative:
            neg_out = (out.parent / f"{out.name}-unstamped") if keep_output else out / "unstamped"
            if not run_dotnet_publish("src/MqttProbe.Web/MqttProbe.Web.csproj", neg_out):
                results.append(("FAIL", "web (negative): dotnet publish without version failed"))
            else:
                neg_dll = neg_out / "MqttProbe.Web.dll"
                neg_info = read_assembly_versions(neg_dll)
                neg_pv = neg_info.get("ProductVersion", "")
                if versions_match(neg_pv, "0.0.0"):
                    results.append(("PASS", f"web (negative): unstamped ProductVersion={neg_pv}"))
                else:
                    results.append(("FAIL", f"web (negative): expected 0.0.0, got {neg_pv}"))

    return results


def verify_desktop(version: str, also_negative: bool, keep_output: bool = False) -> list[tuple]:
    """Verify desktop target. Returns list of (PASS|FAIL|SKIP, reason [, kept_path])."""
    results: list[tuple] = []
    with _publish_dir(keep_output, version, "desktop") as out:
        if not run_dotnet_publish("src/MqttProbe.Desktop/MqttProbe.Desktop.csproj", out, version):
            results.append(("FAIL", "desktop: dotnet publish failed"))
            return results

        exe = out / "MqttProbe.Desktop.dll"
        if not exe.exists():
            exe = out / "MqttProbe.Desktop.exe"
        if not exe.exists():
            results.append(("FAIL", "desktop: output binary not found after publish"))
            return results

        # DesktopAppInfoService: AppVersionResolver.Resolve(Assembly.GetName().Version)
        info = read_assembly_versions(exe)
        av = info.get("AssemblyVersion", "")
        if versions_match(av, version):
            kept = str(out.relative_to(ROOT)) if keep_output else None
            results.append(("PASS", f"desktop: AssemblyVersion={av}", kept))
        else:
            results.append(("FAIL",
                f"desktop: expected {version}, got AssemblyVersion={av} "
                f"(DesktopAppInfoService uses AppVersionResolver.Resolve)"))

        if also_negative:
            neg_out = (out.parent / f"{out.name}-unstamped") if keep_output else out / "unstamped"
            if not run_dotnet_publish("src/MqttProbe.Desktop/MqttProbe.Desktop.csproj", neg_out):
                results.append(("FAIL", "desktop (negative): dotnet publish without version failed"))
            else:
                neg_exe = neg_out / "MqttProbe.Desktop.dll"
                if not neg_exe.exists():
                    neg_exe = neg_out / "MqttProbe.Desktop.exe"
                neg_info = read_assembly_versions(neg_exe)
                neg_av = neg_info.get("AssemblyVersion", "")
                if versions_match(neg_av, "0.0.0"):
                    results.append(("PASS", f"desktop (negative): unstamped AssemblyVersion={neg_av}"))
                else:
                    results.append(("FAIL", f"desktop (negative): expected 0.0.0, got {neg_av}"))

    return results


def verify_docker(version: str, explicit: bool, keep_output: bool = False) -> list[tuple]:
    """Verify docker target. Returns list of (PASS|FAIL|SKIP, reason [, kept_path])."""
    results: list[tuple] = []

    if shutil.which("docker") is None:
        reason = "docker: docker not found on PATH"
        results.append(("FAIL" if explicit else "SKIP", reason))
        return results

    check = subprocess.run(["docker", "info"], capture_output=True, text=True)
    if check.returncode != 0:
        reason = "docker: daemon not running"
        results.append(("FAIL" if explicit else "SKIP", reason))
        return results

    tag = f"mqttprobe-verify-{version}-{os.getpid()}"
    try:
        build = subprocess.run(
            ["docker", "build", "--build-arg", f"VERSION={version}", "-t", tag, str(ROOT)],
            capture_output=True, text=True,
        )
        if build.returncode != 0:
            err = (build.stderr or build.stdout).strip().splitlines()
            for line in err[-5:]:
                print(f"    {line}")
            results.append(("FAIL", "docker: build failed"))
            return results

        create = subprocess.run(
            ["docker", "create", tag], capture_output=True, text=True,
        )
        if create.returncode != 0:
            results.append(("FAIL", "docker: could not create container"))
            return results

        container_id = create.stdout.strip()
        try:
            with tempfile.TemporaryDirectory(prefix="verify-docker-") as tmp:
                dll_path = Path(tmp) / "MqttProbe.Web.dll"
                cp = subprocess.run(
                    ["docker", "cp", f"{container_id}:/app/MqttProbe.Web.dll", str(dll_path)],
                    capture_output=True, text=True,
                )
                if cp.returncode != 0 or not dll_path.exists():
                    results.append(("FAIL", "docker: could not copy DLL from container"))
                    return results

                # Copy DLL to kept output tree if requested
                kept = None
                if keep_output:
                    keep_dir = ROOT / "publish" / "verify-app-version" / version / "docker"
                    if keep_dir.exists():
                        shutil.rmtree(keep_dir)
                    keep_dir.mkdir(parents=True)
                    shutil.copy2(dll_path, keep_dir / "MqttProbe.Web.dll")
                    kept = str(keep_dir.relative_to(ROOT))

                # Web AppInfoService: ProductVersion then InformationalVersion
                info = read_assembly_versions(dll_path)
                pv = info.get("ProductVersion", "")
                if versions_match(pv, version):
                    results.append(("PASS", f"docker: ProductVersion={pv}", kept))
                else:
                    results.append(("FAIL", f"docker: expected {version}, got ProductVersion={pv}"))
        finally:
            subprocess.run(["docker", "rm", container_id], capture_output=True)
    finally:
        subprocess.run(["docker", "rmi", tag], capture_output=True)

    return results


def verify_maui_windows(version: str, explicit: bool, keep_output: bool = False) -> list[tuple]:
    """Verify maui-windows target. Returns list of (PASS|FAIL|SKIP, reason [, kept_path])."""
    results: list[tuple] = []

    if sys.platform != "win32":
        reason = "maui-windows: not on Windows"
        results.append(("FAIL" if explicit else "SKIP", reason))
        return results

    wl = subprocess.run(["dotnet", "workload", "list"], capture_output=True, text=True)
    if "maui-windows" not in (wl.stdout or ""):
        reason = "maui-windows: maui-windows workload not installed"
        results.append(("FAIL" if explicit else "SKIP", reason))
        return results

    code = application_version_code(version)
    with _publish_dir(keep_output, version, "maui-windows") as out:
        extra = [
            "-f:net10.0-windows10.0.19041.0",
            "-p:MqttProbeMauiWindowsTargetFrameworksOverride=net10.0-windows10.0.19041.0",
            f"-p:ApplicationDisplayVersion={version}",
            f"-p:ApplicationVersion={code}",
        ]
        if not run_dotnet_publish("src/MqttProbe.Maui/MqttProbe.Maui.csproj", out, extra_args=extra):
            results.append(("FAIL", "maui-windows: dotnet publish failed"))
            return results

        exe = out / "MqttProbe.Maui.exe"
        if not exe.exists():
            candidates = list(out.glob("*.exe"))
            if candidates:
                exe = candidates[0]
            else:
                results.append(("FAIL", "maui-windows: no .exe found in output"))
                return results

        info = read_assembly_versions(exe)
        pv = info.get("ProductVersion", "")
        iv = info.get("InformationalVersion", "")
        kept = str(out.relative_to(ROOT)) if keep_output else None
        if versions_match(pv, version):
            results.append(("PASS", f"maui-windows: ProductVersion={pv}", kept))
        if versions_match(iv, version):
            results.append(("PASS", f"maui-windows: InformationalVersion={iv}", kept))

        # Search for stamped Package.appxmanifest in both publish output and obj/
        identity_found = False
        identity_matched = False
        manifests_to_check: list[tuple[str, Path]] = []
        for mp in out.rglob("Package.appxmanifest"):
            manifests_to_check.append(("publish", mp))
        obj_dir = ROOT / "src" / "MqttProbe.Maui" / "obj"
        if obj_dir.is_dir():
            for mp in obj_dir.rglob("Package.appxmanifest"):
                manifests_to_check.append(("obj", mp))

        # Prefer manifest whose Version matches expected (not 1.0.0.0)
        for _source, mp in manifests_to_check:
            manifest_text = mp.read_text(encoding="utf-8")
            m = re.search(r'Identity[^>]*Version="([^"]*)"', manifest_text)
            if m:
                identity_found = True
                manifest_ver = m.group(1)
                if versions_match(manifest_ver, version):
                    results.append(("PASS",
                        f"maui-windows: Identity Version={manifest_ver}"))
                    identity_matched = True
                    break  # Found the stamped one, done

        if identity_found and not identity_matched:
            # Collect actual versions for the error message
            actual_versions = []
            for _source, mp in manifests_to_check:
                txt = mp.read_text(encoding="utf-8")
                m = re.search(r'Identity[^>]*Version="([^"]*)"', txt)
                if m:
                    actual_versions.append(m.group(1))
            results.append(("FAIL",
                f"maui-windows: Identity Version mismatch, expected {version} "
                f"(4-part: {version}.0), got {actual_versions}"))
        elif not identity_found:
            results.append(("FAIL",
                "maui-windows: no Package.appxmanifest with Identity found "
                "(searched publish output and src/MqttProbe.Maui/obj/)"))

    return results


def _find_aapt() -> str | None:
    """Find aapt binary, checking PATH and common Android SDK locations."""
    if shutil.which("aapt"):
        return "aapt"
    if shutil.which("aapt2"):
        return "aapt2"

    search_roots: list[Path] = []
    android_home = os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT")
    if android_home:
        search_roots.append(Path(android_home))
    if sys.platform == "win32":
        local_appdata = os.environ.get("LOCALAPPDATA", "")
        if local_appdata:
            search_roots.append(Path(local_appdata) / "Android" / "Sdk")
    else:
        search_roots.append(Path.home() / "Android" / "Sdk")

    for root in search_roots:
        build_tools = root / "build-tools"
        if not build_tools.is_dir():
            continue
        for version_dir in sorted(build_tools.iterdir(), reverse=True):
            for name in ("aapt2", "aapt"):
                ext = ".exe" if sys.platform == "win32" else ""
                candidate = version_dir / (name + ext)
                if candidate.is_file():
                    return str(candidate)
    return None


def verify_android(version: str, explicit: bool, keep_output: bool = False) -> list[tuple]:
    """Verify android target. Returns list of (PASS|FAIL|SKIP, reason [, kept_path])."""
    results: list[tuple] = []

    # VS installs platform workload id "android"; CLI umbrella is "maui-android".
    wl = subprocess.run(["dotnet", "workload", "list"], capture_output=True, text=True)
    wl_out = wl.stdout or ""
    if not re.search(r"(?m)^\s*(android|maui-android|maui)\s", wl_out):
        reason = "android: android/maui-android workload not installed"
        results.append(("FAIL" if explicit else "SKIP", reason))
        return results

    code = application_version_code(version)
    with _publish_dir(keep_output, version, "android") as out:
        extra = [
            "-f:net10.0-android",
            "-p:AndroidPackageFormat=apk",
            f"-p:ApplicationDisplayVersion={version}",
            f"-p:ApplicationVersion={code}",
        ]
        if not run_dotnet_publish("src/MqttProbe.Maui/MqttProbe.Maui.csproj", out, extra_args=extra):
            results.append(("FAIL", "android: dotnet publish failed"))
            return results

        apks = list(out.glob("*.apk"))
        if not apks:
            results.append(("FAIL", "android: no .apk found in output"))
            return results

        apk_path = apks[0]
        aapt_path = _find_aapt()
        if aapt_path:
            aapt = subprocess.run(
                [aapt_path, "dump", "badging", str(apk_path)],
                capture_output=True, text=True,
            )
            for line in aapt.stdout.splitlines():
                if line.startswith("package:"):
                    m = re.search(r"versionName='([^']*)'", line)
                    if m:
                        actual = m.group(1)
                        if versions_match(actual, version):
                            kept = str(out.relative_to(ROOT)) if keep_output else None
                            results.append(("PASS", f"android: versionName={actual}", kept))
                        else:
                            results.append(("FAIL",
                                f"android: expected {version}, got versionName={actual}"))
                    break
            else:
                results.append(("FAIL", "android: could not parse versionName from aapt output"))
        else:
            reason = "android: aapt/aapt2 not found (checked PATH, ANDROID_HOME, common SDK paths)"
            results.append(("FAIL" if explicit else "SKIP", reason))

    return results


# ── CLI ───────────────────────────────────────────────────────────────────────

def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Verify app version is correctly embedded in build artifacts.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--version", required=True,
        help="Expected version (X.Y.Z, no leading v)",
    )
    parser.add_argument(
        "--target", action="append", dest="targets", default=[],
        choices=ALL_TARGETS + ["all"],
        help="Target(s) to verify (default: web desktop). Repeatable.",
    )
    parser.add_argument(
        "--also-negative", action="store_true",
        help="For web/desktop: also publish without -p:Version and assert placeholder 0.0.0",
    )
    parser.add_argument(
        "--keep-output", action="store_true",
        help="Keep publish outputs under publish/verify-app-version/{version}/{target}/ "
             "instead of using auto-deleted temp dirs",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    version = args.version.strip()
    if version.startswith("v"):
        print("ERROR: --version must not start with 'v'")
        return 1
    if not version:
        print("ERROR: --version must not be empty")
        return 1
    if version == "0.0.0":
        print("ERROR: --version must not be 0.0.0 (placeholder)")
        return 1

    if "all" in args.targets:
        targets = list(ALL_TARGETS)
    elif args.targets:
        targets = list(dict.fromkeys(args.targets))
    else:
        targets = list(DEFAULT_TARGETS)

    print(f"\n=== Verify App Version: {version} ===\n")

    failed = 0
    for target in targets:
        print(f"  {target}...", end="", flush=True)
        explicit = target in (args.targets or [])

        if target == "web":
            results = verify_web(version, args.also_negative, args.keep_output)
        elif target == "desktop":
            results = verify_desktop(version, args.also_negative, args.keep_output)
        elif target == "docker":
            results = verify_docker(version, explicit, args.keep_output)
        elif target == "maui-windows":
            results = verify_maui_windows(version, explicit, args.keep_output)
        elif target == "android":
            results = verify_android(version, explicit, args.keep_output)
        else:
            results = [("SKIP", f"unknown target: {target}")]

        if results:
            status, reason = results[0][0], results[0][1]
            kept = results[0][2] if len(results[0]) > 2 else None
            suffix = f"; kept: {kept}" if kept else ""
            print(f" {status} ({reason}{suffix})")
            if status == "FAIL":
                failed += 1
            for r in results[1:]:
                status, reason = r[0], r[1]
                kept = r[2] if len(r) > 2 else None
                suffix = f"; kept: {kept}" if kept else ""
                print(f"         {status} ({reason}{suffix})")
                if status == "FAIL":
                    failed += 1
        else:
            print(" SKIP (no result)")

    print()
    if failed > 0:
        print(f"=== {failed} check(s) FAILED ===")
        return 1
    else:
        print("=== All checks passed ===")
        return 0


if __name__ == "__main__":
    sys.exit(main())
