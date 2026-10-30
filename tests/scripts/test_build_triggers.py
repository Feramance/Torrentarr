import fnmatch
from pathlib import Path
import re
import unittest


WORKFLOW = Path(__file__).parents[2] / '.github/workflows/build.yml'


class BuildTriggerTests(unittest.TestCase):
    def test_stacked_prs_run_build_matrix_without_enabling_branch_push_releases(self):
        workflow = WORKFLOW.read_text()
        def branches(event):
            match = re.search(rf'  {event}:\n    branches: \[([^\]]+)\]', workflow)
            self.assertIsNotNone(match)
            return [pattern.strip() for pattern in match.group(1).split(',')]
        pr_branches = branches('pull_request')
        for target in ['master', 'main', 'develop', 'releases/6.x', 'codex/operational-issues-safety-fixes']:
            with self.subTest(target=target):
                self.assertTrue(any(fnmatch.fnmatchcase(target, pattern) for pattern in pr_branches))
        self.assertFalse(any(fnmatch.fnmatchcase('codex/qbittorrent-log-fixes', pattern) for pattern in branches('push')))
