# Context-Based Routing for Segmented Datastores in DMS Deployments

## Overview

The Ed-Fi Data Management Service (DMS) supports explicit data segmentation through context-based routing using **route qualifiers**. This approach allows API requests to include contextual values (such as school year, district ID, or other identifiers) in the URL path, enabling the same API client to access multiple DMS instances with a single set of credentials.

## Database Segmentation Strategy

### Why Segment Data?

Large-scale Ed-Fi deployments benefit from dividing data into separate databases rather than storing everything in a single datastore. Segmentation provides:

- **Performance optimization** - Smaller databases improve query performance and reduce resource contention
- **Data isolation** - Separate databases ensure district or year-specific data remains isolated
- **Operational flexibility** - Independent databases can be backed up, restored, or archived individually
- **Scalability** - Distributed databases can be hosted across multiple servers as data volumes grow
- **Compliance** - Some regulations require physical separation of data by organization or timeframe

The segmentation strategy should be planned before deployment, as it affects both the database architecture and the API client experience.

### Choosing a Segmentation Strategy

When implementing database segmentation in DMS, you need to make two key decisions:

1. **Segmentation approach** - How to divide the data (by school year, district, region, or other criteria)
2. **Client experience** - Whether segmentation is implicit (transparent to API clients) or explicit (requires route qualifiers in URLs)

### Client Experience Models

**Implicit Segmentation:**
- Each API client credential has access to a single DMS instance
- The database routing is transparent - clients don't specify which instance in the URL
- Simplest for API clients but requires separate credentials for each instance
- Best for clients that only need access to one district or one year

**Explicit Segmentation:**
- API clients use route qualifiers in URLs to specify which instance to access
- Single credential can access multiple instances based on URL context
- Requires clients to construct URLs like `/255901/2024/data/ed-fi/students`
- Best for regional services or applications that work across multiple districts/years

### Segmentation Options

The table below shows common segmentation strategies and their characteristics:

| Strategy | Experience | Description |
|----------|-----------|-------------|
| **Shared Instance** | Implicit | Multiple API clients access a single shared database. No year or district separation—all data coexists in one instance. Requires manual data rollover for year transitions. Suitable for small deployments or single-district implementations. |
| **Year-Specific** | Implicit | Data is separated by school year into distinct databases. Each API client credential is tied to a single year's database. New instances and credentials are provisioned annually. Best for districts that need clean year-over-year separation without multi-year access. |
| **Year-Specific** | Explicit | Multiple school year databases are accessible via URL-based routing (e.g., `/2024/data/ed-fi/schools`). A single API client can access different years by changing the route qualifier. Requires route context configuration in DMS. |
| **District-Specific** | Implicit | Each district has a separate database. API clients have credentials tied to a single district's instance. Provides strong data isolation between districts. Common in regional or cooperative service deployments. |
| **District-Specific** | Explicit | Multiple district databases are accessible via URL-based routing (e.g., `/255901/data/ed-fi/schools`). Regional applications can access different districts using the same credential. Requires route context configuration defining district identifiers. |
| **Instance-Year Specific** | Implicit | Data is segmented by both district (or other instance type) and school year. Each client credential accesses only one specific instance-year combination. Provides maximum isolation but requires many credential sets. |
| **Instance-Year Specific** | Explicit | Multi-dimensional routing allows access to different instances and years via URL paths (e.g., `/255901/2024/data/ed-fi/schools`). Single credential accesses multiple dimensions. Requires defining multiple route context keys (e.g., `districtId` and `schoolYear`). |

**Note:** Custom segmentation strategies are also possible. DMS's flexible route context system allows you to define segmentation dimensions that match your operational needs (regions, programs, departments, etc.).

### Choosing the Right Strategy

Consider these factors when selecting your segmentation approach:

- **Deployment scale** - How many districts, years, or other segments will you manage?
- **Client capabilities** - Can your API clients handle explicit URL routing, or do they need simple endpoints?
- **Access patterns** - Do clients need multi-year or multi-district access, or single-instance access?
- **Operational model** - Who manages credentials, and how often do they change?
- **Data retention** - How long must historical data remain accessible?

For most regional or multi-district deployments, **explicit segmentation** with district and year routing provides the best balance of flexibility and manageability.

## Key Concept

Context-based routing enables a single DMS deployment to serve multiple isolated data instances, each identified by one or more route qualifiers. For example:

- `/255901/2024/data/ed-fi/students` → Routes to District 255901's 2024 school year database
- `/255901/2025/data/ed-fi/students` → Routes to District 255901's 2025 school year database
- `/255902/2024/data/ed-fi/students` → Routes to District 255902's 2024 school year database

