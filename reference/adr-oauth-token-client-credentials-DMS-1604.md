---
jira: DMS-1604
---

# ADR: Accept client credentials in the `/oauth/token` request body

**Status:** Accepted and implemented in DMS (`src/dms`). Does not apply to CMS (`src/config`). \
**Date:** 2026-10-13 \
**Author:** Stephen Fuqua, with drafting assistance from Claude Code.

## Executive summary

DMS `POST /oauth/token` used to accept client credentials only in an `Authorization: Basic`
header. A request that carried `client_id` and `client_secret` in the body, form-encoded or as
JSON, got `400 Malformed Authorization header`. The Ed-Fi ODS/API accepts both, and Ed-Fi
documentation has many JSON examples, so a client that works against the ODS/API failed against
DMS.

DMS now accepts the credentials in either place, but not in both at once. Whichever way the caller
sends them, DMS forwards them to the upstream identity service in a Basic header that it builds
itself. The upstream contract is unchanged. A request with missing credentials gets the same
`401` and error code that the ODS/API returns. So does a request whose `Authorization` header has
another scheme in the ODS/API's two-part form, such as `Bearer <token>`. See
[Known limitations](#known-limitations) for the non-Basic headers where the two differ.

## Context

### RFC 6749

[RFC 6749 §2.3.1](https://www.rfc-editor.org/rfc/rfc6749#section-2.3.1) requires an authorization
server to support HTTP Basic client authentication. It also allows (`MAY`) `client_id` and
`client_secret` in an `application/x-www-form-urlencoded` body, though it says this is
"NOT RECOMMENDED". The RFC does not define a JSON request body. DMS accepts JSON for parity with the
ODS/API only.

[RFC 6749 §2.3](https://www.rfc-editor.org/rfc/rfc6749#section-2.3) says a client "MUST NOT use
more than one authentication method in each request".

[RFC 6749 §3.2](https://www.rfc-editor.org/rfc/rfc6749#section-3.2) says, of the token endpoint,
"Parameters sent without a value MUST be treated as if they were omitted from the request." It
also says request parameters "MUST NOT be included more than once".

### ODS/API behavior

The links below pin ODS/API commit
[`e453cd2`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/e453cd2cad8a0653c65453948d3d235aec7c517c).
The behavior comes from reading that source, not from live testing.

- The form action delegates to the JSON action, so both content types follow the same rules
  ([L130-L137](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/TokenController.cs#L130-L137)).
- A Basic header combined with a non-blank body `client_id` or `client_secret` gets
  `400 invalid_client`. The test is `string.IsNullOrWhiteSpace`, so an empty or whitespace-only
  body field beside the header is ignored
  ([L93-L96](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/TokenController.cs#L93-L96)).
- A blank or missing `client_id` or `client_secret`, with no Basic header, gets
  `401 invalid_request`, whether one field is missing or both
  ([L104-L107](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/TokenController.cs#L104-L107)).
- A non-empty `Authorization` header is first split on spaces. Unless it has exactly two parts, it
  gets `400 invalid_request`
  ([L56-L62](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/TokenController.cs#L56-L62)).
  A two-part header with any scheme other than Basic gets `401 invalid_client`, whatever the body
  holds
  ([L64-L68](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/TokenController.cs#L64-L68)).
- The request model has the properties `Client_id`, `Client_secret`, `Grant_type`, and `Scope`
  ([TokenRequest.cs](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Models/Tokens/TokenRequest.cs#L10-L16)).
  JSON goes through Newtonsoft
  ([L197](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Startup/OdsStartupBase.cs#L197)),
  and both Newtonsoft and MVC form binding match property names case-insensitively. So the wire
  names are `client_id`, `client_secret`, and `grant_type`, in any letter case.
- The controller runs all of the checks above before the token provider looks at `grant_type`.
  The provider rejects an unsupported grant type before it verifies the secret
  ([ClientCredentialsTokenRequestProvider.cs L37-L39](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Providers/ClientCredentialsTokenRequestProvider.cs#L37-L39)).
  Its comparison is null-safe
  ([StringExtensions.cs L267-L271](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Common/Extensions/StringExtensions.cs#L267-L271)),
  so a `null` grant type gets `400 unsupported_grant_type`.

### The upstream contract

DMS doesn't issue tokens. It forwards each token request to the identity service configured in
`AppSettings:AuthenticationService`, either the Configuration Service's `/connect/token` or Keycloak.
Both accept a Basic header:

- The Configuration Service (self-contained mode) splits the decoded Basic value at the first colon
  and applies `Uri.UnescapeDataString` to each half.
- Keycloak 26.1 form-decodes each half with `URLDecoder`
  ([`BasicAuthHelper.RFC6749.parseHeader`](https://github.com/keycloak/keycloak/blob/26.1.0/core/src/main/java/org/keycloak/util/BasicAuthHelper.java)).

The Configuration Service reads a Basic header only in self-contained mode; it reads form
credentials in every mode. (Since DMS-1598, in 8.1, it reads a Basic header in Keycloak mode as
well.) In Keycloak mode, DMS calls Keycloak directly rather than the
Configuration Service, and Keycloak accepts Basic. So Basic works with every upstream DMS forwards to.

## Decision

### Supported request types

DMS supports three request types. Each one issues a token that authorizes resource requests.

**Form body with an `Authorization: Basic` header.** This is the request type DMS has always
supported, and it is unchanged.

```http
POST /oauth/token HTTP/1.1
Authorization: Basic {base64(client_id ":" client_secret)}
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
```

**Form body only.**

```http
POST /oauth/token HTTP/1.1
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials&client_id={client_id}&client_secret={client_secret}
```

**JSON body only.**

```http
POST /oauth/token HTTP/1.1
Content-Type: application/json

{
  "grant_type": "client_credentials",
  "client_id": "{client_id}",
  "client_secret": "{client_secret}"
}
```

Field names match case-insensitively in both the form and the JSON body, as they do in the ODS/API.

### Rejections

DMS evaluates the rules below in the order shown, and the first one that matches answers. This is
the ODS/API's order. Duplicate parameters and the length cap are DMS rules, and the ODS/API has
neither.

The two `401` rows use the same problem-details shape that `/oauth/token` already returns when the
upstream rejects the credentials: `type` `urn:ed-fi:api:unauthorized`, with the ODS/API's
RFC 6749 §5.2 error code as the `title`. Every `400` row uses `type` `urn:ed-fi:api:bad-request`
and `title` `Bad Request`. This decision doesn't switch the endpoint to the RFC 6749 §5.2 JSON
error format. No response echoes a credential value.

| Order | Request | Response | `title` | `detail` |
| --- | --- | --- | --- | --- |
| 1 | A form body with `grant_type`, `client_id`, or `client_secret` more than once | `400` | `Bad Request` | `A request parameter was included more than once` |
| 2 | An `Authorization` header that is present but empty, whatever the body holds | `400` | `Bad Request` | `The Authorization header must not be empty` |
| 3 | An `Authorization` header that isn't Basic, whatever the body holds | `401` | `invalid_client` | `The Authorization header must use the Basic scheme` |
| 4 | A Basic header and a non-blank body `client_id` or `client_secret`, form or JSON | `400` | `Bad Request` | `Only one client authentication mechanism may be used` |
| 5 | No `Authorization` header, and a body `client_id` or `client_secret` that is absent, `null`, empty, or whitespace | `401` | `invalid_request` | `Client credentials must be supplied in an Authorization: Basic header or as client_id and client_secret in the request body` |
| 6 | No `Authorization` header, and a body `client_id` or `client_secret` longer than 2048 characters | `400` | `Bad Request` | `client_id and client_secret must each be at most 2048 characters` |
| 7 | A `grant_type` other than `client_credentials`, including a JSON `null` | `400` | `Bad Request` | `Unsupported grant type` |

"Basic header" here means what DMS has always accepted as one: an `Authorization` header that
contains `basic` anywhere, in any letter case. That test is unchanged, so no caller that
authenticates with a Basic header sees a difference. See
[Known limitations](#known-limitations) for what it means for other schemes.

Rule 2 matches the ODS/API, which takes a present `Authorization` header's value even when it is
empty, fails to split it into a scheme and a parameter, and answers `400 invalid_request` before it
looks at the body
([L52-L62](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/TokenController.cs#L52-L62)). A
whitespace-only value arrives trimmed to empty and gets the same answer. Body credentials don't
rescue the request. Only a request with no `Authorization` header at all reaches rules 5 and 6.

Rule 3 matches the ODS/API for a two-part header with another scheme, such as `Bearer <token>`:
it isn't client authentication, and body credentials don't rescue the request. The ODS/API answers
other non-Basic headers with `400`; see [Known limitations](#known-limitations).

Rule 4 counts a body field only when it has a non-blank value. One such field is enough, so a
partial combination such as `client_id=abc&client_secret=` with a Basic header is still mixed. An
empty field is treated as omitted, as
[RFC 6749 §3.2](https://www.rfc-editor.org/rfc/rfc6749#section-3.2) requires, so a Basic header
with `client_id=&client_secret=` is a plain Basic request and is forwarded as one. The ODS/API
accepts it too. A whitespace-only value such as `client_id=%20` is a value under the RFC's
wording, but the ODS/API counts it as blank, so DMS doesn't count it either. Parity with the
ODS/API wins here. A repeated field, such as `client_id=&client_id=abc`, is still rule 1's `400`,
whatever its values. The Configuration Service's `/connect/revoke` still counts an empty form
field as a second mechanism. This decision doesn't change CMS.

A JSON `null` or `""` counts as absent, the same as a missing field. So with a Basic header, such
a field isn't a second mechanism under rule 4, and with no header it's a missing credential under
rule 5. The ODS/API treats both as blank, so the result is the same there. DMS applies the
empty-means-omitted rule in Core, in `TokenRequestCredentials`, so the form and JSON paths can't
drift apart.

Rule 5 covers a request with no credentials at all as well as a partial pair. A whitespace-only
value counts as missing, as it does in the ODS/API.

Rule 6 caps each body `client_id` and `client_secret` at 2048 characters, counted in UTF-16 code
units. A value over the cap is rejected before anything is sent upstream, and the response never
echoes it. A credential in a header was always bounded by the server's request-header limit, but a
body value is bounded only by the request-body limit, which defaults to megabytes. Without the cap,
an oversized value would become an upstream header that the identity service refuses, and the
caller's input error would surface as a `502` and an upstream-failure log entry. The Configuration
Service accepts secrets of at most 1024 characters, so 2048 leaves real headroom. The cap bounds the
work and the header size, but it doesn't guarantee that the upstream accepts the header. A pair
made entirely of non-ASCII characters at the cap still encodes to about 50 KB.

Rule 1 comes from [RFC 6749 §3.2](https://www.rfc-editor.org/rfc/rfc6749#section-3.2), and
`/connect/revoke` applies it too. It covers form bodies only, and it compares names
case-insensitively, as form field lookup does. A JSON body with a repeated property keeps the last
value, as `System.Text.Json` does by default.

Rule 7 used to answer a JSON `null` grant type with a `500`, because the binding model declared
`grant_type` non-nullable and the `null` reached a method call. The model now declares it nullable,
the endpoint passes an empty string in its place, and the comparison in `OAuthManager` is null-safe.

### Normalize to Basic at the proxy

DMS always sends the upstream a Basic header and a form body with only `grant_type`. It never
forwards body credentials as form fields.

- With a Basic header from the caller, DMS forwards the header verbatim, as it always has.
- With body credentials, DMS builds the header as RFC 6749 §2.3.1 defines it:
  `base64(UTF8(encode(client_id) ":" encode(client_secret)))`.

The encoding is `Uri.EscapeDataString`, which percent-encodes everything except the RFC 3986
unreserved characters. It's not form encoding, which writes a space as `+`. The Configuration
Service's `Uri.UnescapeDataString` leaves `+` as a literal plus, so a form-encoded space would
reach it as `+`. Percent escapes such as `%20` and `%2B` decode correctly under the Configuration
Service and under Keycloak's `URLDecoder`. Encoding also keeps a colon in `client_id` from moving
the split. A unit test sends a secret with a space, `+`, `:`, `%`, `/`, and non-ASCII characters,
and decodes it both ways.

### Secrets stay out of logs and responses

No log line or response contains `client_secret`, the header DMS built, or the request body. The
record that carries body credentials into Core renders neither value from `ToString`. The upstream
response is still handled as
[adr-oauth-upstream-error-disclosure.md](adr-oauth-upstream-error-disclosure.md) describes, so a
wrong secret in the body gets the same `401` as a wrong secret in a Basic header.

## Consequences

- A client written against the ODS/API, or against the Ed-Fi documentation's JSON examples, can
  get a token from DMS without changes.
- A JSON body with only `grant_type` and a Basic header still works. It's not one of the three
  documented request types, but rejecting it would break current clients.
- A client that sent a Basic header and also put a non-blank `client_id` or `client_secret` in the
  body used to succeed, because DMS ignored the body fields. It now gets `400`. The ODS/API rejects
  that request too, so a client migrating from the ODS/API already complies. Empty or
  whitespace-only body fields beside the header are still accepted, as in the ODS/API. An empty
  one is treated as omitted, per
  [RFC 6749 §3.2](https://www.rfc-editor.org/rfc/rfc6749#section-3.2).
- A request with no credentials, or with a non-Basic `Authorization` header, used to get
  `400 Malformed Authorization header`. It now gets `401`, with `invalid_request` or
  `invalid_client` as the `title`. That matches the ODS/API except for the non-Basic headers listed
  under [Known limitations](#known-limitations). A client that branches on that `400` must handle
  the `401` instead.
- An empty or whitespace-only `Authorization` header still gets `400`, but its `detail` is now
  `The Authorization header must not be empty` instead of `Malformed Authorization header`.
- A JSON body with `"grant_type": null` used to get a `500`. It now gets
  `400 Unsupported grant type`.
- A header that contains `basic` but isn't a valid `Authorization` value, such as
  `Basicxyz:secret` or two `Authorization` headers joined as `Basic X,Basic Y`, used to get a
  `500`, and the error log quoted the whole header. It now gets
  `400 Malformed Authorization header`, and the header isn't logged.
- `scope` handling hasn't changed.
- Callers that use a Basic header see no change in what reaches the upstream.

## Known limitations

DMS still recognizes a Basic header by the substring `basic`, not by its scheme token. A header
with another scheme that happens to contain `basic`, such as `Digest username="basic-user"` or a
`Bearer` token whose encoded payload contains those letters, counts as a Basic header. Such a
header is forwarded upstream as before, or rejected as mixed when the body also has credentials,
rather than getting rule 3's `401 invalid_client`. The ODS/API would answer `401`. The test stays
as it is so that no existing Basic-header caller sees a change.

DMS doesn't copy the ODS/API's two-part check on the `Authorization` header. The ODS/API splits a
non-empty header on spaces and answers `400 invalid_request` unless there are exactly two parts,
before it looks at the scheme
([L56-L62](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/TokenController.cs#L56-L62)).
DMS answers `401 invalid_client` instead for any such header without `basic` in it: a bare
`Bearer`, a header with three or more parts such as `Bearer a b`, and any real `Digest` header,
such as `Digest username="a", realm="b", nonce="c"`, which always has several parts. A request that
fails on the ODS/API fails on DMS too, with a different status. DMS follows the ticket's rule that
a non-Basic header gets `401 invalid_client`.

DMS parses the JSON body itself instead of leaving it to the framework's request binding. A body
that cannot be parsed or has the wrong JSON type, such as a number or an object in `client_id`,
`client_secret`, or `grant_type`, or that is empty or the literal `null`, gets an empty `400` in
every environment. DMS never logs the parse error, because System.Text.Json quotes part of the
body in its message, which can include the secret. Left to the framework, that text reached the
`Debug` log in production and the `Error` log, with a `500`, in `Development`. What the `400`
should carry, and whether it should match the ODS/API, is still open.

## Tests

- `OAuthManagerTests` (Core) covers each rule, including an empty or whitespace-only header, a
  `Bearer` header with full, partial, or no body credentials, a two-part `Digest` header with full
  body credentials, a Basic header with empty or whitespace-only body fields (forwarded) and with
  one non-blank field beside an empty or blank one (mixed), empty and whitespace-only fields with
  no header, a `null` grant type,
  and the length cap at 2048 and 2049 characters for each field. A fixture for each adjacent pair
  of rules 3 to 7 makes a reordering of those checks fail. It also covers the
  upstream header and form body, the special-character round trip, and the absence of secrets
  from the log.
- `TokenRequestCredentialsTests` (Core) covers the empty-means-omitted normalization and which
  values count as a second mechanism.
- `TokenEndpointModuleTests` (frontend) covers form and JSON extraction, empty form and JSON fields
  reaching the manager as absent and getting the same answer through the real `OAuthManager`,
  case-insensitive names in both form and JSON, duplicate form parameters (including ones that
  differ only in letter case, and one with a `Bearer` header, which pins rule 1 before rule 3), the
  header-only request types, an empty header reaching the manager as distinct from no header, and
  JSON `null` credential and grant type fields through the real `OAuthManager`. It also covers
  malformed, mistyped, empty, and `null` JSON bodies in `Production` and `Development`: an empty
  `400`, no manager call, and no body content in any log entry at `Trace` level.
- `Features/Security/TokenEndpoint.feature` (E2E) runs the three request types above, the JSON
  with Basic request type, and a Basic header with empty body credential fields. It also runs the
  no-credentials, partial-credentials, `Bearer`, mixed-mechanism, and `null` grant type
  rejections, and the wrong-secret `401`, against a running stack.
