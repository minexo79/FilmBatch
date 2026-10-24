#!/usr/bin/env python3
"""Behavioral checks for CircleCI webhook admission; no network or credentials."""
import copy
import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch
from urllib.error import HTTPError

SPEC = importlib.util.spec_from_file_location('ci_admission', Path(__file__).with_name('ci-admission.py'))
admission = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(admission)

REPOSITORY = 'minexo79/FilmBatch'
REVISION = '3cb45164e17d4f59603024f69fd5479051f0773f'
PIPELINE = {
    'trigger': {'type': 'webhook'},
    'vcs': {'branch': 'dev-ios-app', 'revision': REVISION, 'review_id': '15'},
}
PR = {
    'number': 15, 'state': 'open',
    'head': {'ref': 'dev-ios-app', 'sha': REVISION},
    'base': {'ref': 'pre-merge', 'repo': {'full_name': REPOSITORY}},
}


def response(payload):
    return io.BytesIO(json.dumps(payload).encode())


class AdmissionTests(unittest.TestCase):
    def check_pr(self, pull_request, expected):
        with patch.object(admission, 'urlopen', return_value=response([pull_request])):
            self.assertEqual(expected, admission.should_run_ci(PIPELINE, REPOSITORY))

    def test_legacy_webhook_pr_to_pre_merge_is_admitted_without_app_metadata(self):
        self.check_pr(PR, True)

    def test_pr_to_master_is_admitted(self):
        pr = copy.deepcopy(PR)
        pr['base']['ref'] = 'master'
        self.check_pr(pr, True)

    def test_wrong_target_closed_stale_head_and_wrong_repository_are_rejected(self):
        for field, value in [('target', 'develop'), ('state', 'closed'), ('sha', '0' * 40),
                             ('head', 'another-feature'), ('repository', 'another/FilmBatch')]:
            with self.subTest(field=field):
                pr = copy.deepcopy(PR)
                if field == 'target':
                    pr['base']['ref'] = value
                elif field == 'state':
                    pr['state'] = value
                elif field == 'repository':
                    pr['base']['repo']['full_name'] = value
                else:
                    pr['head']['sha' if field == 'sha' else 'ref'] = value
                self.check_pr(pr, False)

    def test_feature_push_without_open_pr_does_not_start_builds(self):
        with patch.object(admission, 'urlopen', return_value=response([])):
            self.assertFalse(admission.should_run_ci(PIPELINE, REPOSITORY))

    def test_manual_schedule_and_unknown_triggers_cannot_use_an_existing_pr(self):
        for trigger in ['api', 'scheduled', 'schedule', 'custom_webhook', 'unknown']:
            with self.subTest(trigger=trigger):
                pipeline = copy.deepcopy(PIPELINE)
                pipeline['trigger']['type'] = trigger
                with patch.object(admission, 'urlopen', side_effect=AssertionError('Unexpected PR lookup')):
                    self.assertFalse(admission.should_run_ci(pipeline, REPOSITORY))

    def test_tags_do_not_build_even_on_a_protected_branch(self):
        pipeline = copy.deepcopy(PIPELINE)
        pipeline['vcs'].update(branch='master', tag='v0.3.1')
        with patch.object(admission, 'urlopen', side_effect=AssertionError('Unexpected PR lookup')):
            self.assertFalse(admission.should_run_ci(pipeline, REPOSITORY))

    def test_target_branch_and_queue_updates_do_not_require_an_open_pr(self):
        for branch in ['master', 'pre-merge', 'gh-readonly-queue/master/pr-15-abc',
                       'gh-readonly-queue/pre-merge/pr-15-abc']:
            with self.subTest(branch=branch):
                pipeline = copy.deepcopy(PIPELINE)
                pipeline['vcs']['branch'] = branch
                with patch.object(admission, 'urlopen', side_effect=AssertionError('Unexpected PR lookup')):
                    self.assertTrue(admission.should_run_ci(pipeline, REPOSITORY))

    def test_lookalike_queue_is_not_a_target_branch_update(self):
        pipeline = copy.deepcopy(PIPELINE)
        pipeline['vcs']['branch'] = 'gh-readonly-queue/pre-merge-next/pr-15-abc'
        with patch.object(admission, 'urlopen', return_value=response([])):
            self.assertFalse(admission.should_run_ci(pipeline, REPOSITORY))

    def test_admission_finds_a_matching_pr_after_the_first_api_page(self):
        unrelated = []
        for number in range(100):
            pr = copy.deepcopy(PR)
            pr.update(number=number + 100, state='closed')
            unrelated.append(pr)
        with patch.object(admission, 'urlopen', side_effect=[response(unrelated), response([PR])]):
            self.assertTrue(admission.should_run_ci(PIPELINE, REPOSITORY))

    def test_github_lookup_failure_is_not_reported_as_a_successful_skip(self):
        with patch.object(admission, 'urlopen', side_effect=HTTPError('https://api.github.com/', 403, 'Forbidden', {}, None)):
            with self.assertRaises(HTTPError):
                admission.should_run_ci(PIPELINE, REPOSITORY)

    def test_malformed_lookup_does_not_admit_builds(self):
        with patch.object(admission, 'urlopen', return_value=response({'message': 'not a PR list'})):
            with self.assertRaises(ValueError):
                admission.should_run_ci(PIPELINE, REPOSITORY)


if __name__ == '__main__':
    unittest.main()
