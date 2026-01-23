# Getting Started with Multi-Tenancy

This guide walks you through deploying and configuring a multi-tenant Ed-Fi Data
Management Service (DMS) environment.

## Overview

Multi-tenancy in DMS provides two layers of data isolation:

1. **Tenant Isolation** - Configuration data (vendors, applications, instances)
   is isolated between tenants via the `Tenant` HTTP header in Configuration
   Service requests
2. **Instance Routing** - Each tenant can have multiple DMS instances (databases),
   accessible via URL-based routing or credential-based routing

## Prerequisites

- Docker and Docker Compose installed
- PowerShell Core 7+ (`pwsh`)
- PostgreSQL client tools (optional, for database management)

## Step 1: Configure the Environment File

Navigate to the docker-compose directory and create your environment file (a
working `.env.multitenancy` is included in the repo with the settings specified here):

```powershell
cd eng/docker-compose
cp .env.example .env.multitenancy
```

Edit `.env.multitenancy` and configure these key settings:

### Multi-Tenancy Settings

```bash
# Enable multi-tenancy in DMS
DMS_MULTI_TENANCY=true

# Enable multi-tenancy in Configuration Service
DMS_CONFIG_MULTI_TENANCY=true
```

### Route Qualifier Settings (Optional)

If you want explicit URL-based routing (e.g., `/2024/data/ed-fi/schools`),
configure route qualifiers:

```bash
# Single qualifier (school year routing)
ROUTE_QUALIFIER_SEGMENTS=schoolYear

# Multiple qualifiers (district and school year routing)
ROUTE_QUALIFIER_SEGMENTS=districtId,schoolYear
```

When route qualifiers are configured:

- URLs follow the pattern: `/{qualifier1}/{qualifier2}/data/ed-fi/{resource}`
- Example: `GET /2024/data/ed-fi/schools` routes to the 2024 school year database

Leave `ROUTE_QUALIFIER_SEGMENTS` empty or commented out for implicit routing
(each API credential accesses only one instance).

### Database Settings

```bash
# PostgreSQL password
POSTGRES_PASSWORD=abcdefgh1!

# Main database name (used by Configuration Service)
POSTGRES_DB_NAME=edfi_datamanagementservice

# Enable automatic database schema deployment for the main database
NEED_DATABASE_SETUP=true

# IMPORTANT: Enable schema deployment to ALL tenant instance databases on startup
# With this setting DMS will deploy schema to each instance
# loaded from the Configuration Service. Otherwise you must do it yourself.
DMS_DEPLOY_DATABASE_ON_STARTUP=true
```

### Complete Example Environment File

A working [`.env.multitenancy`](../eng/docker-compose/.env.multitenancy) configuration
is included in the repository.

## Step 2: Deploy DMS

> [!NOTE]
> This guide uses a manual, API-driven workflow (via the `.http` file) because
> it matches how environments are commonly configured in the field.
> For local demos and quick setup, `start-local-dms.ps1` supports
> `-SchoolYearRange` to automatically create `dmsInstances` and their
> `schoolYear` route contexts.
> `-SchoolYearRange` and `-NoDmsInstance` are mutually exclusive.
> If `DMS_CONFIG_MULTI_TENANCY=true`, then `-SchoolYearRange` also requires
> `CONFIG_SERVICE_TENANT` in the environment file so the script can send the
> required `Tenant` header.
> Avoid mixing `-SchoolYearRange` with manual instance creation in the same
> environment, because it can create duplicate instances/route contexts.

Start the DMS stack with your multi-tenancy configuration:

```powershell
cd eng/docker-compose

pwsh ./start-local-dms.ps1 `
    -EnvironmentFile "./.env.multitenancy" `
    -EnableConfig `
    -EnableSwaggerUI `
    -IdentityProvider self-contained `
    -NoDmsInstance `
    -r
```

The `-NoDmsInstance` flag prevents automatic instance creation since
you'll create tenant-specific instances manually.

Wait for all services to start (approximately 2-3 minutes):

```powershell
docker ps
```

You should see containers running for:

- `dms-local-dms-1` (DMS API on port 8080)
- `dms-config-service` (Configuration Service on port 8081)
- `dms-postgresql` (PostgreSQL on port 5435)
- `dms-local-swagger-ui-1` (Swagger UI on port 8082)

## Step 3: Create Tenant Databases

Before creating instances in the Configuration Service, create the target
databases. DMS will deploy the schema automatically on first connection.

```powershell
# Create databases for Tenant 1 (DistrictA)
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_dms_districta_2024;"
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_dms_districta_2025;"

# Create databases for Tenant 2 (DistrictB)
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_dms_districtb_2024;"
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_dms_districtb_2025;"
```

### Connection String Format

When configuring instances, use this connection string format:

