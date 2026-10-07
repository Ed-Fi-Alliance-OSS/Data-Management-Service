#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

"""Access matrix for the review-variant credentials. Read-only (token + GET); makes no changes.

For each credential in the JSON written by bootstrap/add-review-variants.ps1 -OutFile, gets a token
through the deployment's DMS /oauth/token proxy and prints how many schools, students and
assessments it can see (Total-Count). Never prints a secret or a token. Exits nonzero when a token
request fails.

TLS is VERIFIED by default, since key/secret and bearer tokens are sent; set INSECURE=1 for a
self-signed certificate.

Expected on Grand Bend (DS 5.2: 3 schools / 960 students; DS 6.1 + educator-prep: 9 / 1959):
  SISVendor / EdFiSandbox at district 255901 -> every school and student
  SISVendor at school 255901107               -> that school's 400 students only
  AssessmentVendor at 255901 (uri://one.example.com) -> 403 on schools, 0 assessments

Usage:
  FQDN=your-host ./sample-variants.py ~/review-variants.json
  INSECURE=1 FQDN=localhost SCHOOL_YEAR=2025 ./sample-variants.py ~/review-variants.json
"""

import base64
import json
import os
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request


def call(context, url, data=None, headers=None):
    request = urllib.request.Request(url, data=data, headers=headers or {})
    try:
        with urllib.request.urlopen(request, context=context, timeout=60) as response:
            return response.status, {k.lower(): v for k, v in response.headers.items()}, response.read()
    except urllib.error.HTTPError as error:
        return error.code, {k.lower(): v for k, v in error.headers.items()}, error.read()


def main() -> int:
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    credentials = json.load(open(sys.argv[1], encoding="utf-8"))
    credentials = credentials if isinstance(credentials, list) else [credentials]
    host = os.environ.get("FQDN", "localhost")
    school_year = os.environ.get("SCHOOL_YEAR", "2025")
    context = ssl.create_default_context()
    if os.environ.get("INSECURE") == "1":
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE

    print(f"{'environment':22} {'application':46} {'token':>5} {'schools':>8} {'students':>9} {'assessments':>12}")
    failures = 0
    for credential in credentials:
        environment = credential["Environment"]
        # "single-tenant" or bootstrap's "single-tenant/full"; "multi-tenant/<tenant>".
        if environment.startswith("single-tenant"):
            path = "/st-dms"
        else:
            path = f"/mt-dms/{environment.split('/', 1)[1]}/{school_year}"
        base = f"https://{host}{path}"
        basic = base64.b64encode(f"{credential['Key']}:{credential['Secret']}".encode()).decode()
        code, _, body = call(
            context,
            f"{base}/oauth/token",
            urllib.parse.urlencode({"grant_type": "client_credentials"}).encode(),
            {"Authorization": f"Basic {basic}", "Content-Type": "application/x-www-form-urlencoded"},
        )
        if code != 200:
            failures += 1
            print(f"{environment:22} {credential.get('Application', ''):46} {code:>5}  token request failed")
            continue
        auth = {"Authorization": f"Bearer {json.loads(body)['access_token']}"}
        counts = []
        for resource in ("schools", "students", "assessments"):
            status, headers, _ = call(context, f"{base}/data/ed-fi/{resource}?limit=1&totalCount=true", headers=auth)
            counts.append(headers.get("total-count", "?") if status == 200 else f"HTTP {status}")
        print(f"{environment:22} {credential.get('Application', ''):46} {code:>5} {counts[0]:>8} {counts[1]:>9} {counts[2]:>12}")
    print(f"== {len(credentials) - failures}/{len(credentials)} tokens issued")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
