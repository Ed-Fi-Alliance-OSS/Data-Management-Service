---
jira:
epic: DMS-1418
source_spike: DMS-1419
---

# Story: Add Credential Exclusion for Request/Response Capture

## TL;DR

Builds the safeguard that keeps passwords, secrets, and access tokens out of captured API traffic.
If a captured request shows any sign of carrying a credential, its content is withheld and only a record that it happened is kept.

## Description

Every captured row passes through one component before it is stored.
That component defines what a row is, decides whether the row's client content may be stored, and caps what is stored.
It is pure: it does no I/O, so it is proven by a generative test rather than by examples alone.

This story builds that component and its proof.
Nothing calls it until the capture-pipeline story lands.

Per `reference/design/request-response-capture-DMS-1419/design.md` ("## D4: Credential Exclusion"), and "### Semantics" under D6 for the caps.

**Citation convention.**
Unprefixed paths are relative to the repository root.

## Acceptance Criteria

**The capture-row contract**

- One type in DMS core defines every column of a capture row and its class, *server-generated* or *client-derived*, starting from the outline in design.md ("### Column Classes").
- A test fails when a column has no class.
- The method is the `RequestMethod` value, and `UNSUPPORTED` for any other verb. The raw verb text is never a field of the row.
- The path is the full request path as received, including the path base and the tenant and route-qualifier segments, not core's `/{dmsPath}`. The component takes it as an input; the capture-pipeline story supplies it.
- The tenant is the grant's canonical tenant name from the snapshot, never the route spelling, and the profile is the resolved profile name.
- The trace ID holds the correlation ID only when DMS generated it. A client-supplied correlation ID goes only to the client-derived correlation ID column. The component takes the source as an input; the capture-pipeline story supplies it.

**The withholding rule**

- When any D4 trigger holds, every client-derived field is empty, `Withheld` is set, and the first trigger found is the reason. Every server-generated field is unchanged.
- When no trigger holds, every client-derived field is stored as given, subject only to the cap.
- Trigger 1: a listed name is a query parameter name, or a JSON property name at any depth of the request or response body.
- Trigger 2: the request's bearer token appears in a client-derived field as stored, JSON-unescaped, or percent-decoded. The decoders never throw, and leave an invalid sequence as it is.
- Trigger 3: an unparseable request body contains a listed name, contains a NUL byte, or is not valid UTF-8; the body state is `Unread` or `NotRetained`; or the examination stopped at a depth or size bound.
- A retained body is examined the same way on every route, including a 405 route's body, which DMS reads only for the copy: as JSON when it parses, and as text when it does not.
- Names are compared after unescaping or percent-decoding, with ordinal case folding and `_` and `-` removed, as whole names.
- The credential-name list is the one in design.md ("### The Withholding Rule").

**Exception detail**

- The exception description comes from a fixed map keyed by exception type. An unmapped type gets a generic description.
- No exception message, inner exception, or stack trace is ever a field of the row.

**Caps**

- After the rule, each stored body longer than the cap the component is given is truncated and its flag set. The cap is the `MaxStoredBodyBytes` setting, which the store story binds.
- The rule always sees the full content, so a triggered body is withheld, not truncated.

**The generative proof**

- A CsCheck 5.0.1 test in `src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/` follows `docs/property-based-testing/`.
- Its generator produces request and response JSON, malformed bodies, UTF-16 bodies, gzip-, deflate-, and brotli-encoded bodies, Latin-1 bodies, every body state, and query strings with repeated and mixed-case names. It plants credential names and the token in every client-derived field, in random decoded forms.
- An independent oracle implements D4 exactly, and the properties hold:
    - a trigger means every client-derived field is withheld and every server-generated field is present
    - no trigger means every field is stored as given, capped
- A collision test fails when a normalized listed name equals a property or query field name in any ApiSchema package the repository references.

**Named regressions**

Each is an example test:

- a copied secret before `client_secret`
- a numeric credential value
- `%61lpha%FF`, and a secret-free `{"note":"%FF"}` that is stored
- a malformed body naming a credential
- a UTF-16 body and a gzip body, each naming a credential, that are withheld
- a short brotli body naming a credential that contains no NUL byte: withheld, because it is not valid UTF-8
- a malformed Latin-1 body naming no credential: withheld, because it is not valid UTF-8
- a GET or DELETE carrying a body
- a retained PATCH body naming no credential, sent to a data route: stored, with its path and the 405 response
- a `NotRetained` body with `?note=alpha` in the query: withheld, and `alpha` appears in no field
- repeated and mixed-case credential names in the query
- a look-alike name, such as `customizationKey`, that is stored
- an over-cap triggered body that is withheld, not truncated
- a bearer token sent as the HTTP method of a data route: the method is `UNSUPPORTED`, and the token appears in no field
- a bearer token no longer than `CorrelationIdMaxLength`, sent as a client-supplied correlation ID: the trace ID is empty, and the correlation ID is withheld with the other client-derived fields

## Dependencies

None.

## Estimate

2 SP.