```
host=dms-postgresql;port=5432;username=postgres;password={your_password};database={database_name};
```

For the databases created above:

| Database | Connection String |
|----------|-------------------|
| edfi_dms_districta_2024 | `host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_dms_districta_2024;` |
| edfi_dms_districta_2025 | `host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_dms_districta_2025;` |
| edfi_dms_districtb_2024 | `host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_dms_districtb_2024;` |
| edfi_dms_districtb_2025 | `host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_dms_districtb_2025;` |

## Step 4: Configure Tenants and Instances

Use the REST client file to configure tenants, instances, and applications.

Open `src/dms/tests/RestClient/multi-tenancy-setup.http` in VS Code with the
REST Client extension installed.

### Configuration Workflow

Execute the requests in order:

1. **Register Admin** - Creates system administrator credentials
2. **Get Token** - Authenticates with Configuration Service
3. **Create Tenants** - Creates tenant organizations (e.g., DistrictA, DistrictB)
4. **Create Instances** - Creates DMS instances with connection strings (include
   the `Tenant` header)
5. **Create Route Contexts** - Associates route qualifier values with instances
6. **Create Vendors/Applications** - Creates API credentials for each tenant

> **Important**: When creating applications, save the `key` and `secret` from the
> response immediately. These credentials cannot be retrieved later and are needed
> to authenticate in Swagger UI (Step 7). If you lose them, use the
> `reset-credential` request in the HTTP file to generate new ones.

### Manual `schoolYear` instance setup (with `-NoDmsInstance`)

If you started the stack with `-NoDmsInstance` and want explicit school year
routing (for example, `/{tenant}/2024/data/...`), create the instances and
route contexts manually.

Prerequisites:

- `ROUTE_QUALIFIER_SEGMENTS=schoolYear` in your environment file.
- If `DMS_CONFIG_MULTI_TENANCY=true`, include `Tenant: {tenant-name}` on all
  tenant-scoped Configuration Service calls.
- Create the target PostgreSQL databases first (Step 3).

Steps (repeat per school year):

1. Create a tenant (only once per tenant, if needed).
2. Create a `dmsInstance` for the year.
3. Create a `dmsInstanceRouteContext` with `contextKey=schoolYear` and
   `contextValue={year}`.

Example for tenant `DistrictA` and school year `2024`:

```http
POST http://localhost:8081/v2/tenants
Authorization: bearer {token}
Content-Type: application/json

{
  "name": "DistrictA"
}
```

```http
POST http://localhost:8081/v2/dmsInstances
Authorization: bearer {token}
Tenant: DistrictA
Content-Type: application/json

{
  "instanceType": "SchoolYear",
  "instanceName": "District A - School Year 2024",
  "connectionString": "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_dms_districta_2024;"
}
```

```http
POST http://localhost:8081/v2/dmsInstanceRouteContexts
Authorization: bearer {token}
Tenant: DistrictA
Content-Type: application/json

{
  "instanceId": {instanceIdFromPreviousResponse},
  "contextKey": "schoolYear",
  "contextValue": "2024"
}
```

Create another instance for `2025` by repeating the last two requests with a
different database and `contextValue`.

After creating instances and route contexts, restart the DMS container so it
reloads instance configuration:

```powershell
docker restart dms-local-dms-1
```

Notes:

- If `DMS_CONFIG_MULTI_TENANCY=true`, include `Tenant: {tenant-name}` on all tenant-scoped Configuration Service requests.
- Each instance needs route context entries that match `ROUTE_QUALIFIER_SEGMENTS`.

## Step 5: Restart DMS Container

After creating all instances, restart the DMS container to load the new
configurations and deploy the database schema to each tenant database:

```powershell
docker restart dms-local-dms-1
```

Wait 30-60 seconds for DMS to restart. During startup, DMS will:

1. Load all tenants and their instances from the Configuration Service
2. Deploy the database schema to each instance database (if `DMS_DEPLOY_DATABASE_ON_STARTUP=true`)

Verify instances were loaded and schema was deployed:

```powershell
# Check instances were loaded
docker logs dms-local-dms-1 | Select-String "Successfully fetched"

# Check schema deployment (should see entries for each tenant database)
docker logs dms-local-dms-1 | Select-String "Deploying database schema"
```

You should see:

- `Successfully fetched X DMS instances`
- `Deploying database schema to DMS instance 'District A - School Year 2024'...`
- etc.

## Step 6: Test the Setup

### Get API Token

Authenticate using the application credentials created in Step 4:

```http
POST http://localhost:8081/connect/token
Authorization: basic {clientKey}:{clientSecret}
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
```

### Access Data with Multi-Tenancy and Route Qualifiers

With `DMS_MULTI_TENANCY=true` and `ROUTE_QUALIFIER_SEGMENTS=schoolYear` configured,
the URL pattern is: `/{tenant}/{routeQualifier}/data/ed-fi/{resource}`

