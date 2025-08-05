# Integration Tests for Claims Data Loading

## Prerequisites

Before running the integration tests, ensure PostgreSQL is running and accessible:

### Option 1: Using Docker Compose (Recommended)

From the `eng/docker-compose` directory:

```bash
cd /home/brad/work/dms-root/Data-Management-Service/eng/docker-compose
./start-postgresql.ps1
```

### Option 2: Using Local PostgreSQL

Ensure PostgreSQL is running on:
- Host: localhost
- Port: 5432
- Database: edfi_configurationservice
- Username: postgres
- Password: (no password or default password)

### Option 3: Custom PostgreSQL Instance

Create an `appsettings.Test.json` file in this directory with your connection string:

```json
{
    "ConnectionStrings": {
        "DatabaseConnection": "host=your_host;port=5432;username=your_user;password=your_password;database=edfi_configurationservice;pooling=true;minimum pool size=10;maximum pool size=50;Application Name=EdFi.DmsConfigurationService"
    }
}
```

## Running the Tests

### From Command Line

```bash
# From the root directory
pwsh ./build-config.ps1 IntegrationTest

# Or directly with dotnet test
cd src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Test.Integration
dotnet test
```

### From Visual Studio / VS Code

Run the tests from the Test Explorer. Ensure the connection string is configured properly.

## Test Coverage

The integration tests cover:

1. **Empty Tables Detection**
   - Verifies the system correctly identifies when claims tables are empty

2. **Fresh Deployment**
   - Tests loading claims data into empty tables
   - Verifies all 16 claim sets are loaded
   - Confirms hierarchy data is loaded correctly

3. **Existing Data Scenarios**
   - Tests that data is not reloaded if it already exists
   - Ensures idempotent behavior

4. **Error Handling**
   - Tests validation failures
   - Tests handling of invalid claims data

5. **Data Integrity**
   - Verifies system reserved flags
   - Checks expected domains in hierarchy
   - Validates claim set counts

## Troubleshooting

### Connection Refused Errors

If you see "Failed to connect to 127.0.0.1:5432", ensure:
1. PostgreSQL is running
2. The port 5432 is not blocked
3. The connection string in appsettings.json is correct

### Database Not Found

The tests expect a database named `edfi_configurationservice`. Create it manually if needed:

```sql
CREATE DATABASE edfi_configurationservice;
```

### Permission Errors

Ensure the PostgreSQL user has sufficient permissions to:
- Create tables
- Insert/update/delete data
- Begin/commit transactions

## Test Data

The tests use the embedded `Claims.json` file from the Backend assembly. This file contains:
- 16 claim sets (all marked as system reserved)
- Complete claims hierarchy with multiple domains