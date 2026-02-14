# GAP Analysis

This document summarizes the contractual and behavioral gaps between the Admin API v2.3 and the CMS (DMS Configuration Service) implementation.

## General Gaps

### API information payload

Admin API exposes only `version` and `build`, whereas CMS advertises application metadata, release identifiers, and the discovery URL. To remove guesswork for identity/bootstrap flows, Admin API should add the missing fields.

Admin API

```json
{
  "version": "2.0",
  "build": "0.1.0.0"
}
```

CMS

```json
{
  "version": "0.7.0",
  "applicationName": "Ed-Fi Alliance DMS Configuration Service",
  "informationalVersion": "Release Candidate 1",
  "urls": {
    "openApiMetadata": "http://localhost:8081/metadata/specifications"
  }
}
```

### HTTP status semantics

The CMS contract relies on specific status codes (for example `201 Created` for POST /vendors and `204 No Content` for DELETE operations). AdminAPI for some PUT returns 200. So that, we need to use the same codes for both contracts.

PUT /v2/vendors/{{vendorId}}

Response

```
HTTP/1.1 200 OK
Server: nginx/1.27.2
Date: Tue, 10 Feb 2026 19:48:42 GMT
Content-Length: 0
Connection: close
```

PUT /v2/vendors/{{vendorId}}

Response

```
HTTP/1.1 204 No Content
Connection: close
Date: Tue, 10 Feb 2026 19:49:10 GMT
Server: Kestrel
```

### `Location` header format

After POST operations, Admin API returns relative `Location` headers while CMS returns fully qualified URLs. The Admin API ecosystem expects a relative URL, so either CMS should include both formats or align to relative URLs so existing ID extraction logic continues to work.

Admin API

```
Location: /profiles/2
```

CMS

```
Location: http://localhost:8081/v2/profiles/2
```

### Query Parameter Support (Sort/Limit)

CMS endpoints generally do not support query parameters for sorting or limiting results (such as `offset`, `limit`, `orderBy`, or `direction`). While Admin API provides these options for endpoints like tenants, CMS omits them except where explicitly planned for parity. This can impact client applications that rely on pagination or sorting features present in the Admin API.

### Instance naming (`ods*` vs `dms*`)

The Admin API schema, DTOs, and query parameters consistently use the `odsInstance` naming. CMS renamed every element to `dmsInstance`. Because these shapes appear throughout applications, API clients, and instance-management routes, the casing and naming must match exactly (for example, `odsInstanceIds` arrays, `instanceName` vs `name`).

| AdminApi                | CMS                        |
|-------------------------|----------------------------|
| odsInstance             | dmsInstance                |
| odsInstanceRouteContexts| dmsInstanceRouteContexts   |
| odsInstanceDerivatives  | dmsInstanceDerivatives     |

GET AdminApi /v2/odsInstances

```json
[
  {
    "id": 1,
    "name": "Ods-test",
    "instanceType": "OdsInstance"
  }
]
```

GET CMS /v2/dmsInstances

```json
[
  {
    "id": 1,
    "instanceType": "Development",
    "instanceName": "Local Development Instance",
    "connectionString": "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;",
    "dmsInstanceRouteContexts": [],
    "dmsInstanceDerivatives": [],
    "tenantId": null
  }
]
```

### PUT identifier validation

Admin API trusts the identifier supplied in the URL. CMS additionally requires an `id` property in the request body and rejects the call when the values differ. Introducing this validation to Admin API would be a breaking change, so the safer option is for CMS to relax the body requirement when operating in Admin compatibility mode.

CMS
PUT /v2/vendors/{{vendorId}}

```json
{
  "id": {{vendorId}},
  "company": "Sample Vendor (Updated)",
  "namespacePrefixes": "uri://sample/vendor",
  "contactName": "Updated Contact",
  "contactEmailAddress": "<vendor.owner@example.org>"
}
```

Response

```
HTTP/1.1 204 No Content
Connection: close
Date: Tue, 10 Feb 2026 19:00:25 GMT
Server: Kestrel
```

If the id is not sent it will fail.
CMS
PUT /v2/vendors/{{vendorId}}

```json
{
  "company": "Sample Vendor (Updated)",
  "namespacePrefixes": "uri://sample/vendor",
  "contactName": "Updated Contact",
  "contactEmailAddress": "<vendor.owner@example.org>"
}
```

Response

```
{
  "detail": "Data validation failed. See 'validationErrors' for details.",
  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
  "title": "Data Validation Failed",
  "status": 400,
  "correlationId": "0HNJ8OCUNQODN:00000001",
  "validationErrors": {
    "Id": [
      "Request body id must match the id in the url."
    ]
  },
  "errors": []
}
```

