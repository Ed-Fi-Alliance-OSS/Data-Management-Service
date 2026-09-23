# ADR: Upstream identity service error disclosure at the token proxy

**Status:** Implemented in DMS (`src/dms`). Does not apply to CMS (`src/config`);
see [Scope boundaries](#scope-boundaries). \
**Date:** 2026-09-17 \
**Author:** Stephen Fuqua.

## Executive summary

DMS proxies `POST /oauth/token` to an upstream identity service. When that service rejects or
fails a token request, its response body is **externally generated content**: DMS chooses neither
its wording, its size, nor its shape. That body has two possible destinations — the problem-details
response DMS returns to the caller, and the DMS server log — and the two are governed by different
rules for different reasons.

This ADR fixes the **disclosure contract** for that body: nothing of it reaches the client beyond
the OAuth 2.0 error fields the client is entitled to, and nothing of it reaches the log beyond an
explicitly selected summary. It exists because the same class of defect was reintroduced three
times in a single review cycle, each time by a fix for the previous one (see
[Defects found and their lessons](#defects-found-and-their-lessons)).

## Context

`OAuthManager.GetAccessTokenAsync` forwards a client-credentials token request upstream and then
branches on the upstream status:

| Upstream status             | DMS behavior                                                      |
| --------------------------- | ----------------------------------------------------------------- |
| `200`                       | the upstream response is returned unaltered                       |
| `401`                       | `GenerateUnauthorizedResponse` builds a DMS problem-details `401` |
| `429`                       | `GenerateTooManyTokensResponse` builds a DMS problem-details `429` |
| `409`                       | `503 Service Unavailable` with a fixed retry detail               |
| anything else               | `502 Bad Gateway` with a fixed detail                             |
| transport failure (`catch`) | `502 Bad Gateway` with the same fixed detail                      |

Three facts drive everything below.

1. **`/oauth/token` is unauthenticated.** Anything placed in its response body is readable by any
   caller that can reach the endpoint. There is no authorization check to hide behind.
2. **The upstream body is attacker-influenceable in content *and* in size.** A caller who can
   provoke a large or hostile upstream error can, absent bounds, write an unbounded amount of
   chosen text into the DMS log on every request.
3. **The upstream body is often the only explanation of the rejection.** Identity providers put
   the operative detail in members RFC 6749 never defined — Okta's `errorCode` and `errorSummary`,
   a Keycloak `realm`, a bare `reason` of `"client disabled"`. Discarding the body outright leaves
   an operator holding a correlation ID and no answer.

Facts 1 and 2 pull against fact 3. The decision below is the settlement, not a compromise reached
by accident.

The governing policy for the log side already exists:
[docs/LOGGING.md](../docs/LOGGING.md) states that Information-level request logs must not include
request bodies, response bodies, authorization headers, bearer tokens, API keys, client secrets,
connection strings, raw query strings, arbitrary headers, route values, or raw tenant header
values. That document says nothing specific about the token proxy, which is the gap this ADR
fills.

## Decision

### The client receives contract fields, never a body

| Case                                    | `title`                                | `detail`                      |
| --------------------------------------- | -------------------------------------- | ----------------------------- |
| `502`, upstream-error branch            | `"Upstream service unavailable"`       | `GatewayErrorDetail`          |
| `502`, `catch` branch                   | `"Upstream service unavailable"`       | `GatewayErrorDetail`          |
| `401` with a usable `error_description` | the `error` value, or `"Unauthorized"` | the `error_description` value |
| `401` without one                       | the `error` value, or `"Unauthorized"` | `UnauthorizedFallbackDetail`  |
| `429`, canonical token-limit body       | `"Too Many Tokens"`                    | fixed token-limit detail      |
| `429`, any other body                   | `"Too Many Requests"`                  | fixed rate-limit detail       |
| `503`, upstream `409`                   | `"Service Unavailable"`                | fixed retry detail            |

The two `502` branches are **deliberately indistinguishable from outside**. An upstream that
returned a well-formed HTTP error and an upstream that could not be reached at all are different
conditions for an operator and the same condition for an unauthenticated caller.

A `429` passes nothing through. When the body is the Configuration Service's canonical
token-limit rejection, the only thing read from it is the integer limit, and the `errors` message
is rebuilt around that number from DMS-side text. Any other `429` body gets the generic rate-limit
contract, because the upstream status is trustworthy even when its body is not.

A `409` passes nothing through either. The Configuration Service answers it when a token grant
timed out waiting for a database lock or was chosen as a deadlock victim, which retrying resolves,
so DMS answers the transient-condition `503` rather than the `502` that reports the upstream as
failed. The status is the whole signal: the body is neither parsed nor logged, and the one
Information event the arm emits carries only `TraceId`.

`error` and `error_description` are passed through because they are the OAuth 2.0 error contract
and the client is entitled to them. A body that merely *failed* to contain them is not part of any
contract, and is withheld in full: its wording, error taxonomy, internal hostnames, realm names
and any stack trace it carries are all disclosure.

### The log receives a selected summary, never a body

On the `401` fallback DMS emits one Information event (`DiscardedUnauthorizedDetailTemplate`)
carrying exactly three bound properties: `TraceId`, `StandardFields`, `OtherFieldNames`. A `429`
whose body is not the canonical token-limit rejection emits the same three properties, from the
same summary, at Warning: an unrecognized `429` can mean the Configuration Service's message and
the DMS parser have drifted apart, which is a condition an operator must act on. A canonical
token-limit `429` discards nothing, so it logs nothing.

- **Only on the fallback.** A `401` is routine and client-triggered; every mistyped client secret
  produces one. When the upstream did supply an `error_description`, the client already has the
  explanation and nothing is being discarded, so there is nothing to record.
- **Information, not Warning.** A rejected credential is not a condition an operator must act on,
  so it must not reach the stream watched for conditions that are.
- **Information, not Debug.** DMS ships at Information. A Debug event would not be emitted in a
  default deployment, which would leave the correlation ID in the client's response pointing at
  nothing — the defect this event exists to fix.
- **Bound as parameters, never interpolated.** Both summaries derive from a JSON document and can
  therefore contain braces. Interpolating them into the template would have
  `Microsoft.Extensions.Logging` read those braces as property holes: the event would gain holes
  named after fragments of upstream content and lose its named properties entirely.

### Allowlisted fields by value; every other field by name only

The top-level members of the upstream body are split in two. The RFC 6749 §5.2 members are
eligible to contribute their values. Every other member contributes **its name and nothing else**.

This is the settlement of facts 1–3. Restricting the log to an allowlist alone would reinstate
fact 3, because the member carrying the answer is by definition the one nobody knew to allowlist.
Logging the body instead is closed off by `docs/LOGGING.md`. Sanitizing and bounding the body —
which DMS does — answer log injection and log volume; **they redact nothing.**

So the operator learns that the upstream also sent a `reason`, and takes that name to the identity
provider's own logs.

**The residual loss is real and deliberate.** The log shows that `reason` was present. It never
shows that it read `"client disabled"`. That is the price of the no-payload policy, paid
knowingly. Do not "restore" full-body logging to close the gap.

### Allowlist membership is eligibility, not safety

A standard field *name* does not establish that its contents are safe. RFC 6749 §5.2 defines every
error member as a string, but nothing obliges an upstream to comply, and `JsonNode.ToString()` on
a member that arrived as an object or an array serializes the whole subtree. An `error_uri` of
`{"client_secret":"…"}` would otherwise be written into the event, and an `error` of the same shape
echoed to an unauthenticated caller.

What a member contributes is therefore decided **per value**, in `StandardFieldValueForLogging`:

| Upstream value                 | Log           | Client                        |
| ------------------------------ | ------------- | ----------------------------- |
| JSON string, number or boolean | the value     | forwarded per the table above |
| JSON object or array           | `(malformed)` | fixed title / detail          |
| JSON `null`                    | `null`        | fixed title / detail          |
| any shape, for `error_uri`     | `(withheld)`  | not applicable                |

`null` is kept distinct from `(malformed)` so an operator can tell an upstream that sent nothing
from one that sent something unloggable.

A malformed member falls back to the same fixed default an absent one uses. It is therefore not a
"usable `error_description`", which means it does not suppress the log event — the event fires and
reports the field as malformed.

### `error_uri` is withheld entirely, whatever its shape

RFC 6749 §5.2 defines `error_uri` as a URI. A URI carries credentials often enough that no part of
one is logged: a client secret in the query string is the obvious case, credentials in the userinfo
component the classic one, and a one-time token in the path no better. Nothing available at that
point can distinguish a documentation link from any of those, so presence is the whole diagnostic.

Omit rather than redact, deliberately. Redacting components means more code, more parsing of
untrusted input, and a weaker guarantee than withholding the value outright.

### Both bounds are visible when applied

- **`MaxLoggedUpstreamFieldNames` = 20.** The *count* of members is attacker-influenceable
  independently of their length: ten thousand one-character names fit inside the character bound
  and would still produce an unreadable line. Exceeding the cap appends `...[N more]` rather than
  being swallowed silently, so a body with twenty members is distinguishable from one with twenty
  thousand.
- **`MaxLoggedUpstreamContentLength` = 2048 characters.** Applied per summary rather than per
  event, so an oversized value in one group cannot consume the other's share. Truncation appends
  `...[truncated]`, so a short body is distinguishable from a cut one.

Both numbers are roughly an order of magnitude above observed practice, which is the sizing
argument: a genuine diagnostic arrives intact, a padded one is cut well before it can dominate a
log line.

### Sanitize first, truncate second

The order is observable, not stylistic. Under truncate-then-sanitize, a body padded with control
characters would spend the whole character budget on characters the sanitizer then removes, so the
diagnostic text following the padding would never reach the log even though the logged value came
in far under the cap.

Member **names** are sanitized and bounded along with values: a name is as attacker-influenceable
as a value, and an upstream is free to return one carrying newlines or a megabyte of padding.

Filtering uses `LoggingSanitizer.SanitizeFreeTextForLogging` — the broad allowlist, not the strict
`Method`/`Path` one, which would strip the quotes, braces, commas and equals signs a JSON or
form-encoded error payload is made of. Truncation backs off a split surrogate pair so the cut
cannot manufacture an unpaired half. The allowlist itself is specified in
[adr-correlation-id-normalization.md](./adr-correlation-id-normalization.md).

### The correlation ID is the only bridge between the two destinations

The client is given a generic detail and a `correlationId`. That ID is the supported — and only —
route from the response to the explanation, which is what FR-LOG-6 guarantees. Two consequences
follow directly:

1. The event must carry `TraceId`, or the entry exists and is unreachable.
2. The event must fire **even when there is nothing to summarize**, including when the body was
   the JSON literal `null`. An ID that leads nowhere is worse than no ID, because the client was
   told to quote it.

## Scope boundaries

- **DMS only.** CMS (`src/config`) has its own token handling and is not covered here.
- **The token proxy only.** This is not a general rule for every outbound call DMS makes. The
  sanitizer is shared; the selection policy is not.
- **Not the `200` passthrough.** A successful upstream response is returned unaltered, by design.
- **Client-facing documentation** belongs in the separate Ed-Fi documentation repository, not
  this one.

## Known deviations and open items

Both are pre-existing on `main` and were deliberately left outside the scope of the branch that
implemented this ADR.

- **[DMS-1549](https://edfi.atlassian.net/browse/DMS-1549)** — *Token proxy turns an upstream 401
  with a non-JSON-object body into a 502.* `JsonNode.Parse` and `AsObject` run unguarded inside
  the enclosing `try`, so an empty body, an HTML error page, a JSON array or a JSON scalar throws
  and is converted to a `502` logged at `Error`. **This ADR's log event sits after the parse and
  therefore never fires on that path** — the case that most needs the diagnostic is the one case
  with no record at all. Whoever fixes the status code must confirm the event still lands.
- **[DMS-1550](https://edfi.atlassian.net/browse/DMS-1550)** — *Token proxy logs the entire
  upstream body on the 502 path, and the logging policy does not say whether that is allowed.*
  The `502` branch logs the whole sanitized, bounded body at **Warning**. The `docs/LOGGING.md`
  prohibition is scoped to Information, so this is not literally a policy violation — the policy
  is silent. That silence is the substance of the ticket. The `502` branch was left untouched
  rather than aligned by assumption.

## Defects found and their lessons

Recorded so the *class* of mistake is documented, not just the line that was fixed. Each of the
first three was introduced by the fix for the one before it.

1. **Forwarding an arbitrary upstream body to an unauthenticated caller.** The original `401`
   fallback assigned the raw body to `errorDescription` whenever the body parsed as a JSON object
   but happened not to carry an `error_description`. **Lesson:** on an unauthenticated endpoint,
   "pass through what the upstream said" is a disclosure decision, never a convenience.
2. **Fixing disclosure by discarding.** Replacing the body with a fixed detail removed the
   disclosure and removed the diagnostic with it, leaving a `correlationId` that led to no
   explanation. **Lesson:** withholding content from a client is not the same as destroying it;
   ask where it goes instead before deleting the only copy.
3. **Fixing that by logging the whole body.** The restored diagnostic copied the sanitized,
   bounded body into ordinary production logs. Sanitizing answers log injection, bounding answers
   log volume, and **neither redacts anything.** **Lesson:** never treat a sanitizer as a
   redactor. They solve unrelated problems.
4. **Allowlisting by name while assuming the value's shape.** The field selection gated on the
   member *name* and then called `ToString()` on whatever the value was, so a nested object under
   an allowlisted name walked straight through the allowlist. **Lesson:** an allowlist over names
   constrains names. If the value's shape matters, check the value's shape.
5. **Tests that encode the defect they were written after.** Tests added alongside fix 3 asserted
   that non-standard field *values* appeared in the log — pinning the disclosure in place as
   though it were the requirement. **Lesson:** when a test asserts that data reaches a sink, state
   which policy entitles it to be there; if no policy does, the assertion is the bug.
6. **Doc comments describing the behavior a change replaced.** Twice, a fix landed while the
   comment above it went on describing the previous behavior — once claiming an allowlist entry was
   unreachable when the same change had just made it reachable. **Lesson:** the comment on the
   lines you changed is part of the diff, not context around it.

## References

- [docs/LOGGING.md](../docs/LOGGING.md) — DMS logging policy; the Information-level prohibition
- [adr-correlation-id-normalization.md](./adr-correlation-id-normalization.md) — the shared
  sanitizer allowlist, and the FR-LOG-3..6 parity guarantee this ADR depends on
- RFC 6749 §5.2 — OAuth 2.0 error response (`error`, `error_description`, `error_uri`)
- `src/dms/core/EdFi.DataManagementService.Core/OAuth/OAuthManager.cs` — implementation
- `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/OAuth/OAuthManagerTests.cs` — coverage
