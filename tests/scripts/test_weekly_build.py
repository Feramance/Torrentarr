import json
import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest


WORKFLOW = Path(__file__).parents[2] / '.github/workflows/weekly-build.yml'
NOW = 2000000000


class WeeklyBuildTests(unittest.TestCase):
    def run_gate(self, age=604801, paths='webui/package-lock.json\nCHANGELOG.md',
                 subjects='chore: update dependencies', event='schedule', opt_in='false',
                 releases=None, api_failure=False, git_failure='', count=1):
        step = WORKFLOW.read_text().split('      id: should_release\n', 1)[1]
        script = textwrap.dedent(step.split('      run: |\n', 1)[1].split('\n    - name:', 1)[0])
        if releases is None:
            releases = [[{'tag_name': 'v6.16.1-1', 'published_at': '2026-01-01T00:00:00Z', 'draft': False}]]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            commands = {
                'gh': '[[ "$API_FAILURE" != true ]] || exit 1\nprintf "%s" "$RELEASES"',
                'date': 'if [[ "$*" == *"-d"* ]]; then echo "$RELEASE_EPOCH"; else echo "$NOW"; fi',
                'git': '''
                    [[ "$1" != "$GIT_FAILURE" ]] || exit 1
                    case "$1" in
                      rev-list) echo "$COUNT" ;;
                      log) printf '%s\\n' "$SUBJECTS" ;;
                      diff) printf '%s\\n' "$PATHS" ;;
                      *) exit 1 ;;
                    esac
                ''',
            }
            for name, body in commands.items():
                executable = root / name
                executable.write_text('#!/bin/bash\n' + textwrap.dedent(body))
                executable.chmod(0o755)
            output = root / 'output'
            env = dict(os.environ, PATH=f'{root}:{os.environ["PATH"]}',
                       GITHUB_OUTPUT=str(output), GITHUB_REPOSITORY='Feramance/Torrentarr',
                       EVENT_NAME=event, DISPATCH_BUILD_RELEASE=opt_in,
                       RELEASES='\n'.join(json.dumps(page) for page in releases),
                       API_FAILURE=str(api_failure).lower(), NOW=str(NOW),
                       RELEASE_EPOCH=str(NOW-age), COUNT=str(count), SUBJECTS=subjects,
                       PATHS=paths, GIT_FAILURE=git_failure)
            result = subprocess.run(['bash', '-c', script], env=env, capture_output=True, text=True)
            return result.returncode, output.read_text() if output.exists() else '', result.stdout

    def test_age_boundary(self):
        for age, expected in [(0, False), (604799, False), (604800, False), (604801, True)]:
            with self.subTest(age=age):
                code, output, _ = self.run_gate(age=age)
                self.assertEqual(code, 0)
                self.assertEqual(output, f'release={str(expected).lower()}\n')

    def test_ineligible_ranges_are_successful_skips(self):
        for args in [dict(paths='CHANGELOG.md'), dict(paths='src/App.cs'),
                     dict(subjects='[patch] Fix a bug'), dict(paths=''), dict(count=0)]:
            with self.subTest(args=args):
                code, output, _ = self.run_gate(**args)
                self.assertEqual((code, output), (0, 'release=false\n'))

    def test_manual_runs_obey_opt_in_and_age(self):
        for opt_in, age, expected in [('false', 604801, False), ('true', 1, False), ('true', 604801, True)]:
            code, output, _ = self.run_gate(event='workflow_dispatch', opt_in=opt_in, age=age)
            self.assertEqual((code, output), (0, f'release={str(expected).lower()}\n'))

    def test_empty_release_history_skips(self):
        self.assertEqual(self.run_gate(releases=[[]])[:2], (0, 'release=false\n'))

    def test_selects_newest_published_release_across_pages_including_prereleases(self):
        releases = [[{'tag_name': 'old', 'published_at': '2025-01-01T00:00:00Z', 'draft': False}],
                    [{'tag_name': 'draft', 'published_at': '2027-01-01T00:00:00Z', 'draft': True},
                     {'tag_name': 'new', 'published_at': '2026-01-01T00:00:00Z', 'draft': False, 'prerelease': True}]]
        code, output, log = self.run_gate(releases=releases)
        self.assertEqual((code, output), (0, 'release=true\n'))
        self.assertIn('Commits since new:', log)

    def test_api_and_git_errors_fail_without_dispatch(self):
        for args in [dict(api_failure=True), dict(git_failure='rev-list'),
                     dict(git_failure='log'), dict(git_failure='diff')]:
            with self.subTest(args=args):
                code, output, _ = self.run_gate(**args)
                self.assertNotEqual(code, 0)
                self.assertNotIn('release=true', output)


SHA = 'a' * 40


def ci_run(run_id=1, status='completed', conclusion='success', sha=SHA,
           branch='master', event='push'):
    return dict(databaseId=run_id, headSha=sha, headBranch=branch,
                event=event, status=status, conclusion=conclusion)


