---
jira: DMS-1589
---

# ADR: Ignore unconsumed query parameters and report them in `X-EdFi-Warning`

**Status:** Proposed. Applies to DMS (`src/dms`) only. \
**Date:** 2026-10-06 \
**Author:** Adam Hopkins, with drafting assistance from Claude Code and Codex.

## Executive summary

DMS answers `400` to a query parameter that an operation does not use. The Ed-Fi ODS/API ignores
such parameters. A client migrating from the ODS/API breaks on that difference even when the extra
parameter is harmless.

DMS now ignores them too, as the [operation matrix](#operation-matrix) specifies. A query parameter
that the operation does not use has no effect on the request or its result. Unlike the ODS/API, DMS
reports the ignored parameter names, up to a fixed limit, in an `X-EdFi-Warning` response header. A malformed value for a known control, such as `limit=abc`, still returns `400`.

## Context

### ODS/API behavior

The links below pin ODS/API commit
[`e453cd2`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/e453cd2cad8a0653c65453948d3d235aec7c517c).
The behavior comes from reading that source, not from live testing. In the request paths
inspected, each action binds a fixed set of query parameters. A name that no binder and no
`additionalParameters` consumer matches has no effect. No inspected code rejects a query parameter
because of its name.

- **GET many** (resources and descriptors) binds these parameters
  ([L155-L160](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/DataManagementControllerBase.cs#L155-L160)):
  - the common controls
    ([`UrlQueryParametersRequest`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Common/Models/Queries/UrlQueryParametersRequest.cs#L8-L25)):
    `offset`, `limit`, `totalCount`, `minChangeVersion`, `maxChangeVersion`, `pageToken`,
    `pageSize` and `q`;
  - the resource filters;
  - an `additionalParameters` dictionary. The dictionary has two consumers: `useJoinAuth` and
    identification-code criteria
    ([L46-L85](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Common/Providers/Queries/Criteria/IdentificationCodeAggregateRootQueryCriteriaApplicator.cs#L46-L85)).
- **Partitions** binds `number`, the common controls and `additionalParameters`. It uses
  `allowSmallPartitions` and `useJoinAuth`. It binds resource filters dynamically, but discards
  the binding result
  ([L82-L86, L178-L180](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/Partitions/Controllers/PartitionsController.cs#L82-L180)).
  Its result uses neither `limit`, `offset`, `totalCount` nor `pageSize`. It does parse
  `pageToken`
  ([L107](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/Partitions/Controllers/PartitionsController.cs#L107)).
- **Deletes** and **key changes** bind only the common controls
  ([Deletes L62-L63](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Features/ChangeQueries/Controllers/DeletesController.cs#L62-L63),
  [KeyChanges L62-L66](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Features/ChangeQueries/Controllers/KeyChangesController.cs#L62-L66)).
  They use `limit`, `offset`, `totalCount` and the change versions, and they parse `pageToken`
  ([Deletes L80-L95](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Features/ChangeQueries/Controllers/DeletesController.cs#L80-L95)).
  Resource filters are not bound.
- **GET by id**, **PUT**, **POST** and **DELETE** bind only the route, the body and the headers
  ([Get L218-L222](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/DataManagementControllerBase.cs#L218-L222),
  [Put L274-L281](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/DataManagementControllerBase.cs#L274-L281),
  [Post L352-L359](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/DataManagementControllerBase.cs#L352-L359),
  [Delete L432-L436](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Controllers/DataManagementControllerBase.cs#L432-L436)).

A malformed value for a bound parameter fails model binding, and the ODS/API answers `400`
([L31-L47](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/e453cd2cad8a0653c65453948d3d235aec7c517c/Application/EdFi.Ods.Api/Startup/ApiBehaviorOptionsConfigurator.cs#L31-L47)).
That applies even to a parameter the operation then does not use. The exception is resource
filters on Partitions: their binding result is discarded, so a malformed filter value there is
ignored.

### DMS behavior before this decision

On GET many, Partitions, Deletes and KeyChanges, DMS returns `400` in three cases:

- the first name that matches no query field of the resource;
- a paging control on Partitions;
- a cursor control or resource filter on Deletes and KeyChanges.

GET by id and the write operations never read the query string.

### Risk of silent leniency

Ignoring a parameter also ignores what the client meant by it. Take a mistyped filter such as
`?studentUniqueld=123`, with a lowercase `l` in place of the `I`. The filter is dropped, and the
request returns the whole collection. A resource filter sent to Deletes has the same effect. An
unattended sync or cleanup job then acts on the wrong result set. The ODS/API accepts this risk
silently. DMS reduces it by naming what it ignored.

## Decision

### Operation matrix

A parameter is **consumed** when the operation uses it. A parameter is **ignored** when the
operation does not use it. An ignored parameter has no effect on the operation, and it is listed
in the warning header.

| Operation | Consumed | Ignored, and how its value is validated |
| --- | --- | --- |
| GET many (resources and descriptors) | `limit`, `offset`, `totalCount`, `pageToken`, `pageSize`, change versions, resource filters | Every other name. Its value is not validated. |
| Partitions | `number`, change versions, resource filters | `limit`, `offset`, `totalCount`, `pageToken`, `pageSize`: the value must be well formed (see below). Every other name: the value is not validated. |
| Deletes and KeyChanges | `limit`, `offset`, `totalCount`, change versions | `pageToken`, `pageSize`: the value must be well formed. Resource filters and every other name: the value is not validated. |
| GET by id, POST, PUT, DELETE | None | Unchanged. The query string is never read, and no warning header is sent. |

Validation of consumed parameters does not change. The same values return the same `400` with the
same body as before.

**Well-formed** means the value passes the check DMS applies to that control where it is consumed:

- `limit` must be an integer from 0 to the maximum page size;
- `offset` must be an integer of 0 or more;
- `totalCount` must be a boolean;
- `pageSize` must be within the cursor page-size range;
- `pageToken` must be a token DMS can decode.

Rules that combine controls, such as `offset` with `pageToken`, apply only where the controls are
consumed. Ignoring a control never turns on its behavior. Change queries do not page by cursor,
and partition-boundary responses are not paginated.

Names are matched as they are today. Paging controls are matched exactly after the frontend
canonicalizes their case. Change versions and resource filters are matched without regard to case.

The rules above have two client-visible consequences:

- All ignored names are collected. Evaluation no longer stops at the first unknown name, so a
  request that combines an unknown name with a malformed consumed value now gets the `400` for
  that value, together with the warning.
- On Deletes and KeyChanges, a resource-filter value is never validated, because the filter is not
  consumed. `?birthDate=notadate` there is ignored. It no longer returns `400`.

### Warning header

```http
X-EdFi-Warning: Ignored query parameters: studentUniqueld, foo%20bar
```

- The header is sent only when at least one parameter was ignored.
- The value is `Ignored query parameters: ` followed by the rendered names, separated by `, `.
  Values never appear.
- Each name is listed once, in the order of its first occurrence. ASP.NET Core merges names that
  differ only in case, keeping the position of the first and the spelling of the last.
- Each name is rendered in this order:
  1. Remove control characters, format characters, `U+2028`/`U+2029` and unpaired surrogates.
  2. If nothing remains, the name renders as `(empty)`.
  3. Percent-encode the UTF-8 bytes of every character outside the RFC 3986 unreserved set
     (`A-Z a-z 0-9 - . _ ~`).
  4. Stop at 64 encoded characters, never splitting a `%XX` triplet. If anything was cut, append
     `(truncated)`.
- At most 10 names are listed. When more were ignored, `, (and N more)` follows, where N is the
  number of names not listed.
- The whole value is at most 817 ASCII characters, well under 1024:

  | Part | Characters |
  | --- | --- |
  | Prefix | 26 |
  | 10 names, each at most 64 + 11 for `(truncated)` | 750 |
  | 9 separators | 18 |
  | Overflow marker, with N at most 10 digits | 23 |
  | **Total** | **817** |

Every character in the value is printable ASCII, which rules out header injection. Rendered names never
contain parentheses, commas or spaces, because encoding replaces them. So the markers and the
separator cannot be mistaken for part of a name. The caps are fixed constants, not configuration,
and they limit only what is reported. Every ignored parameter is still ignored, and every consumed
parameter is still applied.

### Responses that carry the header

The step that validates the query decides which parameters to ignore, and it does so before any
response exists. From then on, every response carries the header. That covers the step's own `400`
for a malformed known value. It also covers any response a later step produces, including an
authorization `403` and the handler's `200`.

Some responses come before that step and do not carry the header: authentication `401`,
tenant `404`, path `404` and `405`. An exception that escapes the pipeline also does not carry it.
The alternatives would be to parse the query string before authentication, or to add middleware
that decorates unrelated failures. Neither is worth it for a notice that the client's next request
repeats.

### Logging and browser access

- **Logging.** When any parameter is ignored, the request writes one Debug event, guarded by
  `IsEnabled`. The event carries the rendered list from the header, the total number ignored and
  `TraceId.Value`. It never carries values, raw names or the raw query string
  ([docs/LOGGING.md](../docs/LOGGING.md)).
- **Telemetry.** Ignored parameters no longer count as validation rejections.
- **Browser access.** `X-EdFi-Warning` is added to the CORS policy's exposed headers in every
  configuration.

## Accepted differences from the ODS/API

- **The warning header** exists only in DMS. It is additive: it changes nothing else about the
  response. How ignored names are handled follows the operation matrix, not full ODS/API request
  and result parity. The differences below still apply.
- **ODS-only capabilities are not implemented.**
  - `q`, `useJoinAuth` and `allowSmallPartitions` are ignored and reported. The ODS/API acts on
    them, so the same request can return a broader or different result from DMS.
  - DMS reads the correlation ID only from its configured header. A `correlationId` query
    parameter is ignored and reported.
- **Value checks differ.**
  - On Partitions, a malformed resource-filter value returns `400`. The ODS/API discards its
    binding result there.
  - An ignored control's value must pass the same range check DMS applies where the control is
    consumed. The ODS/API only checks the type. For example, `?limit=-1` on Partitions returns
    `400` in DMS and is accepted by the ODS/API.
  - A malformed value for an ODS-only parameter is not checked, because DMS does not recognize the
    parameter. For example, the ODS/API fails `useJoinAuth=abc`, and DMS ignores it.
  - DMS keeps its own messages and range rules for consumed controls.
- **Page tokens are DMS tokens.** An ODS/API `pageToken` is not one DMS can decode.

## Alternatives considered

| Alternative | Outcome | Reason |
| --- | --- | --- |
| Keep rejecting | Rejected | Safest, but it blocks ODS/API migrations over parameters that change nothing. |
| Ignore silently, like the ODS/API | Rejected | The wider-result risk stays invisible. |
| Ignore, and report in a header | **Chosen** | ODS/API-compatible handling of ignored names, per the operation matrix, plus a signal to the caller. |
| Standard `Warning` header | Rejected | RFC 9111 obsoletes it, and its grammar targets caches. |
| Configurable caps | Rejected | No deployment needs them, and fixed caps are easier to keep header-safe. |

## Consequences

- Extra and mistyped parameters no longer break clients. Only clients that read the header learn
  what was dropped. This limitation is accepted.
- Tightening again later, such as a strict mode or a deprecation period, needs its own decision.
  The header lets the team measure how often parameters are ignored before making it.
- CMS, discovery, OpenAPI and identity endpoints are unaffected.
