#!/usr/bin/env python3
"""Continue webhook CI only for target-branch updates or matching open PRs.

Uses public CircleCI/GitHub metadata for this public repository. The only
credential is CircleCI's automatic, single-use CIRCLE_CONTINUATION_KEY.
"""
import json
import os
from pathlib import Path
import sys
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen


TARGETS = ('master', 'pre-merge')


def get_json(url):
    request = Request(url, headers={'Accept': 'application/json', 'User-Agent': 'FilmBatch-CI'})
    with urlopen(request, timeout=15) as response:
        return json.load(response)


def should_run_ci(pipeline, repository):
    # API/manual/scheduled pipelines must not borrow the eligibility of an open PR.
    if pipeline['trigger']['type'] != 'webhook':
        return False
    vcs = pipeline['vcs']
    if vcs.get('tag'):
        return False
    branch, revision = vcs['branch'], vcs['revision']
    if branch in TARGETS or any(branch.startswith('gh-readonly-queue/' + target + '/') for target in TARGETS):
        return True

    # OAuth webhook metadata has a head branch, not GitHub App PR base fields.
    # Check the current head SHA as well: old pipelines must not build a newer PR.
    page = 1
    while True:
        url = ('https://api.github.com/repos/' + repository + '/commits/'
               + quote(revision, safe='') + '/pulls?per_page=100&page=' + str(page))
        pull_requests = get_json(url)
        if not isinstance(pull_requests, list):
            raise ValueError('GitHub did not return a pull request list')
        for pr in pull_requests:
            if (pr['state'] == 'open' and pr['base']['ref'] in TARGETS
                    and pr['base']['repo']['full_name'].casefold() == repository.casefold()
                    and pr['head']['ref'] == branch and pr['head']['sha'] == revision):
                return True
        if len(pull_requests) < 100:
            return False
        page += 1


def main():
    try:
        key = os.environ['CIRCLE_CONTINUATION_KEY']
        if not key:
            raise ValueError('CIRCLE_CONTINUATION_KEY is empty; enable setup workflows')
        pipeline_id = os.environ['CIRCLE_PIPELINE_ID']
        repository = os.environ['CIRCLE_PROJECT_USERNAME'] + '/' + os.environ['CIRCLE_PROJECT_REPONAME']
        pipeline = get_json('https://circleci.com/api/v2/pipeline/' + quote(pipeline_id, safe=''))
        allowed = should_run_ci(pipeline, repository)
        configuration = (Path(__file__).resolve().parent.parent / '.circleci' / 'ci.yml').read_text()
        payload = json.dumps({'continuation-key': key, 'configuration': configuration,
                              'parameters': {'run_ci': allowed}}).encode()
    except (KeyError, ValueError, OSError) as error:
        print('CI admission failed: ' + str(error), file=sys.stderr)
        return 1

    print('CI admission: run_ci=' + str(allowed).lower())
    request = Request('https://circleci.com/api/v2/pipeline/continue', data=payload,
                      headers={'Content-Type': 'application/json', 'Accept': 'application/json'}, method='POST')
    try:
        # A continuation is single-use. Never retry a POST after an unknown outcome.
        with urlopen(request, timeout=15) as response:
            if response.status != 200:
                print('Continuation OUTCOME_UNKNOWN: HTTP ' + str(response.status) + '; do not retry.', file=sys.stderr)
                return 1
    except HTTPError as error:
        if error.code >= 500:
            print('Continuation OUTCOME_UNKNOWN: HTTP ' + str(error.code) + '; do not retry.', file=sys.stderr)
        else:
            print('CircleCI rejected continuation: HTTP ' + str(error.code), file=sys.stderr)
        return 1
    except (URLError, TimeoutError, OSError):
        print('Continuation OUTCOME_UNKNOWN; do not retry this setup workflow.', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
