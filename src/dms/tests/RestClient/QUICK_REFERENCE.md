# Multi-Instance Testing - Quick Reference

## Files Created

1. **`multi-instance-route-qualifiers.http`** - REST Client file with complete test scenario
2. **`multi-instance.env`** - Environment configuration for deployment
3. **`MULTI_INSTANCE_TESTING_GUIDE.md`** - Detailed setup instructions

## Quick Start

### 1. Deploy DMS

```powershell
cd eng/docker-compose
pwsh start-local-dms.ps1 -EnableConfig -EnableSwaggerUI -IdentityProvider self-contained -SearchEngine OpenSearch -EnvFile multi-instance.env
```

### 2. Create Test Databases

```powershell
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2024;"
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2025;"
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255902_sy2024;"
```

### 3. Deploy Schema to Databases

```powershell
# Get schema from main database
docker exec -it dms-postgresql pg_dump -U postgres -s edfi_datamanagementservice > schema.sql

# Apply to test databases
Get-Content schema.sql | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024
Get-Content schema.sql | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2025
Get-Content schema.sql | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255902_sy2024
```

### 4. Run Tests

Open `multi-instance-route-qualifiers.http` in VS Code and execute requests in order.

## Test Scenario

Creates 3 DMS instances:

- **Instance 1**: District 255901 + School Year 2024 → `edfi_datamanagementservice_d255901_sy2024`
- **Instance 2**: District 255901 + School Year 2025 → `edfi_datamanagementservice_d255901_sy2025`
- **Instance 3**: District 255902 + School Year 2024 → `edfi_datamanagementservice_d255902_sy2024`

## URL Format

```
/data/{districtId}/{schoolYear}/ed-fi/schools
```

Examples:

- `/data/255901/2024/ed-fi/schools` → Instance 1
- `/data/255901/2025/ed-fi/schools` → Instance 2
- `/data/255902/2024/ed-fi/schools` → Instance 3

## Configuration

Route qualifiers are configured in `multi-instance.env`:

```bash
APPSETTINGS__ROUTEQUALIFIERSEGMENTS__0=districtId
APPSETTINGS__ROUTEQUALIFIERSEGMENTS__1=schoolYear
```

## Verification

Check data in specific database:

```powershell
docker exec -it dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024 -c "SELECT * FROM dms.School;"
```

## Cleanup

```powershell
cd eng/docker-compose
pwsh teardown-local-dms.ps1
```

## Troubleshooting

**Check DMS logs:**

```powershell
docker logs dms-local --follow
```

**Check Config Service logs:**

```powershell
docker logs dms-config-service --follow
```

**Verify route qualifiers:**
Look for log messages showing route qualifier extraction when making requests.

**Common Issues:**

- 404 errors → Check route contexts are created and match URL pattern
- Connection errors → Verify databases exist and schema is deployed
- Token errors → Regenerate credentials and update in HTTP file
