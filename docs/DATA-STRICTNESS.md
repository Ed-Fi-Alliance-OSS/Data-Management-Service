# Data strictness

The Ed-Fi API applies a set of strictness and laxity rules when it validates
`POST` and `PUT` request bodies. These rules follow the Ed-Fi
[Data Strictness](https://docs.ed-fi.org/reference/data-exchange/api-guidelines/design-and-implementation-guidelines/api-design-guidelines/rest-api-conventions/data-strictness/)
API guideline.

This document describes the behavior that API clients can rely on. The headline
change for integrators moving from the Ed-Fi ODS/API is that **property names in
request bodies are now case-sensitive**.

## Case-sensitive property names (breaking change)

> [!IMPORTANT]
> The Ed-Fi API enforces case-sensitive property names in `POST` and `PUT`
> request bodies. Property names must match the exact casing defined in the
> resource's OpenAPI specification (Ed-Fi resource properties are `camelCase`).
> This is a breaking change from the Ed-Fi ODS/API, which did not enforce
> property-name casing.

### How an incorrectly cased property is handled

A property whose name is not recognized — including a known property submitted
with the wrong casing, such as `GRADELEVELS` instead of `gradeLevels` — is
treated as an **unexpected property**. The API does not reject the request simply
because an unexpected property is present; instead, the unexpected property is
**dropped from the request** and not stored. The outcome then depends on whether
the correctly cased property was **required**:

- **Required property, wrong casing.** Because the wrongly cased property is
  dropped, the required property is now missing. The request fails validation
  with **HTTP 400** (a missing-required-property error).
- **Optional property, wrong casing.** The wrongly cased property is dropped and,
  if the rest of the body is valid, the request **can succeed** (`201`/`200`).
  However, the submitted value is **silently discarded** — it is not stored and
  will not appear in later `GET` responses.

> [!WARNING]
> An incorrectly cased **optional** property does not produce an error. The value
> is silently dropped, which can look like data loss. Always send property names
> exactly as defined in the resource's OpenAPI specification.

### Example

Sending `GRADELEVELS` (uppercase) instead of the required `gradeLevels` on a
`POST /ed-fi/schools`:

```json
{
  "schoolId": 255901001,
  "nameOfInstitution": "Example School",
  "educationOrganizationCategories": [
    {
      "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
    }
  ],
  "GRADELEVELS": [
    { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade" }
  ]
}
```

The uppercase `GRADELEVELS` is dropped, leaving the required `gradeLevels`
missing, so the API responds with **HTTP 400**. The response uses the standard
data-validation problem shape (the `validationErrors` content below is
representative):

```json
{
  "detail": "Data validation failed. See 'validationErrors' for details.",
  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
  "title": "Data Validation Failed",
  "status": 400,
  "correlationId": null,
  "validationErrors": {
    "$.gradeLevels": ["gradeLevels is required."]
  },
  "errors": []
}
```

### Migrating from the Ed-Fi ODS/API

Review request bodies for property names that differ in casing from the resource
definition. The exact, correctly cased property names for every resource are
published in the API's OpenAPI specification (available from the API's
Discovery/metadata endpoints). Sending those names verbatim is sufficient to
satisfy this rule.

## Related request-body behaviors

The rules below also apply when validating `POST` and `PUT` bodies. They are
largely carried over from the Ed-Fi ODS/API and are described here for
completeness.

### Unexpected properties are dropped

Any property that is not part of a resource's definition is removed from the
request before it is stored, and is not returned by later `GET` requests. The
request is not rejected solely because it contains unexpected properties. (As
described above, this is also why an incorrectly cased property is dropped.)

### Null values are dropped

A property explicitly set to `null` is removed from the request. For an
**optional** property this is silent. For a **required** property, dropping the
`null` leaves the property missing, which fails validation with **HTTP 400**.

### Leading and trailing whitespace

Leading and trailing whitespace is trimmed from descriptor `codeValue` and
`shortDescription` values. Other string values are stored as submitted.

### Type coercion from strings

Values submitted as JSON strings are coerced to the type defined by the resource
when the conversion is unambiguous:

- **Booleans.** The strings `"true"` and `"false"` (any letter casing) are
  accepted and stored as booleans. Numeric strings such as `"1"` and `"0"` are
  **not** interpreted as booleans.
- **Numbers and decimals.** Numeric strings such as `"100"` or `"3.14"` are
  accepted and stored as numbers.

If a string cannot be converted to the expected type, the request fails
validation with **HTTP 400**.

### Date and date-time normalization

Date and date-time values are normalized to ISO-8601:

- A date-time without a time zone is treated as UTC.
- A date-time with a time zone or offset is converted to UTC.
- A 12-hour time with `AM`/`PM` is accepted and converted to 24-hour UTC.
- A date supplied without a time is accepted; the time defaults to midnight.
- Slash-separated dates (for example `9/28/2021`) are accepted and converted to
  the dashed ISO-8601 form.

The stored value uses the form `yyyy-MM-ddTHH:mm:ssZ`. For example,
`2021-09-28` is stored as `2021-09-28T00:00:00Z`, and `2021-09-28 2:15:30 PM`
is stored as `2021-09-28T14:15:30Z`.

> [!NOTE]
> Slash-separated dates are interpreted month-first (`M/d/yyyy`), so an ambiguous
> value such as `5/6/2009` is read as May 6. To avoid ambiguity, submit dates in
> ISO-8601 (`yyyy-MM-dd`) form.

## What is not case-sensitive

Case sensitivity applies to **property names in request bodies**. The following
are matched case-insensitively:

| Element | Case-sensitive? |
| --- | --- |
| Request-body property names | Yes |
| Resource endpoint names in the URL path | No |
| Query parameter names | No |
| Reserved query parameters (`limit`, `offset`, `totalCount`) | No |
| Descriptor URI values | No |

For example, `GET /ed-fi/SCHOOLS?LiMiT=2` is equivalent to
`GET /ed-fi/schools?limit=2`, and a descriptor value such as
`uri://ed-fi.org/GradeLevelDescriptor#Ninth grade` is matched regardless of
casing.

> [!NOTE]
> Case-insensitive matching of query **values** (the data being filtered on) is
> not part of this contract and should not be assumed.

## References

- Ed-Fi API guideline:
  [Data Strictness](https://docs.ed-fi.org/reference/data-exchange/api-guidelines/design-and-implementation-guidelines/api-design-guidelines/rest-api-conventions/data-strictness/)
- The resource OpenAPI specification published by the API (via its
  Discovery/metadata endpoints), which defines the exact property names and
  casing for every resource.
