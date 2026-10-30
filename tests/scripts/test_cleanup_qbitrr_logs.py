import importlib.util
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("cleanup", Path(__file__).parents[2] / "scripts/cleanup-qbitrr-logs.py")
cleanup = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cleanup)


class CleanupTests(unittest.TestCase):
    def test_only_verified_inactive_legacy_files_are_removed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            contents = {
                "All.log": "\x1b[32m[2026-10-17 13:37:34] INFO : qBitrr.Manager : ready\x1b[0m\n",
                "All.log.old": "[2026-10-17 13:37:34] INFO : qBitrr : ready\n",
                "Main.log": "INFO : Torrentarr : ready\n",
                "Manager.log": "[2026-10-17 13:37:34] INFO : qBitrr.Manager : active\n",
                "webui-20261030.log": "INFO : qBitrr : unrelated\n",
                "worker-Sonarr20261030.log": "[INF] ready\n",
                "unknown.log": "INFO : qBitrr : unrelated\n",
            }
            for name, text in contents.items():
                (root / name).write_text(text)
            with patch.object(cleanup.shutil, "which", return_value="lsof"), patch.object(cleanup, "inactive", side_effect=lambda p: p.name != "Manager.log"):
                self.assertEqual(cleanup.cleanup(root), ["All.log", "All.log.old"])
                self.assertTrue((root / "All.log").exists())
                self.assertEqual(cleanup.cleanup(root, apply=True), ["All.log", "All.log.old"])
            self.assertEqual({p.name for p in root.iterdir()}, set(contents) - {"All.log", "All.log.old"})

    def test_symlinks_and_mixed_torrentarr_history_are_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Main.log").write_text("qBitrr: ready\nTorrentarr: ready\n")
            (root / "All.log").symlink_to(root / "Main.log")
            self.assertFalse(cleanup.verified(root / "All.log"))
            self.assertFalse(cleanup.verified(root / "Main.log"))
            (root / "Manager.log").write_text("Some unrelated program mentions qBitrr: ready\n")
            self.assertFalse(cleanup.verified(root / "Manager.log"))

    @unittest.skipUnless(cleanup.shutil.which("lsof"), "lsof is not installed")
    def test_real_open_file_is_not_considered_inactive(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "All.log"
            with path.open("w") as output:
                output.write("qBitrr: active\n")
                output.flush()
                self.assertFalse(cleanup.inactive(path))

    def test_uncertain_handle_inspection_never_deletes(self):
        with patch.object(cleanup.subprocess, "run") as run:
            run.return_value.returncode = 1
            run.return_value.stderr = "permission denied"
            run.return_value.stdout = ""
            with self.assertRaises(RuntimeError):
                cleanup.inactive(Path("All.log"))
