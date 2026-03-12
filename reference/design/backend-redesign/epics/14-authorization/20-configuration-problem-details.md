---
jira: DMS-1099
jira_url: https://edfi.atlassian.net/browse/DMS-1099
---

# Story: Implement Security Configuration ProblemDetails

## Description

Implement request-time 500 "Security Configuration Error" ProblemDetails responses for misconfigured authorization metadata per `reference/design/backend-redesign/design-docs/auth.md`.

## Acceptance Criteria
- When a request targets a resource for which no claim set or security metadata has been configured, the system returns a 500 response with:
    - Type: urn:ed-fi:api:system-configuration:security
    - Title: Security Configuration Error
    - Detail: A security configuration problem was detected. The request cannot be authorized.
    - Error: No security metadata has been configured for this resource.
- When the caller's claim matches a resource but no authorization strategies are defined for the requested action, the system returns a 500 response with:
    - Type: urn:ed-fi:api:system-configuration:security
    - Title: Security Configuration Error
    - Detail: A security configuration problem was detected. The request cannot be authorized.
    - Error: No authorization strategies were defined for the requested action '{action}' against resource URIs ['{uri1}', '{uri2}'] matched by the caller's claim
    '{claimName}'.
- When the configured authorization strategy name does not correspond to any known strategy implementation, the system returns a 500 response with:
    - Type: urn:ed-fi:api:system-configuration:security
    - Title: Security Configuration Error
    - Detail: A security configuration problem was detected. The request cannot be authorized.
    - Error: Could not find authorization strategy implementations for the following strategy names: '{strategyName1}', '{strategyName2}'.
- When a custom view-based strategy references a basis entity property that does not exist on the authorization subject entity, the system returns a 500 response with:
    - Type: urn:ed-fi:api:system-configuration:security
    - Title: Security Configuration Error
    - Detail: A security configuration problem was detected. The request cannot be authorized.
    - Error: Unable to find a property on the authorization subject entity type '{targetEntityName}' corresponding to the '{propertyName}' property on the custom authorization view's basis entity type '{basisEntityName}' in order to perform authorization. Should a different authorization strategy be used?
- All scenarios include a correlationId in the response body for traceability, consistent with the ProblemDetails RFC 9457 structure used across DMS.
- These checks run at request time during authorization resolution, not at startup. They are distinct from the startup validations in DMS-1091.
- The error is logged with enough detail for an API host to diagnose and fix the misconfiguration.

NOTE: Some of these checks might have already been implemented.
