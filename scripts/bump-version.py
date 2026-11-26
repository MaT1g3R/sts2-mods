#!/usr/bin/env python3
"""Bump Semantic Versioning (SemVer 2.0.0) versions for one or more STS2 mods.

Updates:
  - Mod JSON manifest (<mod-dir>/<ModId>.json) "version" field
  - Mod .csproj <Version> property (including non-test library projects such as *.Core.csproj)
  - Optional Steam Workshop manifest (<mod-dir>/steam/workshop.json) "changeNote"

Examples:
  scripts/bump-version.py --list
  scripts/bump-version.py patch run-history-plus run-resumer
  scripts/bump-version.py minor --all --dry-run
  scripts/bump-version.py patch --all --change-note "Rebuild for STS2 v0.115.0"
  scripts/bump-version.py 2.0.0-rc.1 run-resumer
  scripts/bump-version.py prerelease --preid beta sts2-calculator
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

REPO_ROOT = Path(__file__).resolve().parent.parent

# Strict SemVer 2.0.0 grammar (https://semver.org/#is-there-a-suggested-regular-expression-zig-zag)
SEMVER_RE = re.compile(
    r"^(?P<major>0|[1-9]\d*)"
    r"\.(?P<minor>0|[1-9]\d*)"
    r"\.(?P<patch>0|[1-9]\d*)"
    r"(?:-(?P<prerelease>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)"
    r"(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?"
    r"(?:\+(?P<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$"
)

PREID_RE = re.compile(r"^[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*$")

BUMP_KINDS = {
    "major",
    "minor",
    "patch",
    "premajor",
    "preminor",
    "prepatch",
    "prerelease",
}

SKIP_DIRS = {"bin", "obj", "steam", ".git", "dist"}


@dataclass(frozen=True)
class SemVer:
    major: int
    minor: int
    patch: int
    prerelease: tuple[str, ...] = ()
    build: tuple[str, ...] = ()

    @classmethod
    def parse(cls, raw: str) -> SemVer:
        match = SEMVER_RE.match(raw)
        if not match:
            raise ValueError(f"Invalid SemVer 2.0.0 version: {raw!r}")
        pre = tuple(match.group("prerelease").split(".")) if match.group("prerelease") else ()
        bld = tuple(match.group("build").split(".")) if match.group("build") else ()
        return cls(
            major=int(match.group("major")),
            minor=int(match.group("minor")),
            patch=int(match.group("patch")),
            prerelease=pre,
            build=bld,
        )

    def __str__(self) -> str:
        base = f"{self.major}.{self.minor}.{self.patch}"
        if self.prerelease:
            base += "-" + ".".join(self.prerelease)
        if self.build:
            base += "+" + ".".join(self.build)
        return base

    def core_tuple(self) -> tuple[int, int, int]:
        return (self.major, self.minor, self.patch)

    def compare(self, other: SemVer) -> int:
        """SemVer 2.0.0 precedence comparison (ignores build metadata)."""
        if self.core_tuple() != other.core_tuple():
            return -1 if self.core_tuple() < other.core_tuple() else 1

        if not self.prerelease and not other.prerelease:
            return 0
        if not self.prerelease:
            return 1
        if not other.prerelease:
            return -1

        for left, right in zip(self.prerelease, other.prerelease):
            left_num = left.isdigit()
            right_num = right.isdigit()
            if left_num and right_num:
                li, ri = int(left), int(right)
                if li != ri:
                    return -1 if li < ri else 1
            elif left_num != right_num:
                # Numeric identifiers always have lower precedence than non-numeric.
                return -1 if left_num else 1
            elif left != right:
                return -1 if left < right else 1

        if len(self.prerelease) != len(other.prerelease):
            return -1 if len(self.prerelease) < len(other.prerelease) else 1
        return 0

    def bump(self, kind: str, preid: str | None = None) -> SemVer:
        if kind == "major":
            if self.minor == 0 and self.patch == 0 and self.prerelease:
                return SemVer(self.major, 0, 0)
            return SemVer(self.major + 1, 0, 0)

        if kind == "minor":
            if self.patch == 0 and self.prerelease:
                return SemVer(self.major, self.minor, 0)
            return SemVer(self.major, self.minor + 1, 0)

        if kind == "patch":
            if self.prerelease:
                return SemVer(self.major, self.minor, self.patch)
            return SemVer(self.major, self.minor, self.patch + 1)

        effective_preid = preid if preid is not None else "alpha"
        if not PREID_RE.match(effective_preid):
            raise ValueError(f"Invalid prerelease identifier: {effective_preid!r}")
        pre_parts = tuple(effective_preid.split("."))

        if kind == "premajor":
            return SemVer(self.major + 1, 0, 0, prerelease=pre_parts + ("0",))

        if kind == "preminor":
            return SemVer(self.major, self.minor + 1, 0, prerelease=pre_parts + ("0",))

        if kind == "prepatch":
            return SemVer(self.major, self.minor, self.patch + 1, prerelease=pre_parts + ("0",))

        if kind == "prerelease":
            if not self.prerelease:
                return SemVer(self.major, self.minor, self.patch + 1, prerelease=pre_parts + ("0",))

            if preid is not None and self.prerelease[: len(pre_parts)] != pre_parts:
                return SemVer(self.major, self.minor, self.patch, prerelease=pre_parts + ("0",))

            parts = list(self.prerelease)
            for idx in range(len(parts) - 1, -1, -1):
                if parts[idx].isdigit():
                    parts[idx] = str(int(parts[idx]) + 1)
                    return SemVer(self.major, self.minor, self.patch, prerelease=tuple(parts))

            parts.append("0")
            return SemVer(self.major, self.minor, self.patch, prerelease=tuple(parts))

        raise ValueError(f"Unknown bump kind: {kind!r}")


@dataclass
class ModInfo:
    name: str
    path: Path
    mod_id: str
    manifest_path: Path
    version: SemVer
    csproj_paths: list[Path]
    workshop_path: Path | None


def is_test_csproj(csproj_path: Path, mod_dir: Path) -> bool:
    rel_parts = csproj_path.relative_to(mod_dir).parts
    if any(part.lower() in {"tests", "test"} for part in rel_parts[:-1]):
        return True
    text = csproj_path.read_text(encoding="utf-8")
    if re.search(r"<IsTestProject>\s*true\s*</IsTestProject>", text):
        return True
    if "Microsoft.NET.Test.Sdk" in text:
        return True
    if re.search(r"<OutputType>\s*Exe\s*</OutputType>", text) and "test" in csproj_path.stem.lower():
        return True
    return False


def find_manifest(mod_dir: Path) -> Path | None:
    for candidate in sorted(mod_dir.glob("*.json")):
        try:
            data = json.loads(candidate.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        if isinstance(data, dict) and "id" in data and "version" in data:
            return candidate
    return None


def find_mod_csprojs(mod_dir: Path) -> list[Path]:
    found: list[Path] = []
    for path in sorted(mod_dir.rglob("*.csproj")):
        rel_parts = path.relative_to(mod_dir).parts
        if any(part in SKIP_DIRS for part in rel_parts[:-1]):
            continue
        if is_test_csproj(path, mod_dir):
            continue
        found.append(path)
    return found


def discover_mods(repo_root: Path) -> list[ModInfo]:
    mods: list[ModInfo] = []
    for entry in sorted(repo_root.iterdir()):
        if not entry.is_dir() or entry.name.startswith("."):
            continue
        top_csprojs = list(entry.glob("*.csproj"))
        if not top_csprojs:
            continue
        manifest = find_manifest(entry)
        if manifest is None:
            continue
        data = json.loads(manifest.read_text(encoding="utf-8"))
        version = SemVer.parse(str(data["version"]))
        workshop = entry / "steam" / "workshop.json"
        mods.append(
            ModInfo(
                name=entry.name,
                path=entry,
                mod_id=str(data["id"]),
                manifest_path=manifest,
                version=version,
                csproj_paths=find_mod_csprojs(entry),
                workshop_path=workshop if workshop.is_file() else None,
            )
        )
    return mods


def resolve_targets(all_mods: list[ModInfo], requested: Iterable[str], use_all: bool) -> list[ModInfo]:
    if use_all:
        return list(all_mods)

    by_dir = {m.name: m for m in all_mods}
    by_id = {m.mod_id.lower(): m for m in all_mods}

    resolved: list[ModInfo] = []
    seen: set[str] = set()
    for raw in requested:
        norm = re.sub(r"^\./", "", raw.strip()).rstrip("/")
        if norm == "all":
            for mod in all_mods:
                if mod.name not in seen:
                    resolved.append(mod)
                    seen.add(mod.name)
            continue

        mod = by_dir.get(norm) or by_id.get(norm.lower())
        if mod is None:
            known = ", ".join(m.name for m in all_mods)
            raise SystemExit(f"Error: Unknown mod '{raw}'. Detected mods: {known}")
        if mod.name not in seen:
            resolved.append(mod)
            seen.add(mod.name)
    return resolved


def update_manifest(manifest_path: Path, new_version: SemVer, dry_run: bool) -> bool:
    original = manifest_path.read_text(encoding="utf-8")
    pattern = re.compile(r'("version"\s*:\s*")([^"]*)(")')
    match = pattern.search(original)
    if not match:
        raise ValueError(f'Could not find "version" field in {manifest_path}')

    updated = pattern.sub(rf"\g<1>{new_version}\g<3>", original, count=1)
    # Validate resulting JSON before writing.
    parsed = json.loads(updated)
    if parsed.get("version") != str(new_version):
        raise ValueError(f"Manifest version verification failed for {manifest_path}")

    if updated == original:
        return False
    if not dry_run:
        manifest_path.write_text(updated, encoding="utf-8")
    return True


def read_csproj_version(csproj_path: Path) -> str | None:
    text = csproj_path.read_text(encoding="utf-8")
    match = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", text)
    return match.group(1) if match else None


def update_csproj(csproj_path: Path, new_version: SemVer, dry_run: bool) -> bool:
    original = csproj_path.read_text(encoding="utf-8")
    updated = original

    version_tag_re = re.compile(r"(<Version>)([^<]*)(</Version>)")
    if version_tag_re.search(updated):
        updated = version_tag_re.sub(rf"\g<1>{new_version}\g<3>", updated, count=1)
    else:
        # Insert <Version> inside the first <PropertyGroup>, matching indentation.
        anchor_re = re.compile(
            r"(?m)^(?P<indent>[ \t]*)<(?:RootNamespace|AssemblyName|TargetFramework)>[^<]*</(?:RootNamespace|AssemblyName|TargetFramework)>[ \t]*\r?\n"
        )
        matches = list(anchor_re.finditer(updated))
        if matches:
            anchor = matches[-1]
            # Stay within the first <PropertyGroup>.
            first_pg_end = updated.find("</PropertyGroup>")
            for candidate in matches:
                if first_pg_end == -1 or candidate.end() <= first_pg_end:
                    anchor = candidate
            indent = anchor.group("indent")
            newline = "\r\n" if "\r\n" in updated else "\n"
            insertion = f"{indent}<Version>{new_version}</Version>{newline}"
            updated = updated[: anchor.end()] + insertion + updated[anchor.end() :]
        else:
            pg_re = re.compile(r"(?m)^(?P<indent>[ \t]*)<PropertyGroup>\r?\n")
            pg_match = pg_re.search(updated)
            if not pg_match:
                raise ValueError(f"No <PropertyGroup> found in {csproj_path}")
            indent = pg_match.group("indent") + "  "
            newline = "\r\n" if "\r\n" in updated else "\n"
            insertion = f"{indent}<Version>{new_version}</Version>{newline}"
            updated = updated[: pg_match.end()] + insertion + updated[pg_match.end() :]

    # Keep optional explicit assembly/file/prefix version tags in sync if present.
    four_part = f"{new_version.major}.{new_version.minor}.{new_version.patch}.0"
    three_part = f"{new_version.major}.{new_version.minor}.{new_version.patch}"
    updated = re.sub(r"(<AssemblyVersion>)[^<]*(</AssemblyVersion>)", rf"\g<1>{four_part}\g<2>", updated)
    updated = re.sub(r"(<FileVersion>)[^<]*(</FileVersion>)", rf"\g<1>{four_part}\g<2>", updated)
    updated = re.sub(r"(<VersionPrefix>)[^<]*(</VersionPrefix>)", rf"\g<1>{three_part}\g<2>", updated)

    if updated == original:
        return False
    if not dry_run:
        csproj_path.write_text(updated, encoding="utf-8")
    return True


def update_workshop(
    workshop_path: Path,
    new_version: SemVer,
    change_note: str | None,
    dry_run: bool,
) -> tuple[bool, str | None]:
    original = workshop_path.read_text(encoding="utf-8")
    data = json.loads(original)
    if "changeNote" not in data or not isinstance(data["changeNote"], str):
        return False, None

    old_note = data["changeNote"]
    new_prefix = f"v{new_version}"

    if change_note is not None:
        cleaned = change_note.strip()
        if cleaned.startswith(new_prefix):
            new_note = cleaned
        else:
            new_note = f"{new_prefix} - {cleaned}"
    else:
        prefix_re = re.compile(
            r"^v(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)"
            r"(?:-[0-9a-zA-Z.-]+)?(?:\+[0-9a-zA-Z.-]+)?"
        )
        if not prefix_re.match(old_note):
            return False, None
        new_note = prefix_re.sub(new_prefix, old_note, count=1)

    if new_note == old_note:
        return False, None

    encoded_note = json.dumps(new_note, ensure_ascii=False)
    pattern = re.compile(r'("changeNote"\s*:\s*)"(?:\\.|[^"\\])*"')
    if not pattern.search(original):
        raise ValueError(f'Could not locate "changeNote" in {workshop_path}')

    updated = pattern.sub(lambda m: f"{m.group(1)}{encoded_note}", original, count=1)
    # Validate resulting JSON.
    parsed = json.loads(updated)
    if parsed.get("changeNote") != new_note:
        raise ValueError(f"Workshop changeNote verification failed for {workshop_path}")

    if not dry_run:
        workshop_path.write_text(updated, encoding="utf-8")
    return True, new_note


def compute_next_version(current: SemVer, spec: str, preid: str | None, force: bool) -> SemVer:
    if spec in BUMP_KINDS:
        return current.bump(spec, preid=preid)

    raw = spec[1:] if spec.startswith(("v", "V")) else spec
    explicit = SemVer.parse(raw)
    if not force and explicit.compare(current) <= 0:
        raise ValueError(
            f"Target version {explicit} is not greater than current version {current} "
            "(pass --force to allow)"
        )
    return explicit


def print_mod_list(mods: list[ModInfo]) -> None:
    if not mods:
        print("No mods detected.")
        return
    name_w = max(len(m.name) for m in mods)
    id_w = max(len(m.mod_id) for m in mods)
    for mod in mods:
        csproj_versions = [read_csproj_version(p) or "<unset>" for p in mod.csproj_paths]
        csproj_summary = ", ".join(csproj_versions)
        mismatch = any(v != str(mod.version) for v in csproj_versions)
        flag = " [csproj mismatch]" if mismatch else ""
        print(
            f"  {mod.name:<{name_w}}  ({mod.mod_id:<{id_w}})  "
            f"manifest={mod.version}  csproj={csproj_summary}{flag}"
        )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Bump SemVer 2.0.0 versions across STS2 mod manifests, .csproj files, and Steam Workshop metadata.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Bump arguments:\n"
            "  major | minor | patch | premajor | preminor | prepatch | prerelease\n"
            "  <X.Y.Z[-pre][+build]>   Explicit SemVer 2.0.0 version (optional leading 'v')\n\n"
            "Examples:\n"
            "  scripts/bump-version.py --list\n"
            "  scripts/bump-version.py patch run-history-plus run-resumer\n"
            "  scripts/bump-version.py minor --all --change-note 'Support STS2 v0.115.0'\n"
            "  scripts/bump-version.py 1.2.0-rc.1 run-resumer --dry-run\n"
        ),
    )
    parser.add_argument(
        "bump",
        nargs="?",
        help="Bump kind (major, minor, patch, premajor, preminor, prepatch, prerelease) or explicit SemVer version",
    )
    parser.add_argument(
        "mods",
        nargs="*",
        help="Mod directory names or manifest IDs to bump (or 'all')",
    )
    parser.add_argument(
        "-a",
        "--all",
        action="store_true",
        dest="all_mods",
        help="Bump all detected mods",
    )
    parser.add_argument(
        "-l",
        "--list",
        action="store_true",
        help="List all detected mods and their current versions",
    )
    parser.add_argument(
        "--preid",
        default=None,
        help="Prerelease identifier for pre* bumps (default: alpha)",
    )
    parser.add_argument(
        "--change-note",
        default=None,
        help="Set steam/workshop.json changeNote to 'v<version> - <note>'",
    )
    parser.add_argument(
        "--no-workshop",
        action="store_true",
        help="Do not modify steam/workshop.json",
    )
    parser.add_argument(
        "-n",
        "--dry-run",
        action="store_true",
        help="Show planned changes without modifying any files",
    )
    parser.add_argument(
        "-f",
        "--force",
        action="store_true",
        help="Allow explicit versions that are equal to or lower than the current version",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)

    all_mods = discover_mods(REPO_ROOT)

    if args.list:
        print_mod_list(all_mods)
        return 0

    if not args.bump:
        parser.print_usage(sys.stderr)
        print("Error: A bump kind or explicit SemVer version is required (or use --list).", file=sys.stderr)
        return 2

    if not args.all_mods and not args.mods:
        parser.print_usage(sys.stderr)
        print("Error: Specify one or more mods, or pass --all.", file=sys.stderr)
        return 2

    targets = resolve_targets(all_mods, args.mods, args.all_mods)
    if not targets:
        print("Error: No matching mods found.", file=sys.stderr)
        return 1

    # Validate all target bumps before writing anything so multi-mod runs are atomic on validation errors.
    plan: list[tuple[ModInfo, SemVer]] = []
    for mod in targets:
        try:
            next_ver = compute_next_version(mod.version, args.bump, args.preid, args.force)
        except ValueError as exc:
            print(f"Error ({mod.name}): {exc}", file=sys.stderr)
            return 1
        plan.append((mod, next_ver))

    prefix = "[dry-run] " if args.dry_run else ""
    for mod, next_ver in plan:
        print(f"{prefix}{mod.name} ({mod.mod_id}): {mod.version} -> {next_ver}")

        if update_manifest(mod.manifest_path, next_ver, args.dry_run):
            print(f"  updated {mod.manifest_path.relative_to(REPO_ROOT)}")

        for csproj in mod.csproj_paths:
            if update_csproj(csproj, next_ver, args.dry_run):
                print(f"  updated {csproj.relative_to(REPO_ROOT)}")

        if not args.no_workshop and mod.workshop_path is not None:
            changed, new_note = update_workshop(mod.workshop_path, next_ver, args.change_note, args.dry_run)
            if changed:
                print(f"  updated {mod.workshop_path.relative_to(REPO_ROOT)} (changeNote: {new_note})")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
