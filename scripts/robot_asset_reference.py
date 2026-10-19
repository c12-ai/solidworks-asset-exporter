#!/usr/bin/env python3
"""Public, dependency-free Robot UUID and NAS directory resolver.

Protocol: docs/ROBOT_ASSET_REFERENCE_PROTOCOL.md.
All commands are read-only; this module never uploads or renames NAS resources.
"""

import argparse
import json
from pathlib import Path
import re
import unicodedata
import uuid


ROBOT_NAMESPACE = uuid.UUID("6ba7b811-9dad-11d1-80b4-00c04fd430c8")
ROBOT_PREFIX = "urn:solidworks-robot:v1:"
NAME_WHITESPACE = (
    "\u0009\u000a\u000b\u000c\u000d\u0020\u0085\u00a0\u1680"
    "\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a"
    "\u2028\u2029\u202f\u205f\u3000"
)
ASCII_LOWER = str.maketrans("ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz")
ASSET_ID_PATTERN = re.compile(
    r"([0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}):([1-9][0-9]*)"
)
MAX_VERSION = 2147483647


def normalize_robot_name(name):
    """Trim the fixed White_Space set, NFC-normalize, then lowercase ASCII only."""
    if not isinstance(name, str):
        raise ValueError("Robot name must be a string")
    normalized = unicodedata.normalize("NFC", name.strip(NAME_WHITESPACE))
    if normalized in ("", ".", ".."):
        raise ValueError("Robot name must be a nonempty direct directory name")
    if any(char in "/\\" or unicodedata.category(char) in ("Cc", "Cs") for char in normalized):
        raise ValueError("Robot name contains a path separator, control character or invalid Unicode")
    return normalized.translate(ASCII_LOWER)


def robot_uuid(name):
    """Return the stable model UUID; neither version nor NAS path participates."""
    return uuid.uuid5(ROBOT_NAMESPACE, ROBOT_PREFIX + normalize_robot_name(name))


def robot_asset_id(name, version):
    if type(version) is not int or not 1 <= version <= MAX_VERSION:
        raise ValueError("Robot version must be an integer in 1..2147483647")
    return "{}:{}".format(robot_uuid(name), version)


def parse_robot_asset_id(asset_id):
    """Accept only the canonical UUIDv5:positive-decimal-version wire format."""
    if not isinstance(asset_id, str):
        raise ValueError("Robot asset_id must be a string")
    match = ASSET_ID_PATTERN.fullmatch(asset_id)
    if match is None or len(match.group(2)) > 10:
        raise ValueError("Robot asset_id must be lowercase UUIDv5:version (no legacy name IDs)")
    version = int(match.group(2))
    if version > MAX_VERSION:
        raise ValueError("Robot version exceeds 2147483647")
    return match.group(1), version


def build_robot_index(robots_root):
    """Map UUID strings to original directories, retaining case and Unicode spelling.

    Only direct child directories are model names. Files are ignored. Ambiguous
    normalized names fail the whole index instead of silently choosing a model.
    """
    root = Path(robots_root).resolve()
    if not root.is_dir():
        raise FileNotFoundError("Robot root directory does not exist: {}".format(root))
    index = {}
    for directory in sorted(root.iterdir(), key=lambda item: item.name):
        if not directory.is_dir():
            continue
        key = str(robot_uuid(directory.name))
        if key in index:
            raise ValueError("Ambiguous Robot UUID {}: {!r} and {!r}".format(
                key, index[key].name, directory.name))
        index[key] = directory
    return index


def resolve_robot_asset(robots_root, asset_id):
    """Resolve to robots/<original name>/vN/urdf, not to a guessed URDF file.

    The existing URDF loader owns entry-file selection and mesh URI handling.
    The reference implementation rescans names for each resolution; applications
    may cache build_robot_index with the refresh rules described in the protocol.
    """
    key, version = parse_robot_asset_id(asset_id)
    index = build_robot_index(robots_root)
    if key not in index:
        raise ValueError("Unknown Robot UUID in directory index: {}".format(key))
    urdf_directory = index[key] / ("v" + str(version)) / "urdf"
    if not urdf_directory.is_dir():
        raise FileNotFoundError("Robot version/URDF directory does not exist: {}".format(urdf_directory))
    return urdf_directory


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    generate = commands.add_parser("uuid", help="Compute a Robot model UUID from its name")
    generate.add_argument("name")
    index = commands.add_parser("index", help="Read direct model directories and emit a JSON index")
    index.add_argument("robots_root", type=Path)
    resolve = commands.add_parser("resolve", help="Resolve UUID:version to the existing URDF directory")
    resolve.add_argument("robots_root", type=Path)
    resolve.add_argument("asset_id")
    args = parser.parse_args(argv)
    try:
        if args.command == "uuid":
            print(robot_uuid(args.name))
        elif args.command == "index":
            entries = build_robot_index(args.robots_root)
            print(json.dumps({key: {"name": path.name, "directory": str(path)}
                              for key, path in entries.items()}, ensure_ascii=False, indent=2))
        else:
            print(resolve_robot_asset(args.robots_root, args.asset_id))
    except (ValueError, OSError) as error:
        parser.error(str(error))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
