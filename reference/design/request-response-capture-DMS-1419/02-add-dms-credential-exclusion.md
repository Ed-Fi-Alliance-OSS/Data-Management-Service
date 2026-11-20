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
- The tenant is the validated tenant, and the profile is the resolved profile name.

**The withholding rule**

- When any D4 trigger holds, every client-derived field is empty, `Withheld` is set, and the first trigger found is the reason. Every server-generated field is unchanged.
- When no trigger holds, every client-derived field is stored as given, subject only to the cap.
- Trigger 1: a listed name is a query parameter name, or a JSON property name at any depth of the request or response body.
- Trigger 2: the request's bearer token appears in a client-derived field as stored, JSON-unescaped, or percent-decoded. The decoders never throw, and leave an invalid sequence as it is.
- Trigger 3: an unparseable request body contains a listed name; the body state is `Unread`; or the examination stopped at a depth or size bound.
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
- Its generator produces request and response JSON, malformed bodies, and query strings with repeated and mixed-case names. It plants credential names and the token in every client-derived field, in random decoded forms.
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
- a GET or DELETE carrying a body
- repeated and mixed-case credential names in the query
- a look-alike name, such as `customizationKey`, that is stored
- an over-cap triggered body that is withheld, not truncated
- a bearer token sent as the HTTP method of a data route: the method is `UNSUPPORTED`, and the token appears in no field

## Dependencies

None.

## Estimate

2 SP.