### Error payload shape

Admin API returns a compact payload with `title` and `errors` dictionary, while CMS uses a Problem Details style response that includes `type`, `status`, `correlationId`, and `validationErrors`. Client libraries built for Admin API cannot parse the CMS format. CMS should return the Admin API envelope (or dual-write both structures) so existing error handling logic continues to work.

Admin API (profiles example)

```json
{
  "title": "Validation failed",
  "errors": {
    "Definition": [
      "Profile name attribute value should match with Sample Profile (Updated)."
    ]
  }
}
```

CMS

```json
{
  "detail": "Data validation failed. See 'validationErrors' for details.",
  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
  "title": "Data Validation Failed",
  "status": 400,
  "correlationId": "0HNJ5N5UEOL2J:00000001",
  "validationErrors": {
    "Id": [
      "Profile Id must be greater than zero."
    ],
    "Definition": [
      "Name must match the name attribute in the XML definition."
    ]
  },
  "errors": []
}
```

## Endpoint-Level Gaps

### Applications

#### Claim set name validation

Admin API accepts spaces in `claimSetName` when creating an application. CMS rejects the same value with a `400` and a validation error (`Claim set name must not contain white spaces.`). For backwards compatibility CMS should align to the Admin validation rules or provide a compatibility flag.

#### GET Applications

Admin API returns the full `applicationModel`, including `enabled`, `educationOrganizationIds`, `profileIds`, and `odsInstanceIds`. CMS exposes renames the instance array to `dmsInstanceIds`, so callers lose required context.

Admin API

```json
[
  {
    "id": 1,
    "applicationName": "Sample Application",
    "claimSetName": "District Hosted SIS",
    "educationOrganizationIds": [255901001],
    "vendorId": 2,
    "profileIds": [1],
    "odsInstanceIds": [1],
    "enabled": true
  }
]
```

CMS

```json
[
  {
    "id": 2,
    "applicationName": "For ed orgs",
    "vendorId": 3,
    "claimSetName": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
    "educationOrganizationIds": [
      255,
      255901
    ],
    "dmsInstanceIds": [
      3
    ],
    "profileIds": []
  }
]
```

### API clients

CMS camel-cases every route segment (`/v2/apiClients`), accepts both integers and strings for IDs, omits some fields and renames `odsInstanceIds` to `dmsInstanceIds`. Admin API also returns the newly issued key/secret on POST/PUT/reset, while CMS returns an empty `200`.

#### GET payload shape

Admin API and CMS return different payloads for ApiClient records. CMS renames the instance array to `dmsInstanceIds` and omits some fields, so callers lose required context.

Admin API

```json
[
  {
    "id": 1,
    "key": "vhr4ymgjaUdb",
    "name": "Sample Application (Updated)",
    "isApproved": true,
    "useSandbox": false,
    "sandboxType": 0,
    "applicationId": 1,
    "keyStatus": "Active",
    "educationOrganizationIds": [],
    "odsInstanceIds": [
      1
    ]
  }
]
```

CMS

```json
[
  {
    "id": 1,
    "applicationId": 1,
    "clientId": "c86b44f2-a80b-450d-be0c-4ecf43397e03",
    "clientUuid": "cbcf7711-10f8-40e9-8c6e-8746e4a7830a",
    "name": "For ed orgs",
    "isApproved": true,
    "dmsInstanceIds": [
      2
    ]
  }
]
```

### API clients by application

Admin API filters by numeric application ID and includes `useSandbox`, `sandboxType`, and `keyStatus`. CMS exposes only UUID-based identifiers and removes sandbox metadata.

Also, AdminAPI uses id to filter the GET apiClient but CMS uses the clientUuid.

Admin API

```json
[
  {
    "id": 1,
    "key": "vhr4ymgjaUdb",
    "name": "Sample Application (Updated)",
    "isApproved": true,
    "useSandbox": false,
    "sandboxType": 0,
    "applicationId": 1,
    "keyStatus": "Active",
    "educationOrganizationIds": [],
    "odsInstanceIds": [1]
  }
]
```

CMS

```json
[
  {
    "id": 1,
    "applicationId": 1,
    "clientId": "3bf96300-e05e-4376-8a4e-6e4ec99259ab",
    "clientUuid": "01fa1cfb-806e-4a03-91c4-f87fbe3b8839",
    "name": "Sample Application",
    "isApproved": true,
    "dmsInstanceIds": [1]
  }
]
```

#### POST response payload

Admin API returns an `applicationResult` (including `name` and `applicationId`) with `201 Created`. CMS only returns an object with `id`, `key`, and `secret`, so Admin UI flows that display the friendly name or store the numeric application ID cannot function.

Admin API