The primary benefit is allowing the same API client credentials to access multiple segregated databases based on URL context, without requiring separate authentication for each instance.

## Configuration Overview

To enable context-based routing, administrators use the **DMS Configuration Service** to:

1. **Create DMS Database Instances** - Define separate database instances with connection strings
2. **Define Route Contexts** - Associate each instance with one or more context key-value pairs
3. **Configure Applications** - Grant applications access to multiple instances

## URL Pattern

When context-based routing is configured, API requests follow this pattern:

```
http://{host}:{port}/{qualifier1}/{qualifier2}/.../data/ed-fi/{resource}
```

**Examples:**

- `http://localhost:8080/255901/2024/data/ed-fi/students`
- `http://localhost:8080/255901/2025/data/ed-fi/schools`
- `http://localhost:8080/255902/2024/data/ed-fi/contentClassDescriptors`

The order and number of route qualifiers must match the context keys defined for your instances.

## Configuration Steps

### Step 1: Authenticate with Configuration Service

Obtain an access token from the DMS Configuration Service:

```http
POST http://localhost:8081/connect/token
Content-Type: application/x-www-form-urlencoded

client_id={your_admin_client_id}
&client_secret={your_admin_secret}
&grant_type=client_credentials
&scope=edfi_admin_api/full_access
```

### Step 2: Create DMS Instances

Create a separate DMS instance for each database you want to route to:

```http
POST http://localhost:8081/v2/dmsInstances
Authorization: bearer {config_token}
Content-Type: application/json

{
  "instanceType": "District",
  "instanceName": "District 255901 - School Year 2024",
  "connectionString": "host=dms-postgresql;port=5432;username=postgres;password=yourpassword;database=edfi_datamanagementservice_d255901_sy2024;"
}
```

The response will include an `id` field that you'll use to associate route contexts.

### Step 3: Define Route Contexts

For each instance, create route context entries that define how URL segments map to that instance:

```http
POST http://localhost:8081/v2/dmsInstanceRouteContexts
Authorization: bearer {config_token}
Content-Type: application/json

{
  "instanceId": 1,
  "contextKey": "districtId",
  "contextValue": "255901"
}
```

```http
POST http://localhost:8081/v2/dmsInstanceRouteContexts
Authorization: bearer {config_token}
Content-Type: application/json

{
  "instanceId": 1,
  "contextKey": "schoolYear",
  "contextValue": "2024"
}
```

**Important:** Each instance can have multiple route contexts. DMS requires **all** context keys to match for routing to succeed.

### Step 4: Create Vendor and Application

Create a vendor and application that has access to multiple instances:

```http
POST http://localhost:8081/v2/vendors
Authorization: bearer {config_token}
Content-Type: application/json

{
  "company": "Your Organization",
  "contactName": "Admin Name",
  "contactEmailAddress": "admin@example.edu",
  "namespacePrefixes": "uri://ed-fi.org,uri://yourorg.edu"
}
```

```http
POST http://localhost:8081/v2/applications
Authorization: bearer {config_token}
Content-Type: application/json

{
  "vendorId": 1,
  "applicationName": "Multi-Instance Application",
  "claimSetName": "SIS Vendor",
  "educationOrganizationIds": [255901, 255902],
  "dmsInstanceIds": [1, 2, 3]
}
```

The response will include `key` and `secret` fields for API authentication.

## Using Context-Based Routing

### Authentication

Authenticate with the DMS API using your application credentials:

```http
POST http://localhost:8080/connect/token
Authorization: basic {application_key}:{application_secret}
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
```

### Making Requests

Include the route qualifiers in the URL path before `/data/ed-fi/`:

```http
GET http://localhost:8080/255901/2024/data/ed-fi/students
Authorization: bearer {dms_token}
```

```http
POST http://localhost:8080/255901/2025/data/ed-fi/schools
Authorization: bearer {dms_token}
Content-Type: application/json

{
  "schoolId": 123,
  "nameOfInstitution": "Example School",
  ...
}
```

### How Instance Resolution Works

When DMS receives a request:

1. **Extracts route qualifiers** from the URL path (e.g., `255901` and `2024`)
2. **Matches against route contexts** to find an instance where all context key-value pairs match
3. **Routes the request** to the matched instance's database
4. **Returns 404** if no instance matches the provided qualifiers

## Configuration Examples

### Example 1: Year-Specific Routing

Route requests to different databases based on school year:

**Instance Configuration:**