class BuildTestGateTests(unittest.TestCase):
    def run_step(self, responses, dispatch=False, remote_sha=SHA, api_failure=False):
        name = 'Dispatch build release' if dispatch else 'Wait for passing Build and Test'
        step = WORKFLOW.read_text().split(f'    - name: {name}\n', 1)[1]
        script = textwrap.dedent(step.split('      run: |\n', 1)[1].split('\n    - name:', 1)[0])
        script = script.replace('${{ steps.build_tests.outputs.sha }}', SHA)
        script = script.replace('${{ github.repository }}', 'Feramance/Torrentarr')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            commands = {
                'gh': '''#!/usr/bin/env python3
import json, os, pathlib, sys
root = pathlib.Path(os.environ['MOCK_ROOT'])
with (root / 'calls').open('a') as stream:
    stream.write(json.dumps(sys.argv[1:]) + '\\n')
if os.environ['API_FAILURE'] == 'true':
    sys.exit(1)
if sys.argv[1:3] == ['workflow', 'run']:
    sys.exit(0)
assert sys.argv[1:3] == ['run', 'list']
assert sys.argv[sys.argv.index('--workflow') + 1] == 'build.yml'
assert sys.argv[sys.argv.index('--branch') + 1] == 'master'
assert sys.argv[sys.argv.index('--commit') + 1] == 'a' * 40
count = len((root / 'calls').read_text().splitlines()) - 1
responses = json.loads(os.environ['RESPONSES'])
print(json.dumps(responses[min(count, len(responses)-1)]))
''',
                'git': '''#!/bin/bash
case "$1" in
  rev-parse) echo "$SHA" ;;
  ls-remote) printf '%s\\trefs/heads/master\\n' "$REMOTE_SHA" ;;
  *) exit 1 ;;
esac
''',
                'sleep': '''#!/bin/bash
[[ "$1" == 30 ]] || exit 1
echo sleep >> "$MOCK_ROOT/sleeps"
''',
            }
            for name, body in commands.items():
                executable = root / name
                executable.write_text(body)
                executable.chmod(0o755)
            output = root / 'output'
            env = dict(os.environ, PATH=f'{root}:{os.environ["PATH"]}',
                       MOCK_ROOT=str(root), GITHUB_OUTPUT=str(output),
                       GITHUB_REPOSITORY='Feramance/Torrentarr', SHA=SHA,
                       REMOTE_SHA=remote_sha, RESPONSES=json.dumps(responses),
                       API_FAILURE=str(api_failure).lower())
            result = subprocess.run(['bash', '-c', script], env=env, capture_output=True, text=True)
            calls = (root / 'calls').read_text() if (root / 'calls').exists() else ''
            sleeps = len((root / 'sleeps').read_text().splitlines()) if (root / 'sleeps').exists() else 0
            return result, output.read_text() if output.exists() else '', calls, sleeps

    def test_matching_success_passes(self):
        for event in ['push', 'schedule', 'workflow_dispatch']:
            result, output, _, sleeps = self.run_step([[ci_run(event=event)]])
            self.assertEqual(result.returncode, 0)
            self.assertIn('passed=true\n', output)
            self.assertIn(f'sha={SHA}\n', output)
            self.assertEqual(sleeps, 0)

    def test_pending_waits_until_success(self):
        result, output, _, sleeps = self.run_step([
            [ci_run(status='queued', conclusion='')],
            [ci_run(status='in_progress', conclusion='')], [ci_run()]])
        self.assertEqual(result.returncode, 0)
        self.assertIn('passed=true\n', output)
        self.assertEqual(sleeps, 2)

    def test_failed_cancelled_missing_and_wrong_runs_skip(self):
        for runs in [[], [ci_run(conclusion='failure')], [ci_run(conclusion='cancelled')],
                     [ci_run(sha='b'*40)], [ci_run(event='pull_request')],
                     [ci_run(branch='develop')]]:
            with self.subTest(runs=runs):
                result, output, _, sleeps = self.run_step([runs])
                self.assertEqual(result.returncode, 0)
                self.assertIn('passed=false\n', output)
                self.assertNotIn('passed=true', output)
                self.assertEqual(sleeps, 0)

    def test_latest_run_wins_over_older_success(self):
        result, output, _, _ = self.run_step([[ci_run(), ci_run(run_id=2, conclusion='failure')]])
        self.assertEqual(result.returncode, 0)
        self.assertIn('passed=false\n', output)

    def test_timeout_skips_after_thirty_minutes(self):
        result, output, calls, sleeps = self.run_step([[ci_run(status='in_progress', conclusion='')]])
        self.assertEqual(result.returncode, 0)
        self.assertIn('passed=false\n', output)
        self.assertEqual(sleeps, 60)
        self.assertEqual(len(calls.splitlines()), 61)
        self.assertIn('30 minutes', result.stdout)

    def test_api_failure_blocks_release(self):
        result, output, _, _ = self.run_step([[]], api_failure=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn('passed=true', output)

    def test_dispatch_requires_unchanged_master(self):
        for remote_sha, expected in [(SHA, True), ('b'*40, False), ('', False)]:
            result, _, calls, _ = self.run_step([[]], dispatch=True, remote_sha=remote_sha)
            self.assertEqual(result.returncode, 0)
            self.assertEqual('workflow' in calls, expected)

    def test_workflow_wires_ci_gate_and_regression_tests(self):
        workflow = WORKFLOW.read_text()
        self.assertIn("if: steps.should_release.outputs.release == 'true' && steps.build_tests.outputs.passed == 'true'", workflow)
        build = (WORKFLOW.parent / 'build.yml').read_text()
        self.assertIn("python3 -m unittest discover -s tests/scripts -p 'test_*.py'", build)


if __name__ == '__main__':
    unittest.main()
