# Ed-Fi Relational Flattening Performance Test

This repository contains a proof-of-concept implementation comparing Ed-Fi database design patterns: natural keys vs surrogate keys with compatibility views.

## Getting Started

### Prerequisites
- Docker
- PowerShell
- 7-Zip (for extracting database backup)

### Step 1: Download Database Backup

Download the Northridge test database backup file from:
https://odsassets.blob.core.windows.net/public/Northridge/EdFi_Ods_Northridge_v73_20250909_PG13.7z

Extract the 7z file to create the `EdFi_Ods_Northridge_v73_20250909_PG13` directory in the root of this repository.

### Step 2: Deploy Database Instances

Run the deployment script twice to create two PostgreSQL database instances:

```powershell
# Deploy original database (port 54330)
pwsh deploy-northridge.ps1 -DatabaseName "northridge-original" -Port "54330"

# Deploy flattened database (port 54331)
pwsh deploy-northridge.ps1 -DatabaseName "northridge-flattened" -Port "54331"
```

This will create:
- **northridge-original** (port 54330) - Original natural key implementation
- **northridge-flattened** (port 54331) - Surrogate key implementation

### Step 3: Apply Relational Flattening

Manually apply the SQL scripts to the flattened database instance:

1. Connect to the flattened database:
```bash
docker exec -it postgres-northridge-flattened psql -U postgres -d northridge-flattened
```

2. Apply the relational flattening DDL:
```sql
\i RelationalFlattening/northridge-relational-flattening-postgres.sql
```

3. Create compatibility views:
```sql
\i RelationalFlattening/create-all-views.sql
```

4. Exit the database connection:
```sql
\q
```

### Step 4: Run Performance Tests

Execute the performance comparison scripts. There are two sets of test runners available:

#### Standard Performance Tests

These run the original unoptimized queries:

```powershell
# Test original natural key approach
pwsh ./performance-test-original.ps1

# Test views-based surrogate key approach
pwsh ./performance-test-views.ps1

# Test direct joins surrogate key approach
pwsh ./performance-test-joins.ps1
```

#### Optimized Performance Tests

These run optimized versions that achieve **1.88x to 2.87x performance improvements**:

```powershell
# Test optimized natural key approach (1.88x faster)
pwsh ./performance-test-original-optimized.ps1

# Test optimized views-based approach (2.87x faster - recommended)
pwsh ./performance-test-views-optimized.ps1

# Test optimized direct joins approach (2.52x faster)
pwsh ./performance-test-joins-optimized.ps1
```

#### Simplified Performance Tests

The simplified tests are a small subset of the joins from the original query used to amplify variations between the three query approaches, and are not intended to be representative of the typical ETL query.

```powershell
# Simple test for original approach
pwsh ./performance-test-simple-original.ps1

# Simple test for views approach
pwsh ./performance-test-simple-views.ps1

# Simple test for joins approach
pwsh ./performance-test-simple-joins.ps1
```

#### Analyze Performance Results

After running tests, analyze the results using:

```powershell
# Generate comprehensive performance analysis
pwsh ./analyze-performance-results.ps1
```

This will compare all test results and generate detailed performance reports.

### Results

Performance results will be displayed in the console and saved to JSON files. Each test runs the ETL function 10 times and reports average execution time.

## Database Connections

- **Original Database**: `localhost:54330/northridge-original`
- **Flattened Database**: `localhost:54331/northridge-flattened`
- **Username**: `postgres`
- **Password**: `password123`

## Additional Information

For detailed analysis of the implementation approaches and performance results, see [SUMMARY.md](SUMMARY.md).