| Instance | Database | Context Key | Context Value |
|----------|----------|-------------|---------------|
| 1 | edfi_dms_2024 | schoolYear | 2024 |
| 2 | edfi_dms_2025 | schoolYear | 2025 |

**API Usage:**

- `GET /2024/data/ed-fi/students` → Routes to `edfi_dms_2024`
- `GET /2025/data/ed-fi/students` → Routes to `edfi_dms_2025`

### Example 2: District-Specific Routing

Route requests to different databases based on district:

**Instance Configuration:**

| Instance | Database | Context Key | Context Value |
|----------|----------|-------------|---------------|
| 1 | edfi_dms_district_255901 | districtId | 255901 |
| 2 | edfi_dms_district_255902 | districtId | 255902 |

**API Usage:**

- `GET /255901/data/ed-fi/schools` → Routes to `edfi_dms_district_255901`
- `GET /255902/data/ed-fi/schools` → Routes to `edfi_dms_district_255902`

### Example 3: Multi-Dimensional Routing

Route requests based on both district and school year:

**Instance Configuration:**

| Instance | Database | Context Keys | Context Values |
|----------|----------|--------------|----------------|
| 1 | edfi_dms_d255901_sy2024 | districtId, schoolYear | 255901, 2024 |
| 2 | edfi_dms_d255901_sy2025 | districtId, schoolYear | 255901, 2025 |
| 3 | edfi_dms_d255902_sy2024 | districtId, schoolYear | 255902, 2024 |

**API Usage:**

- `GET /255901/2024/data/ed-fi/students` → Routes to `edfi_dms_d255901_sy2024`
- `GET /255901/2025/data/ed-fi/students` → Routes to `edfi_dms_d255901_sy2025`
- `GET /255902/2024/data/ed-fi/students` → Routes to `edfi_dms_d255902_sy2024`

## Error Handling

### No Instance Found (404)

If no instance matches the provided route qualifiers, DMS returns a 404 response:

```http
GET /999999/2024/data/ed-fi/students
Authorization: bearer {token}

Response: 404 Not Found
```

This can occur when:

- An invalid district ID or school year is provided
- The application doesn't have access to the requested instance
- No route contexts match the qualifier combination

### Missing Route Qualifiers

If route qualifiers are required but not provided in the URL, the request may fail:

```http
GET /data/ed-fi/students
Authorization: bearer {token}

Response: 404 Not Found or 400 Bad Request
```

## Important Notes

- **All context keys must match**: If an instance has multiple route contexts (e.g., districtId and schoolYear), the request URL must provide values for all of them in the correct order.
- **Application access control**: Applications must be explicitly granted access to instances via the `dmsInstanceIds` field when creating the application.
- **Route qualifier order**: The order of route qualifiers in the URL should be consistent across your API implementation.
- **Discovery API**: Route qualifiers are included in Location headers and should be used consistently across all operations (GET, POST, PUT, DELETE).
- **No default instance**: Unlike single-instance deployments, there is no default database when using context-based routing. Route qualifiers are always required.

## Viewing Configuration

### List All DMS Instances

```http
GET http://localhost:8081/v2/dmsInstances?offset=0&limit=25
Authorization: bearer {config_token}
```

### List All Route Contexts

```http
GET http://localhost:8081/v2/dmsInstanceRouteContexts?offset=0&limit=25
Authorization: bearer {config_token}
```

### Get Specific Instance

```http
GET http://localhost:8081/v2/dmsInstances/{instanceId}
Authorization: bearer {config_token}
```

## Migration from ODS/API

If you're migrating from the Ed-Fi ODS/API platform that used `OdsContextRouteTemplate`:

| ODS/API Feature | DMS Equivalent |
|-----------------|----------------|
| `OdsContextRouteTemplate` in ApiSettings | Route contexts defined via Configuration Service API |
| `dbo.OdsInstances` table | `POST /v2/dmsInstances` endpoint |
| `dbo.OdsInstanceContext` table | `POST /v2/dmsInstanceRouteContexts` endpoint |
| ASP.NET route template syntax | Standard URL path segments |

## Summary

Context-based routing in DMS provides a powerful way to segment data while maintaining a unified API surface. By configuring instances and route contexts through the Configuration Service, you can:

- Support multiple districts with a single API deployment
- Maintain year-over-year historical data in separate databases
- Use flexible multi-dimensional routing (district + year, or other combinations)
- Grant applications access to multiple instances with a single credential

This approach is ideal for regional deployments and for organizations that require strict data segmentation, while making the segmentation explicit to the API client through the API routes.