```http
# Access DistrictA's 2024 school year data
GET http://localhost:8080/DistrictA/2024/data/ed-fi/schools
Authorization: bearer {token}

# Access DistrictA's 2025 school year data
GET http://localhost:8080/DistrictA/2025/data/ed-fi/schools
Authorization: bearer {token}

# Access DistrictB's 2024 school year data
GET http://localhost:8080/DistrictB/2024/data/ed-fi/schools
Authorization: bearer {token}
```

### Verify Data Isolation

Create data in different instances and verify isolation:

```powershell
# Check data in District A, 2024
docker exec dms-postgresql psql -U postgres -d edfi_dms_districta_2024 \
    -c "SELECT COUNT(*) FROM dms.Document;"

# Check data in District A, 2025
docker exec dms-postgresql psql -U postgres -d edfi_dms_districta_2025 \
    -c "SELECT COUNT(*) FROM dms.Document;"
```

## Configuration Reference

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `DMS_MULTI_TENANCY` | Enable multi-tenancy in DMS | `false` |
| `DMS_CONFIG_MULTI_TENANCY` | Enable multi-tenancy in Configuration Service | `false` |
| `ROUTE_QUALIFIER_SEGMENTS` | Comma-separated route qualifiers (e.g., `schoolYear` or `districtId,schoolYear`) | (empty) |
| `NEED_DATABASE_SETUP` | Deploy schema to the configuration service database on startup | `true` |
| `DMS_DEPLOY_DATABASE_ON_STARTUP` | Deploy schema to ALL tenant instance databases on startup (required for multi-tenancy) | `false` |

### API Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/v2/tenants/` | GET, POST | List/create tenants |
| `/v2/dmsInstances` | GET, POST, PUT, DELETE | Manage DMS instances |
| `/v2/dmsInstanceRouteContexts` | GET, POST, PUT, DELETE | Manage route contexts |
| `/v2/vendors` | GET, POST, PUT, DELETE | Manage vendors |
| `/v2/applications` | GET, POST, PUT, DELETE | Manage applications |

### Tenant Header

For multi-tenant Configuration Service requests, include:

```
Tenant: {tenant-name}
```

Tenant names must be alphanumeric with hyphens or underscores only (max 256
characters).

## Step 7: Access Swagger UI with Multi-Tenancy

With multi-tenancy enabled, Swagger UI provides dropdown selectors for both
tenant and school year selection.

### Navigate to Swagger UI

Open your browser to [http://localhost:8082](http://localhost:8082).

### Using the Tenant and School Year Selectors

When multi-tenancy is configured with multiple tenants and school years, you'll
see two dropdown selectors at the top of the Swagger UI page:

1. **Tenant** (green background) - Select the tenant organization you want to
   interact with
2. **School Year** (gray background) - Select the school year database instance

All API requests made through Swagger UI will automatically include the selected
tenant and school year in the URL path. For example, selecting "DistrictA" and
"2024" will route requests to:

```
http://localhost:8080/DistrictA/2024/data/ed-fi/{resource}
```

### Authentication in Swagger UI

When using Swagger UI with multi-tenancy:

1. Click the **Authorize** button
2. Enter the `client_id` and `client_secret` for an application that belongs to
   the selected tenant
3. The token request will be routed based on your credential's tenant association

Note: API credentials are tenant-specific. A credential created for "DistrictA"
will only work when the DistrictA tenant is selected.

## Troubleshooting

### No instance found (404)

- Verify the DMS container was restarted after creating instances
- Check that route contexts match the URL qualifiers exactly
- Verify the application has access to the requested instance (`dmsInstanceIds`)

### Tenant header required

- Include the `Tenant` header in all Configuration Service requests for
  tenant-specific resources

### Database connection errors

- Verify the database exists: `docker exec dms-postgresql psql -U postgres -l`
- Check the connection string format (use internal Docker hostname `dms-postgresql`)
- Ensure the password matches `POSTGRES_PASSWORD`

### Check logs

```powershell
# DMS logs
docker logs dms-local-dms-1 --follow

# Configuration Service logs
docker logs dms-config-service --follow
```

## Cleanup

To tear down the environment:

```powershell
cd eng/docker-compose
pwsh ./start-local-dms.ps1 -d -v -EnvironmentFile "./.env.multitenancy"
```

## Additional Resources

- [Database Segmentation Strategy](DATABASE-SEGMENTATION-STRATEGY.md) - Detailed
  routing configuration
- [API Client and Instance Configuration](API-CLIENT-AND-INSTANCE-CONFIGURATION.md) -
  Database schema details
- [REST Client File](../src/dms/tests/RestClient/multi-tenancy-setup.http) -
  Complete API request examples