```json
{
  "id": 2,
  "name": "Sample Api Client",
  "key": "gnDrdsZypzHN",
  "secret": "4cTdDMY28Dk7PhWOWbVcKUmV",
  "applicationId": 1
}
```

CMS

```json
{
  "id": 2,
  "key": "f6131a61-b368-4e2e-8df8-f883a0bdfb4b",
  "secret": "M578tvpppKqOMKPeO4Vugsv25xKUssUW"
}
```

### Claim set export

Admin API emits the full resource-claim tree (actions, default strategies, overrides). CMS returns only a high-level claim set descriptor.

Admin API

```json
{
  "resourceClaims": [
    {
      "id": 1,
      "name": "types",
      "actions": [
        {
          "name": "Read",
          "enabled": true
        }
      ],
      "_defaultAuthorizationStrategiesForCRUD": [
        {
          "actionId": 2,
          "actionName": "Read",
          "authorizationStrategies": [
            {
              "authStrategyId": 1,
              "authStrategyName": "NoFurtherAuthorizationRequired",
              "isInheritedFromParent": false
            }
          ]
        }
      ],
      "authorizationStrategyOverridesForCRUD": [],
      "children": []
    }
  ]
}
```

CMS

```json
{
  "id": 5,
  "name": "EdFiSandbox",
  "_isSystemReserved": true,
  "_applications": {}
}
```

### Instances and derivatives

Admin API models instances with `name`, `instanceType`, and optional details. CMS adds connection metadata but renames fields (`instanceName`) and nests derivative/context collections differently.

Admin API

```json
[
  {
    "id": 1,
    "name": "Ods-test",
    "instanceType": "OdsInstance"
  }
]
```

CMS

```json
[
  {
    "id": 1,
    "instanceType": "Development",
    "instanceName": "Local Development Instance",
    "connectionString": "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;",
    "dmsInstanceRouteContexts": [],
    "dmsInstanceDerivatives": [],
    "tenantId": null
  }
]
```

#### Applications by instance

`/v2/odsInstances/{odsInstanceId}/applications` in Admin API includes an `enabled` flag for each application. CMS omits that property, so Admin consoles cannot display or toggle application availability per instance. Add the field to the CMS response.

### Tenants differences

Admin API exposes:

* `/v2/tenants`
* `/v2/tenants/{tenantName}`
* `/v2/tenants/details`

CMS implements `/v2/tenants` and `/v2/tenants/{id}` but expects an integer identifier and omits the `details` summary. For parity:

1. Accept tenant name in the path to preserve the Admin natural key. It seems that `/v2/tenants` and `/v2/tenants/{id}` will be removed.
2. Add the `details` projection (or move its payload into the instance endpoints as planned) so Admin tooling can render tenant-instance mappings.
3. Support Admin query parameters (`offset`, `limit`, `orderBy`, `direction`).

### Resource claim metadata

