---
jira: DMS-1606
---

# ADR: OAuth error responses from DMS `/oauth/token`

**Status:** Accepted and implemented in DMS (`src/dms`). Applies to the DMS token endpoint, not CMS.
**Date:** 2026-10-20

## Decision

Every in-scope DMS `POST /oauth/token` failure uses an OAuth JSON object with exactly two root
members: `error` and a nonempty `error_description`. DMS returns `application/json` (with an optional
UTF-8 charset) and its own percent-encoded `X-Correlation-ID` response header. Every `401` also
returns `WWW-Authenticate: Basic realm="EdFi.DataManagementService"`. DMS never copies an upstream
challenge, correlation header, or other response header.

The failure contract applies to both supported token request content types and to the existing root
and qualified token routes. Both routes return the same token error contract, and successful token
issuance remains unchanged. Endpoint metadata selects token-specific limiter and exception
responses. Unsupported content types, other methods, non-token endpoints, an already-started
response, a disconnected client, and failures outside DMS application control keep their existing
behavior. DMS `404` remains `404`; this decision does not change it to ODS/API `415`.

| Condition | Status and OAuth error |
| --- | --- |
| Missing client credentials; rejected non-Basic authorization scheme | `401 invalid_client` |
| Partial body credentials, empty authorization, malformed Basic addition, mixed mechanisms, duplicate form fields, malformed form data or form-parser limits, invalid JSON or unsupported JSON charset, missing grant, or a body credential exceeding its 2048-character cap | `400 invalid_request` |
| Unsupported grant | `400 unsupported_grant_type` |
| Provider `401`, or provider `400 invalid_client` after caller Authorization authentication | `401 invalid_client` |
| Provider `400` with one of the six recognized RFC 6749 errors | `400` with that code, except the preceding `invalid_client` rule |
| Provider `400` without a usable recognized error code | `502 server_error` |
| Provider `429` | `429 temporarily_unavailable` |
| Provider `409` token-grant contention | `503 temporarily_unavailable` |
| Other provider status or transport failure | `502 server_error` |
| Token request rate-limit middleware rejection | `429 temporarily_unavailable` |
| Writable token request body too large | `413 invalid_request` |
| Writable unexpected DMS token-endpoint failure | `500 server_error` |

`temporarily_unavailable` and `server_error` are documented DMS token-proxy extensions, not RFC
6749 section 5.2 token errors. Recognized provider 400 codes are exactly the ordinal lowercase
strings `invalid_request`, `invalid_client`, `invalid_grant`, `unauthorized_client`,
`unsupported_grant_type`, and `invalid_scope`; do not case-fold unknown values or stringify
non-string JSON values. Preserve existing 429/502/503 statuses. Do not forward a provider-authored
`Retry-After`. A recognized canonical token-limit response from either accepted CMS URN is rebuilt
into DMS's reuse-until-expiry guidance with its positive integer limit. Other 429 bodies use fixed
DMS text.

For every in-scope error, `X-Correlation-ID` is `Uri.EscapeDataString` of the normalized trace ID.
Clients decode it once using UTF-8 percent decoding to find the matching DMS log entry. A literal
percent sign is encoded as `%25`; clients must not decode twice. Logging continues to use the
normalized trace value. The selected-summary Information event for provider 401 fallback withholds
arbitrary values, and malformed or nested provider content never becomes a response field.
Provider-authored `error_description` is usable only when it is a JSON string whose decoded
characters are U+0020-U+0021, U+0023-U+005B, or U+005D-U+007E; otherwise DMS uses fixed safe text.
Existing bounded/sanitized 502 warning and transport error logging remain as documented in the
upstream disclosure ADR.

## Client migration

Before this change, token failures could use Ed-Fi problem-details members such as `type`, `title`,
`status`, `detail`, and `correlationId`. Clients should read the OAuth fields at the root instead:

```json
{
  "error": "invalid_client",
  "error_description": "The upstream identity service rejected the request credentials."
}
```

Do not depend on problem-details fields or a JSON `correlationId`. For a `401`, inspect the fixed
Basic challenge. For diagnostics, read the `X-Correlation-ID` response header and decode it once.
Clients should continue to reuse access tokens until expiry: a canonical token-limit response
remains `429` and communicates the parsed limit in `error_description`.

Missing and partial credentials are distinct in DMS: no supplied nonblank body credential yields
`401 invalid_client`; exactly one nonblank body credential yields `400 invalid_request`. A body
credential pair combined with Basic remains `400 invalid_request`. A recognized provider 400
`invalid_client` becomes `401` only when the caller used Authorization authentication; body
authentication remains `400`. These DMS choices intentionally differ from the ODS/API request
validation matrix.

## Compatibility and ODS/API parity

The approved comparison establishes parity only for supported request families and OAuth response
shape. DMS intentionally retains its unsupported-content-type `404` instead of ODS/API `415`,
distinguishes missing credentials from a partial body pair, returns `400 invalid_request` for mixed
credentials and missing grants, and emits a fixed realm-bearing challenge on token 401s.
Infrastructure status mapping and the encoded correlation response header are DMS-specific. No
claim is made about every deployed client or a live ODS deployment.

The comparison was checked against pinned ODS source at
[`7fcefbdaccab498893098068cb07f2b80fbc222e`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/7fcefbdaccab498893098068cb07f2b80fbc222e):
[TokenController](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/7fcefbdaccab498893098068cb07f2b80fbc222e/Application/EdFi.Ods.Api/Controllers/TokenController.cs),
[TokenError](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/7fcefbdaccab498893098068cb07f2b80fbc222e/Application/EdFi.Ods.Api/Models/Tokens/TokenError.cs), and
[ClientCredentialsTokenRequestProvider](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/7fcefbdaccab498893098068cb07f2b80fbc222e/Application/EdFi.Ods.Api/Providers/ClientCredentialsTokenRequestProvider.cs).
Those files also matched the earlier approved ADR and v7.3.2 release pin during the parity review.
This is a source comparison, not an assertion that all ODS middleware responses or deployed
configurations behave identically.

## Supersession and scope boundaries

This ADR supersedes only the DMS token-failure response envelope, classifications and correlation
transport described in [ADR DMS-1604](./adr-oauth-token-client-credentials-DMS-1604.md). Its
credential transport, normalization, validation order and disclosure decisions remain in force
except for the missing/partial classification and provider 400 mapping stated here.

This is the single DMS-1606 fix for [DMS-1549](https://edfi.atlassian.net/browse/DMS-1549): a
malformed or non-object provider 401 remains `401 invalid_client`, uses the fixed safe fallback,
and emits the selected-summary Information event. A genuine transport failure remains `502
server_error`. The prior problem-details expectation is replaced; the selected-summary policy is
preserved. The bounded/sanitized Warning on other provider failures remains subject to
[DMS-1550](https://edfi.atlassian.net/browse/DMS-1550) and is not changed here.

CMS token errors, token issuance, grants, persistence, schema hashes, global failure serialization,
general correlation middleware, tenant authorization, warning-log policy and Jira workflow remain
outside this decision.

## References

- [DMS-1606](https://edfi.atlassian.net/browse/DMS-1606)
- [Approved DMS-1606 design specification](./design/DMS-1606-oauth-token-errors.md)
- [DMS-1604 client credentials ADR](./adr-oauth-token-client-credentials-DMS-1604.md)
- [Upstream error disclosure ADR](./adr-oauth-upstream-error-disclosure.md)
- [RFC 6749 section 5.2](https://www.rfc-editor.org/rfc/rfc6749#section-5.2)
