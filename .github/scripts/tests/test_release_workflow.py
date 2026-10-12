"""Run with Python, PyYAML and bump2version installed; never contacts GitHub."""
import ast
import configparser
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest

import yaml


ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = yaml.safe_load((ROOT / ".github/workflows/release.yml").read_text())
JOBS = WORKFLOW["jobs"]


def step(job, name):
    return next(item for item in JOBS[job]["steps"] if item.get("name") == name)


def run_script(script, cwd, **env):
    return subprocess.run(
        ["bash", "-e", "-o", "pipefail", "-c", script],
        cwd=cwd, env={**os.environ, **env}, capture_output=True, text=True,
    )


class ReleaseWorkflowTests(unittest.TestCase):
    def test_scripts_parse(self):
        for job in JOBS.values():
            for item in job["steps"]:
                if "run" not in item:
                    continue
                script = re.sub(r"\$\{\{.*?\}\}", "placeholder", item["run"])
                result = subprocess.run(["bash", "-n"], input=script, text=True,
                                        capture_output=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                for code in re.findall(r"<<'PYEOF'\n(.*?)\nPYEOF", script, re.S):
                    ast.parse(code)

    def test_release_type_selection(self):
        script = step("prepare_release", "Determine release type")["run"]
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "output"
            for release_type in ("patch", "minor", "major", "build"):
                for event in ("push", "workflow_dispatch"):
                    output.write_text("")
                    result = run_script(
                        script, directory, EVENT_NAME=event,
                        COMMIT_MESSAGE=f"[{release_type}] release",
                        INPUT_RELEASE_TYPE=release_type, GITHUB_OUTPUT=str(output),
                    )
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(output.read_text(), f"value={release_type}\n")
            result = run_script(
                script, directory, EVENT_NAME="push", COMMIT_MESSAGE="ordinary commit",
                INPUT_RELEASE_TYPE="", GITHUB_OUTPUT=str(output),
            )
            self.assertNotEqual(result.returncode, 0)

    def test_release_prs_dispatch_classification_and_fail_on_merge_timeout(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for command, script in {
                "git": '#!/bin/sh\n[ "$1" = diff ] && exit 1\nexit 0\n',
                "seq": "#!/bin/sh\necho 1\n",
                "sleep": "#!/bin/sh\nexit 0\n",
                "gh": """#!/bin/sh
echo "$*" >> "$CALL_LOG"
case "$1 $2" in
  "pr list") echo 42 ;;
  "pr merge") exit "$MERGE_STATUS" ;;
  "pr view") printf '%040d\n' 1 ;;
esac
""",
            }.items():
                target = root / command
                target.write_text(script)
                target.chmod(0o755)
            for job, name in (
                ("prepare_release", "Merge version bump through classification gate"),
                ("changelog", "Merge changelog through classification gate"),
            ):
                for status in ("0", "1"):
                    with self.subTest(job=job, merge_status=status):
                        log = root / "calls"
                        log.write_text("")
                        output = root / "output"
                        output.write_text("")
                        result = run_script(
                            step(job, name)["run"], directory,
                            PATH=f"{directory}:{os.environ['PATH']}",
                            CALL_LOG=str(log), MERGE_STATUS=status, VERSION="1.2.3-1",
                            GITHUB_ACTOR="test", GITHUB_REPOSITORY="test/repo",
                            GITHUB_OUTPUT=str(output),
                        )
                        self.assertIn("workflow run classify-prs.yml", log.read_text())
                        self.assertIn("pr merge 42", log.read_text())
                        if status == "0":
                            self.assertEqual(result.returncode, 0, result.stderr)
                            if job == "prepare_release":
                                self.assertIn("value=" + "0" * 39 + "1", output.read_text())
                        else:
                            self.assertNotEqual(result.returncode, 0)
                            self.assertIn("Timed out", result.stderr)

    def test_all_bumps_keep_version_fields_consistent(self):
        config = configparser.ConfigParser(interpolation=None)
        config.read(ROOT / ".bumpversion.cfg")
        major, minor, patch, build = map(
            int, re.fullmatch(r"(\d+)\.(\d+)\.(\d+)-(\d+)",
                             config["bumpversion"]["current_version"]).groups(),
        )
        expected = {
            "patch": f"{major}.{minor}.{patch + 1}-1",
            "minor": f"{major}.{minor + 1}.0-1",
            "major": f"{major + 1}.0.0-1",
            "build": f"{major}.{minor}.{patch}-{build + 1}",
        }
        files = [section.split(":", 2)[2] for section in config.sections()
                 if section.startswith("bumpversion:file:")]
        for release_type, version in expected.items():
            with self.subTest(release_type=release_type), tempfile.TemporaryDirectory() as directory:
                destination = Path(directory)
                for filename in [".bumpversion.cfg", *files]:
                    target = destination / filename
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(ROOT / filename, target)
                result = run_script(
                    step("prepare_release", "Bump project version")["run"],
                    directory, RELEASE_TYPE=release_type,
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                result = run_script(
                    step("prepare_release", "Update all version fields")["run"], directory,
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                props = (destination / "Directory.Build.props").read_text()
                self.assertIn(f"<Version>{version}</Version>", props)
                numeric = version.replace("-", ".")
                for field in ("AssemblyVersion", "FileVersion"):
                    self.assertIn(f"<{field}>{numeric}</{field}>", props)
                for filename in files:
                    text = (destination / filename).read_text()
                    if filename.endswith((".cs", ".toml")):
                        self.assertIn(f'"{version.split("-")[0]}"', text)
                    else:
                        self.assertIn(version, text)

    def test_changelog_works_before_draft_tag_exists(self):
        with tempfile.TemporaryDirectory() as directory:
            env = {
                "GIT_AUTHOR_NAME": "Release test", "GIT_AUTHOR_EMAIL": "test@example.com",
                "GIT_COMMITTER_NAME": "Release test",
                "GIT_COMMITTER_EMAIL": "test@example.com",
                "GIT_CONFIG_NOSYSTEM": "1", "GIT_CONFIG_GLOBAL": os.devnull,
            }
            def git(*args):
                result = subprocess.run(
                    ["git", *args], cwd=directory, env={**os.environ, **env},
                    capture_output=True, text=True,
                )
                self.assertEqual(result.returncode, 0, result.stderr)
                return result.stdout.strip()

            git("init")
            changelog = Path(directory) / "CHANGELOG.md"
            changelog.write_text("# Changelog\n\n")
            git("add", "CHANGELOG.md")
            git("commit", "-m", "old change")
            git("tag", "v1.0.0-1")
            git("commit", "--allow-empty", "-m", "[patch] Fix release regression")
            git("commit", "--allow-empty", "-m", "[skip ci] Bump version to v1.0.1-1")
            code = step("changelog", "Generate changelog entry")["run"]
            code = code.replace("${{ github.repository }}", "example/repository")
            result = run_script(code, directory, VERSION="1.0.1-1")
            self.assertEqual(result.returncode, 0, result.stderr)
            notes = changelog.read_text()
            self.assertIn("Fix release regression", notes)
            self.assertNotIn("old change", notes)
            self.assertNotIn("Bump version", notes)
            self.assertEqual(git("tag", "--list", "v1.0.1-1"), "")

    def test_artifacts_are_pinned_and_publication_waits_for_success(self):
        sha = "${{ needs.prepare_release.outputs.release_hash }}"
        for name, job in JOBS.items():
            if name == "prepare_release":
                continue
            checkout = next(item for item in job["steps"]
                            if item.get("uses", "").startswith("actions/checkout@"))
            self.assertEqual(checkout["with"]["ref"], sha)
        self.assertEqual(step("create_release", "Create draft release")["with"]["target_commitish"], sha)
        self.assertIn("build_binaries", JOBS["publish_release"]["needs"])
        self.assertIn("changelog", JOBS["publish_release"]["needs"])
        self.assertIn("build_and_push_docker", JOBS["changelog"]["needs"])
        for job in JOBS.values():
            self.assertNotIn("always()", job.get("if", ""))
        content = (ROOT / ".github/workflows/release.yml").read_text()
        self.assertNotIn("secrets.PAT", content)
        self.assertNotIn("GPG", content)
        self.assertNotIn("git commit -S", content)
        self.assertNotIn("--gpg-sign", content)
        self.assertEqual(WORKFLOW["permissions"]["actions"], "write")
        self.assertEqual(WORKFLOW["permissions"]["pull-requests"], "write")
        self.assertNotIn("--force", content)
        self.assertNotIn("--no-verify", content)


if __name__ == "__main__":
    unittest.main()
