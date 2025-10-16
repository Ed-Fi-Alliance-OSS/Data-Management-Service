# Multi-Instance Route Qualifiers Testing Guide

This guide will help you set up and test the multi-instance DMS with route qualifiers.

## Prerequisites

- Docker and Docker Compose installed
- PowerShell 7+ installed
- VS Code with REST Client extension (or similar HTTP client)

## Step 1: Configure Environment for Multi-Instance

```powershell
# Navigate to docker-compose directory
cd eng/docker-compose

# Copy the environment template if you haven't already
cp .env.example .env

# Edit .env and set the ROUTE_QUALIFIER_SEGMENTS line:
# Find this section in .env:
#   # Multi-Instance Route Qualifiers (Optional)
#   #ROUTE_QUALIFIER_SEGMENTS=districtId,schoolYear
#
# Change to (uncomment the line):
#   ROUTE_QUALIFIER_SEGMENTS=districtId,schoolYear
```

## Step 2: Deploy DMS with Multi-Instance Configuration

```powershell
# Deploy with multi-instance configuration enabled
./start-local-dms.ps1 -EnableConfig -r
```

Wait for all services to start (check with `docker ps`).

## Step 3: Create Additional Databases

The main `.env` file creates the main database, but you need to create the additional databases for each instance:

```powershell
# Create databases
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2024;"
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255901_sy2025;"
docker exec -it dms-postgresql psql -U postgres -c "CREATE DATABASE edfi_datamanagementservice_d255902_sy2024;"
```

## Step 4: Deploy Schema to Each Database

You need to copy the DMS schema from the main database to each test database:

```powershell
# Export the schema from the main database (schema only, no data)
docker exec -i dms-postgresql pg_dump -U postgres -d edfi_datamanagementservice --schema-only > $env:TEMP\dms_schema.sql

# Apply schema to each test database
Get-Content $env:TEMP\dms_schema.sql | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024
Get-Content $env:TEMP\dms_schema.sql | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2025
Get-Content $env:TEMP\dms_schema.sql | docker exec -i dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255902_sy2024

# Verify schema was applied (should show 61 tables in dms schema)
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024 -c "SELECT COUNT(*) as table_count FROM information_schema.tables WHERE table_schema = 'dms';"
```

## Step 5: Restart DMS to Load Instance Configuration

**IMPORTANT**: The DMS loads instance configuration only at startup. After creating the instances and route contexts in the Configuration Service, you must restart the DMS container:

```powershell
# Restart DMS to load the new instances
docker restart docker-compose-dms-1

# Wait for it to start up (about 10-15 seconds)
# You can monitor the logs to confirm it loaded all instances:
docker logs docker-compose-dms-1 --tail 50

# Look for these log messages confirming successful load:
# "Successfully fetched 4 DMS instances"
# "Loaded DMS instance: ID=2, Name='District 255901 - School Year 2024'"
# etc.
```

## Step 6: Run the REST Client Tests

1. Open `src/dms/tests/RestClient/multi-instance-route-qualifiers.http` in VS Code
2. Execute the requests in order (they build on each other)
3. Follow the instructions in the comments

### Key Testing Steps

1. **Get Config Service Token** - Authenticates with the configuration service
2. **Create DMS Instances** - Sets up 3 instances with different route contexts:
   - Instance 1: District 255901, School Year 2024
   - Instance 2: District 255901, School Year 2025
   - Instance 3: District 255902, School Year 2024
3. **Create Application** - Creates an app associated with all instances
4. **Get DMS Token** - Authenticates with DMS using app credentials
5. **Test Routing** - Creates descriptors via different routes and verifies
   they go to the correct database

## Step 7: Verify Data Routing

You can verify that data is going to the correct database by checking the descriptors created:

```powershell
# Check District 255901, School Year 2024 (should have "District255901-2024")
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024 -c "SELECT * FROM dms.Descriptor WHERE CodeValue LIKE 'District%';"

# Check District 255901, School Year 2025 (should have "District255901-2025")
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2025 -c "SELECT * FROM dms.Descriptor WHERE CodeValue LIKE 'District%';"

# Check District 255902, School Year 2024 (should have "District255902-2024")
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255902_sy2024 -c "SELECT * FROM dms.Descriptor WHERE CodeValue LIKE 'District%';"
```

## Expected Behavior

When you make requests to:

- `/255901/2024/data/ed-fi/contentClassDescriptors` →
  Routes to `edfi_datamanagementservice_d255901_sy2024`
- `/255901/2025/data/ed-fi/contentClassDescriptors` →
  Routes to `edfi_datamanagementservice_d255901_sy2025`
- `/255902/2024/data/ed-fi/contentClassDescriptors` →
  Routes to `edfi_datamanagementservice_d255902_sy2024`

## Troubleshooting

### Route qualifiers not being parsed

- Check that `ROUTE_QUALIFIER_SEGMENTS` is set correctly in the `.env` file (comma-separated format)
- Verify the environment variable in the container: `docker exec docker-compose-dms-1 printenv | grep ROUTE`
- Verify the DMS logs: `docker logs docker-compose-dms-1`

### 404 - No database instance found or "No candidates found for the request path"

- **Most common cause**: DMS container needs to be restarted after creating instances in the Configuration Service (see Step 5)
- Verify DMS loaded all instances by checking the logs:
  ```powershell
  docker logs docker-compose-dms-1 | grep "Successfully fetched"
  # Should show: "Successfully fetched 4 DMS instances" (or your expected count)
  ```

- Verify route contexts are created correctly in the Configuration Service
- Check that the application is associated with the instances
- Verify the JWT token includes the correct `dms_instance_ids` claim

### Connection errors

- Ensure all databases are created
- Verify schema is deployed to each database
- Check PostgreSQL is running: `docker ps | grep postgresql`

### Check DMS logs

```powershell
docker logs docker-compose-dms-1 --follow
```

### Check Config Service logs

```powershell
docker logs dms-config-service --follow
```

## Cleanup

To tear down the environment:

```powershell
cd eng/docker-compose
pwsh teardown-local-dms.ps1
```