None of the `/v2/resourceClaims*` endpoints are present in CMS. These routes power the Admin UI pages used to browse claims/actions and configure overrides. We created a ticket previously [DMS-853](https://edfi.atlassian.net/browse/DMS-853)

#### /v2/resourceClaims (AdminApi)

##### Schema

```json
[
  {
    "id": 0,
    "name": "string",
    "parentId": 0,
    "parentName": "string",
    "children": [
      "string"
    ]
  }
]
```

##### Payload (Response)

```json
{
    "id": 382,
    "name": "epdm",
    "parentId": 0,
    "parentName": null,
    "children": [
        "id": 386,
        "name": "performanceEvaluation",
        "parentId": 382,
        "parentName": "epdm",
        "children": [
          {
            "id": 387,
            "name": "performanceEvaluation",
            "parentId": 386,
            "parentName": "performanceEvaluation",
            "children": []
          },
          {
            "id": 388,
            "name": "evaluation",
            "parentId": 386,
            "parentName": "performanceEvaluation",
            "children": []
          }
        ]
    ]
}
```

#### /v2/resourceClaimActionAuthStrategies (AdminApi)

##### Schema

```json
[
  {
    "resourceClaimId": 0,
    "resourceName": "string",
    "claimName": "string",
    "authorizationStrategiesForActions": [
      {
        "actionId": 0,
        "actionName": "string",
        "authorizationStrategies": [
          {
            "authStrategyId": 0,
            "authStrategyName": "string"
          }
        ]
      }
    ]
  }
]
```

##### Payload (Response)

```json
[
  {
    "resourceClaimId": 448,
    "resourceName": "candidate",
    "claimName": "http://ed-fi.org/ods/identity/claims/ed-fi/candidate",
    "authorizationStrategiesForActions": [
      {
        "actionId": 1,
        "actionName": "Create",
        "authorizationStrategies": [
          {
            "authStrategyId": 1,
            "authStrategyName": "NoFurtherAuthorizationRequired"
          }
        ]
      },
      {
        "actionId": 2,
        "actionName": "Read",
        "authorizationStrategies": [
          {
            "authStrategyId": 1,
            "authStrategyName": "NoFurtherAuthorizationRequired"
          }
        ]
      },
      {
        "actionId": 3,
        "actionName": "Update",
        "authorizationStrategies": [
          {
            "authStrategyId": 1,
            "authStrategyName": "NoFurtherAuthorizationRequired"
          }
        ]
      },
      {
        "actionId": 4,
        "actionName": "Delete",
        "authorizationStrategies": [
          {
            "authStrategyId": 1,
            "authStrategyName": "NoFurtherAuthorizationRequired"
          }
        ]
      },
      {
        "actionId": 5,
        "actionName": "ReadChanges",
        "authorizationStrategies": [
          {
            "authStrategyId": 1,
            "authStrategyName": "NoFurtherAuthorizationRequired"
          }
        ]
      }
    ]
  }
]
```

### Authorization strategies

CMS includes `/authorizationStrategies`, but Admin API does not. For parity we either need to backfill the endpoint in Admin API (preferred) or retire it from CMS. Regardless of direction, both services should describe the response schema and supported error codes.

##### Payload (Response)

```json
[
  {
    "id": 11,
    "name": "RelationshipsWithEdOrgsOnlyInverted",
    "displayName": "Relationships with Education Organizations only (Inverted)"
  },
  {
    "id": 12,
    "name": "RelationshipsWithEdOrgsAndPeopleInverted",
    "displayName": "Relationships with Education Organizations and People (Inverted)"
  }
]
```

### Vendors

Vendor POST in Admin API returns a `201 Created` with an empty body, while CMS emits a body with `id`, `status`, and `title`. Although the extra fields are not harmful, CMS should still send the Admin status codes and ensure the schema matches what clients expect. An example CMS payload today:

#### AdminApi (Response)

```
HTTP/1.1 201 Created
Server: nginx/1.27.2
Date: Fri, 13 Feb 2026 15:35:50 GMT
Content-Length: 0
Connection: close
Location: /vendors/1
```

```json
```

#### CMS (Response)

```
HTTP/1.1 201 Created
Connection: close
Content-Type: application/json; charset=utf-8
Date: Fri, 13 Feb 2026 15:36:56 GMT
Server: Kestrel
Location: http://localhost:8081/v2/vendors/1
Transfer-Encoding: chunked
```

```json
{
  "id": 1,
  "status": 201,
  "title": "New Vendor Sample Vendor has been created successfully."
}
```

## Upcoming Admin API Changes

* **Tenant endpoints deprecation:** `/v2/tenants`, `/v2/tenants/{tenantName}`, and `/v2/tenants/details` are scheduled for removal once the instance detail payloads fully cover the same information. CMS consumers should treat these routes as legacy and plan to rely on the instance endpoints instead.
* **Education organization endpoints:** Admin API will expose new education-organization (`edOrg`) endpoints. The shape is still in progress, so parity work should wait for the finalized schema before backfilling or mapping CMS routes.

## Summary

If an Admin API client simply switches its base URL to the CMS service without code changes:

* **Non-existent routes return 404:** Any call to `/v2/resourceClaims*`, `/v2/claimSets/*/resourceClaimActions`, or `/v2/tenants/details` immediately fails because CMS does not expose those endpoints.
* **Successful calls return incompatible payloads:** Requests to `/v2/applications`, `/v2/apiClients`, `/v2/odsInstances*`, and `/v2/claimSets/{id}/export` will still return `200 OK`, but the JSON shape differs (missing `enabled`, `odsInstanceIds`, sandbox metadata, resource-claim trees). Clients that deserialize Admin DTOs will either throw parsing errors or silently discard required data.
* **Error handling regresses:** CMS emits Problem Details bodies and often responds with `200` even for validation failures. Callers expecting Admin API status codes (`400/401/403/409`) and the `{ "title": "Validation failed", "errors": { ... } }` envelope will misinterpret failures as success.
* **Tenant-aware automation stalls:** Logic that looks up tenants by name or relies on `/v2/tenants/details` will either receive 404s or incomplete payloads, causing instance provisioning and routing scripts to fail.
* **`Location` headers cannot be parsed:** Admin clients extract new resource IDs from relative headers (for example `/profiles/2`). CMS returns absolute URLs (`http://localhost:8081/v2/profiles/2`), so lightweight HTTP clients that concatenate the base URI or regex the relative path will either duplicate segments or fail to capture the identifier.
