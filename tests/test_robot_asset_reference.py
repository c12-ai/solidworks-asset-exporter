"""Exercise the public Robot identity contract and its read-only NAS resolver.

A UUID is not reverse-hashed into a model name. Resolution indexes existing
directory names by their UUID and returns the original matching path.
"""

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "robot_asset_reference.py"
SPEC = importlib.util.spec_from_file_location("robot_asset_reference", SCRIPT)
reference = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(reference)

TALOS_UUID = "be210481-a11b-5fb2-8002-bf9bd0b9afdb"
HEBE_UUID = "4ce608df-8e9e-5a8b-b084-894b8da37409"


class RobotIdentityTests(unittest.TestCase):
    def test_published_uuid_vectors(self):
        vectors = (
            ("Talos", TALOS_UUID),
            ("Hebe", HEBE_UUID),
            ("e\u0301", "ab40490a-e945-5e15-a365-38704430a7d8"),
            ("\u00c9", "834a7fbc-3c74-595a-aa63-bc1d5aac6d57"),
            ("机器人", "45d5373c-3bc3-5651-92b2-9c4eddfb990c"),
            ("机器人🤖", "071cc8ff-46f2-5ce7-b399-6a8a7a78ec15"),
        )
        for name, expected in vectors:
            with self.subTest(name=name):
                self.assertEqual(expected, str(reference.robot_uuid(name)))

    def test_fixed_whitespace_is_trimmed(self):
        whitespace = (
            "\t\n\v\f\r \u0085\u00a0\u1680"
            "\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a"
            "\u2028\u2029\u202f\u205f\u3000"
        )
        for character in whitespace:
            with self.subTest(codepoint=hex(ord(character))):
                name = character + "TaLoS" + character
                self.assertEqual("talos", reference.normalize_robot_name(name))
                self.assertEqual(TALOS_UUID, str(reference.robot_uuid(name)))

    def test_normalization_preserves_non_ascii_case_and_internal_spaces(self):
        self.assertEqual("\u00e9robot", reference.normalize_robot_name("e\u0301Robot"))
        self.assertEqual("\u00c9robot", reference.normalize_robot_name("\u00c9Robot"))
        self.assertNotEqual(reference.robot_uuid("\u00c9"), reference.robot_uuid("\u00e9"))
        self.assertEqual("robot  arm", reference.normalize_robot_name(" Robot  ARM "))
        self.assertEqual("\u200btalos\u200b", reference.normalize_robot_name("\u200bTalos\u200b"))

    def test_invalid_names_are_rejected(self):
        invalid_names = (
            None, 1, b"Talos", "", " \u00a0 ", ".", " .. ", "Talos/Other",
            "Talos\\Other", "Ta\0los", "Ta\u001flos", "\u001cTalos", "Ta\tlos",
            "Ta\u007flos", "Ta\ud800los",
        )
        for name in invalid_names:
            with self.subTest(name=repr(name)):
                with self.assertRaises(ValueError):
                    reference.robot_uuid(name)

    def test_uuid_stays_stable_across_versions(self):
        self.assertEqual(TALOS_UUID + ":1", reference.robot_asset_id("Talos", 1))
        self.assertEqual(TALOS_UUID + ":2", reference.robot_asset_id(" TALOS ", 2))
        self.assertEqual(TALOS_UUID + ":2147483647", reference.robot_asset_id("Talos", 2147483647))

    def test_invalid_versions_are_rejected(self):
        for version in (None, True, False, "1", 1.0, 0, -1, 2147483648):
            with self.subTest(version=repr(version)):
                with self.assertRaises(ValueError):
                    reference.robot_asset_id("Talos", version)

    def test_parse_valid_asset_ids(self):
        for version in (1, 2, 2147483647):
            with self.subTest(version=version):
                self.assertEqual(
                    (TALOS_UUID, version),
                    reference.parse_robot_asset_id(TALOS_UUID + ":" + str(version)),
                )

    def test_parse_rejects_legacy_and_noncanonical_ids(self):
        invalid_ids = (
            None, 1, "", "Talos:1", TALOS_UUID, TALOS_UUID.upper() + ":1",
            "{" + TALOS_UUID + "}:1", TALOS_UUID.replace("-", "") + ":1",
            "be210481-a11b-4fb2-8002-bf9bd0b9afdb:1",
            "be210481-a11b-5fb2-7002-bf9bd0b9afdb:1",
        )
        invalid_ids += tuple(TALOS_UUID + suffix for suffix in (
            ":0", ":-1", ":+1", ":01", ":1.0", ":v1", ": 1", ":1 ", ":1\n",
            ":2147483648", ":99999999999", ":", ":1:2", ":\u0661",
        ))
        for asset_id in invalid_ids:
            with self.subTest(asset_id=repr(asset_id)):
                with self.assertRaises(ValueError):
                    reference.parse_robot_asset_id(asset_id)


class RobotDirectoryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="robot-reference-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "robots"
        self.talos = self.root / "Talos"
        (self.talos / "meshes" / "simplified").mkdir(parents=True)
        (self.talos / "meshes" / "simplified" / "arm.stl").write_bytes(b"fixture mesh")
        for version in (1, 2):
            directory = self.talos / ("v" + str(version)) / "urdf"
            directory.mkdir(parents=True)
            (directory / "Talos.urdf").write_text("<robot name='Talos' />", encoding="utf-8")
        (self.root / "README.txt").write_text("Fixture index note", encoding="utf-8")

    def snapshot(self):
        result = {}
        for path in [self.root] + sorted(self.root.rglob("*")):
            result[str(path.relative_to(self.root))] = (
                "directory" if path.is_dir() else path.read_bytes(),
                path.stat().st_mtime_ns,
            )
        return result

    def cli(self, *arguments):
        return subprocess.run(
            [sys.executable, "-B", str(SCRIPT), *map(str, arguments)],
            cwd=self.temporary.name,
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )

    def test_index_uses_only_direct_model_directories(self):
        self.assertEqual({TALOS_UUID: self.talos}, reference.build_robot_index(self.root))

    def test_resolve_versions_returns_original_urdf_directory(self):
        for version in (1, 2):
            with self.subTest(version=version):
                resolved = reference.resolve_robot_asset(self.root, TALOS_UUID + ":" + str(version))
                self.assertEqual(self.talos / ("v" + str(version)) / "urdf", resolved)
                self.assertTrue(resolved.is_dir())
                self.assertEqual("Talos", resolved.parent.parent.name)

    def test_unicode_directory_spelling_is_preserved(self):
        original = self.root / "e\u0301Robot"
        urdf = original / "v1" / "urdf"
        urdf.mkdir(parents=True)
        asset_id = reference.robot_asset_id("\u00e9ROBOT", 1)
        self.assertEqual(urdf, reference.resolve_robot_asset(self.root, asset_id))
        self.assertEqual(original, reference.build_robot_index(self.root)[asset_id.split(":")[0]])

    def test_duplicate_ascii_case_names_are_rejected(self):
        lower = self.root / "talos"
        if lower.exists():
            self.skipTest("Filesystem does not distinguish Talos and talos")
        lower.mkdir()
        with self.assertRaisesRegex(ValueError, "Ambiguous Robot UUID"):
            reference.build_robot_index(self.root)
        with self.assertRaisesRegex(ValueError, "Ambiguous Robot UUID"):
            reference.resolve_robot_asset(self.root, TALOS_UUID + ":1")

    def test_unknown_uuid_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Unknown Robot UUID"):
            reference.resolve_robot_asset(self.root, HEBE_UUID + ":1")

    def test_resolution_uses_index_instead_of_reversing_uuid(self):
        renamed = self.root / "OtherRobot"
        self.talos.rename(renamed)
        with self.assertRaisesRegex(ValueError, "Unknown Robot UUID"):
            reference.resolve_robot_asset(self.root, TALOS_UUID + ":1")
        self.assertEqual(
            renamed / "v1" / "urdf",
            reference.resolve_robot_asset(self.root, reference.robot_asset_id("OtherRobot", 1)),
        )

    def test_missing_root_or_file_root_is_rejected(self):
        for root in (self.root / "missing", self.root / "README.txt"):
            with self.subTest(root=root):
                with self.assertRaises(FileNotFoundError):
                    reference.build_robot_index(root)
                with self.assertRaises(FileNotFoundError):
                    reference.resolve_robot_asset(root, TALOS_UUID + ":1")

    def test_missing_version_or_urdf_is_rejected(self):
        (self.talos / "v3").mkdir()
        (self.talos / "v4").mkdir()
        (self.talos / "v4" / "urdf").write_text("not a directory", encoding="utf-8")
        for version in (3, 4, 99):
            with self.subTest(version=version):
                with self.assertRaises(FileNotFoundError):
                    reference.resolve_robot_asset(self.root, TALOS_UUID + ":" + str(version))

    def test_resolver_rejects_legacy_and_invalid_asset_ids(self):
        for asset_id in ("Talos:1", TALOS_UUID + ":0", TALOS_UUID.upper() + ":1"):
            with self.subTest(asset_id=asset_id):
                with self.assertRaises(ValueError):
                    reference.resolve_robot_asset(self.root, asset_id)

    def test_cli_uuid_index_and_resolve_outputs(self):
        generated = self.cli("uuid", " TaLoS ")
        self.assertEqual(0, generated.returncode, generated.stderr)
        self.assertEqual(TALOS_UUID + "\n", generated.stdout)
        indexed = self.cli("index", self.root)
        self.assertEqual(0, indexed.returncode, indexed.stderr)
        self.assertEqual(
            {TALOS_UUID: {"name": "Talos", "directory": str(self.talos)}},
            json.loads(indexed.stdout),
        )
        resolved = self.cli("resolve", self.root, TALOS_UUID + ":2")
        self.assertEqual(0, resolved.returncode, resolved.stderr)
        self.assertEqual(str(self.talos / "v2" / "urdf") + "\n", resolved.stdout)

    def test_cli_rejects_unknown_uuid_and_legacy_id(self):
        for asset_id in (HEBE_UUID + ":1", "Talos:1"):
            with self.subTest(asset_id=asset_id):
                result = self.cli("resolve", self.root, asset_id)
                self.assertEqual(2, result.returncode)
                self.assertEqual("", result.stdout)
                self.assertIn("error:", result.stderr)
                self.assertNotIn("Traceback", result.stderr)

    def test_module_and_cli_leave_nas_directory_tree_unchanged(self):
        before = self.snapshot()
        reference.robot_uuid("Talos")
        reference.build_robot_index(self.root)
        reference.resolve_robot_asset(self.root, TALOS_UUID + ":1")
        for arguments in (
            ("uuid", "Talos"), ("index", self.root),
            ("resolve", self.root, TALOS_UUID + ":1"),
        ):
            result = self.cli(*arguments)
            self.assertEqual(0, result.returncode, result.stderr)
        self.cli("resolve", self.root, HEBE_UUID + ":1")
        self.assertEqual(before, self.snapshot())


if __name__ == "__main__":
    unittest.main()
