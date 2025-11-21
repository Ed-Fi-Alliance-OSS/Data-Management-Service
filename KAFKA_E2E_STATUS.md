# Kafka E2E Test Status Report

**Date**: 2025-11-21
**Branch**: DMS-874-bugfix
**Status**: 🔧 **INFRASTRUCTURE DUPLICATION FIXED**

---

## Executive Summary

### Latest Issue (2025-11-21 12:25 CST) - TOPIC CREATION FIX

The Kafka E2E tests are failing because **instance-specific Kafka topics are not being created** during infrastructure setup.

**Root Cause**: Debezium connectors only create topics when they publish their first message. Since the instance databases are empty (no records yet), the initial snapshot completes without publishing any messages, leaving the topics uncreated. When the test consumer tries to subscribe to `edfi.dms.1.document`, `edfi.dms.2.document`, and `edfi.dms.3.document`, they don't exist yet, causing:
```
[ERR] Kafka consume error: Subscribed topic not available: edfi.dms.1.document: Broker: Unknown topic or partition
```

**Fix Applied**: Added explicit topic creation in `start-local-dms.ps1` (lines 267-274) using `kafka-topics.sh --create --if-not-exists` immediately after connector setup.

---

### Previous Issues (Now Resolved)

The Kafka E2E tests were previously failing due to **duplicate infrastructure setup code** causing confusing errors and wasted setup time.

The root cause was NOT in the test validation logic (that was fixed previously), but in the **build orchestration** - `build-dms.ps1` was duplicating work already done by `start-local-dms.ps1`.

**Fixed**: Removed duplicate database creation, schema application, and Kafka connector setup from `build-dms.ps1`.

---

## Issue 4: False Positive in Cross-Instance Validation (2025-11-21)

### Problem Description

2 tests were failing with false positives in cross-instance message leakage detection:
- `_04NoCross_InstanceDataLeakageInKafkaTopics`
- `_07ComprehensiveIsolationValidationAcrossAllInstances`

Error: `Expected isIsolated to be True because All instances should be properly isolated with no cross-instance message leakage, but found False.`

### Root Cause

The validation logic in `KafkaTopicHelper.cs` was checking for instance markers using a **two-part string match**:
```csharp
// OLD CODE - Caused false positives
{ 2, ["255901", "2025"] },  // Instance 2: District 255901 - School Year 2025
return Array.TrueForAll(markers, marker => messageContent.Contains(marker));
```

**The False Positive:**
- Instance 1 messages (255901/2024) correctly went to topic `edfi.dms.1.document`
- But Kafka message metadata includes timestamps like `"2025-11-21 12:57:56.408 UTC"` (current year)
- Validation checked if message contained "255901" ✓ AND "2025" ✓
- The "2025" in the **timestamp** triggered a false match for instance 2 data!

From test logs:
```
[06:58:18 WRN] Message found on topic edfi.dms.1.document (instance 1) that appears to contain data for instance 2
Message timing - Arrived: 2025-11-21 12:57:56.408 UTC
```

### The Fix

**File**: `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Management/KafkaTopicHelper.cs`
**Lines**: 112-135

Changed from multi-part marker matching to **complete pattern matching**:

```csharp
// NEW CODE - Uses complete pattern
var instanceDataMarkers = new Dictionary<long, string>
{
    { 1, "District255901-2024" },  // Instance 1
    { 2, "District255901-2025" },  // Instance 2
    { 3, "District255902-2024" }   // Instance 3
};
return messageContent.Contains(marker, StringComparison.OrdinalIgnoreCase);
```

**Why this works:**
- Looks for complete business data pattern: `"District255901-2024"`
- Appears in the `codeValue` field of test messages
- Won't match timestamp fields containing "2025"
- More precise and avoids metadata contamination

### Verification

The fix ensures:
1. ✓ Messages with "District255901-2024" are identified as instance 1 data
2. ✓ Timestamp fields with "2025-11-21" don't cause false positives
3. ✓ True cross-instance leakage would still be detected (if it occurred)

---

## Issue 3: Missing Kafka Topic Creation (2025-11-21)

### Problem Description

All 4 Kafka E2E tests were failing with the same root cause: **Kafka topics for instances do not exist when tests try to consume from them**.

### Symptoms

1. **Test Failure Pattern**: All tests waiting for Kafka messages fail after 15-second timeout
   ```
   Expected messages not to be empty because Expected to receive at least one message for instance 1 (route: 255901/2024) within 15 seconds.
   ```

2. **Kafka Consumer Errors** (from test logs):
   ```
   [ERR] Kafka consume error: Subscribed topic not available: edfi.dms.1.document: Broker: Unknown topic or partition
   ```

3. **Consumer Diagnostics** show no assigned partitions:
   ```
   Messages collected: 0
   Assigned partitions: 0
   ```

4. **Failed Tests**:
   - `_02MessagesPublishedToCorrectInstance_SpecificTopics`
   - `_03MultipleInstancesPublishToSeparateTopics`
   - `_04NoCross_InstanceDataLeakageInKafkaTopics`
   - `_07ComprehensiveIsolationValidationAcrossAllInstances`

### Root Cause Analysis

**Why topics don't exist:**

1. Infrastructure setup successfully creates Debezium connectors for all 3 instances
2. Connector configuration uses `"snapshot.mode": "initial"` to capture existing data
3. Instance databases are empty (no records in `dms.document` table)
4. Debezium completes initial snapshot without finding any data to publish
5. **Debezium only creates Kafka topics when it publishes messages**
6. No messages = no topics created
7. Test consumer tries to subscribe to `edfi.dms.1.document`, `edfi.dms.2.document`, `edfi.dms.3.document`
8. Topics don't exist → subscription fails → tests fail

**Why this wasn't caught before:**

The connectors were working correctly and running (`RUNNING` status), but they hadn't created topics yet because there was no data to publish.

### The Fix

**File**: `eng/docker-compose/start-local-dms.ps1`
**Lines**: 267-274 (new code added)

Added explicit topic creation immediately after connector setup:

```powershell
# Explicitly create Kafka topics for each instance
# This is required because Debezium only creates topics when publishing messages,
# and empty databases won't trigger topic creation during initial snapshot
Write-Output "Creating Kafka topics for instances..."
docker exec dms-kafka1 /opt/kafka/bin/kafka-topics.sh --create --if-not-exists --topic edfi.dms.1.document --bootstrap-server localhost:9092 --partitions 1 --replication-factor 1 2>$null
docker exec dms-kafka1 /opt/kafka/bin/kafka-topics.sh --create --if-not-exists --topic edfi.dms.2.document --bootstrap-server localhost:9092 --partitions 1 --replication-factor 1 2>$null
docker exec dms-kafka1 /opt/kafka/bin/kafka-topics.sh --create --if-not-exists --topic edfi.dms.3.document --bootstrap-server localhost:9092 --partitions 1 --replication-factor 1 2>$null
Write-Output "Kafka topics created"
```

**Key aspects of the fix:**

1. **`--if-not-exists` flag**: Makes the command idempotent - won't fail if topics already exist
2. **`--partitions 1`**: Matches Kafka default configuration (single partition per topic)
3. **`--replication-factor 1`**: Single broker setup, so replication factor of 1
4. **`2>$null`**: Suppresses stderr to avoid noise from "topic already exists" warnings
5. **Timing**: Topics created immediately after connectors, before tests start

### Verification Steps

After applying the fix, verify topics exist:

```bash
docker exec dms-kafka1 /opt/kafka/bin/kafka-topics.sh --list --bootstrap-server localhost:9092 | grep "edfi.dms"
```

**Expected output**:
```
edfi.dms.1.document
edfi.dms.2.document
edfi.dms.3.document
```

### Diagnostic Process

1. Ran `./build-dms.ps1 InstanceE2ETest -Configuration Release`
2. Tests failed with 4 failures
3. Examined test results file: `TestResults\EdFi.InstanceManagement.Tests.E2E.trx`
4. Found error: `Kafka consume error: Subscribed topic not available: edfi.dms.1.document`
5. Verified connectors were running: `curl http://localhost:8083/connectors/postgresql-source-instance-1/status`
6. Listed Kafka topics: `docker exec dms-kafka1 /opt/kafka/bin/kafka-topics.sh --list --bootstrap-server localhost:9092`
7. Confirmed: Topics `edfi.dms.1.document`, `edfi.dms.2.document`, `edfi.dms.3.document` **do not exist**
8. Reviewed Debezium connector template (`instance_connector_template.json`)
9. Understood: Debezium with `snapshot.mode=initial` only creates topics when publishing messages
10. Solution: Add explicit topic creation to infrastructure setup

---

## The Actual Problem: Duplicate Setup Code

### What Was Happening

When running `./build-dms.ps1 InstanceE2ETest`, the execution flow was:

```
./build-dms.ps1 InstanceE2ETest
  ↓
setup-local-dms.ps1 (wrapper in E2E project)
  ↓
eng/docker-compose/start-local-dms.ps1 -AddDmsInstance
  ├─ Creates 3 DMS instance records in Config Service ✓
  ├─ Creates 3 PostgreSQL databases ✓
  ├─ Runs migrations (creates DMS schema) ✓
  ├─ Sets up 3 Kafka connectors ✓
  └─ Debezium auto-creates publications ✓

THEN build-dms.ps1 DUPLICATED everything:
  ├─ Setup-InstanceManagementDatabases()
  │   ├─ CREATE DATABASE (fails silently - already exists)
  │   ├─ pg_dump + psql to copy schema (ERROR: relations already exist)
  │   └─ CREATE PUBLICATION (redundant)
  └─ Setup-InstanceKafkaConnectors()
      └─ Deletes and recreates connectors (unnecessary)
```

### Observed Symptoms

1. **Confusing 400 Bad Request errors**: "Failed to create DMS test instances: Response status code does not indicate success: 400 (Bad Request)"
   - This happened when trying to create instances that already existed
   - Caught by try/catch, shown as warning, but confusing

2. **Hundreds of "relation already exists" errors in stderr**:
   ```
   ERROR: relation "alias" already exists
   ERROR: relation "document" already exists
   ERROR: multiple primary keys for table "document" are not allowed
   ```
   - These were from trying to apply schema dump over existing schema
   - Harmless but noisy and confusing

3. **Unnecessary connector deletion and recreation**
   - Connectors were working fine but got deleted and recreated
   - Added 30+ seconds to setup time

---

## The Fix

**File**: `build-dms.ps1`
**Lines Removed**: 476-528 (Setup-InstanceManagementDatabases), 562-627 (Setup-InstanceKafkaConnectors)

### Changes Made

1. **Deleted `Setup-InstanceManagementDatabases()` function**
   - This was creating databases and applying schema via pg_dump
   - Completely redundant since `start-local-dms.ps1 -AddDmsInstance` already runs migrations

2. **Deleted `Setup-InstanceKafkaConnectors()` function**
   - This was setting up connectors that were already created
   - Unnecessary duplication

3. **Simplified `InstanceE2ETests()` function**
   - Removed calls to deleted functions
   - Now just: setup → wait for services → run tests
   - Added informational output showing what infrastructure was created

### New Clean Execution Flow

```
./build-dms.ps1 InstanceE2ETest
  ↓
setup-local-dms.ps1
  ↓
start-local-dms.ps1 -AddDmsInstance
  ├─ Creates ALL infrastructure (instances, databases, schema, connectors)
  ↓
Wait-ForConfigServiceAndClients
  ↓
Restart-DmsContainer
  ↓
RunInstanceE2E (actual tests)
```

---

## What Infrastructure Is Created

**By start-local-dms.ps1 with -AddDmsInstance flag:**

### 1. DMS Instance Records (Config Service)
- Instance 1: District 255901 - School Year 2024
- Instance 2: District 255901 - School Year 2025
- Instance 3: District 255902 - School Year 2024

### 2. PostgreSQL Databases with DMS Schema
- `edfi_datamanagementservice_d255901_sy2024`
- `edfi_datamanagementservice_d255901_sy2025`
- `edfi_datamanagementservice_d255902_sy2024`

Schema created via: `dotnet /app/Installer/EdFi.DataManagementService.Backend.Installer.dll`

### 3. PostgreSQL Publications (Auto-created by Debezium)
- `to_debezium_instance_1` (in d255901_sy2024 database)
- `to_debezium_instance_2` (in d255901_sy2025 database)
- `to_debezium_instance_3` (in d255902_sy2024 database)

Created automatically due to `"publication.autocreate.mode": "filtered"` in connector template.

### 4. Kafka Connectors
- `postgresql-source-instance-1` → topic: `edfi.dms.1.document`
- `postgresql-source-instance-2` → topic: `edfi.dms.2.document`
- `postgresql-source-instance-3` → topic: `edfi.dms.3.document`

Created via: `setup-instance-kafka-connectors.ps1`

### 5. Route Contexts (for instance routing)
- Instance 1: `districtId=255901, schoolYear=2024`
- Instance 2: `districtId=255901, schoolYear=2025`
- Instance 3: `districtId=255902, schoolYear=2024`

---

## Test Validation Logic

**File**: `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Management/KafkaTopicHelper.cs`
**Method**: `ContainsInstanceSpecificData` (lines 112-136)

The test validation was previously fixed to use **instance-specific test data markers** instead of naive string matching:

```csharp
private static bool ContainsInstanceSpecificData(KafkaTestMessage message, long instanceId)
{
    // Map instance IDs to their unique test data identifiers
    var instanceDataMarkers = new Dictionary<long, string[]>
    {
        { 1, ["255901", "2024"] },  // Instance 1: District 255901 - School Year 2024
        { 2, ["255901", "2025"] },  // Instance 2: District 255901 - School Year 2025
        { 3, ["255902", "2024"] }   // Instance 3: District 255902 - School Year 2024
    };

    if (!instanceDataMarkers.TryGetValue(instanceId, out var markers))
    {
        return false;
    }

    var messageContent = message.Value ?? string.Empty;

    // Requires BOTH districtId AND schoolYear to match
    return Array.TrueForAll(markers, marker => messageContent.Contains(marker));
}
```

This fixes the previous issue where naive string matching (`message.Contains("1")`) caused false positives.

---

## Files Modified in This Fix

### 1. `build-dms.ps1`
**Lines Removed**:
- 476-528: Setup-InstanceManagementDatabases() function
- 562-627: Setup-InstanceKafkaConnectors() function

**Lines Modified**:
- 644-683: InstanceE2ETests() function simplified
- Removed duplicate setup calls
- Added informational output
- Enabled test execution (uncommented RunInstanceE2E)

---

## Verification Steps

To verify the clean setup:

### 1. Check Instance Records
```bash
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice -c "SELECT id, instancename FROM dmscs.dmsinstance;"
```
**Expected**: 3 rows with IDs 1, 2, 3

### 2. Check Databases Exist
```bash
docker exec dms-postgresql psql -U postgres -c "\l" | grep edfi_datamanagementservice
```
**Expected**: 4 databases (main + 3 instances)

### 3. Check Schema in Instance Database
```bash
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024 -c "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'dms';"
```
**Expected**: 61 tables

### 4. Check Publications
```bash
docker exec dms-postgresql psql -U postgres -d edfi_datamanagementservice_d255901_sy2024 -c "SELECT pubname FROM pg_publication;"
```
**Expected**: `to_debezium_instance_1`

### 5. Check Kafka Connectors
```bash
curl -s http://localhost:8083/connectors
```
**Expected**: `["postgresql-source","postgresql-source-instance-1","postgresql-source-instance-2","postgresql-source-instance-3"]`

### 6. Check Connector Status
```bash
curl -s http://localhost:8083/connectors/postgresql-source-instance-1/status | jq '.connector.state'
```
**Expected**: `"RUNNING"`

---

## Running the Tests

### Clean Run (Recommended)

1. Teardown existing environment:
   ```bash
   cd src/dms/tests/EdFi.InstanceManagement.Tests.E2E
   pwsh ./teardown-local-dms.ps1
   ```

2. Run tests from repository root:
   ```bash
   cd C:\Tanager\Data-Management-Service
   pwsh ./build-dms.ps1 InstanceE2ETest -Configuration Release
   ```

### Manual Infrastructure Setup (for testing/debugging)

1. Clean environment:
   ```bash
   cd eng/docker-compose
   pwsh ./start-local-dms.ps1 -d -v -EnvironmentFile ./.env.routeContext.e2e
   ```

2. Setup infrastructure:
   ```bash
   pwsh ./start-local-dms.ps1 -EnableKafkaUI -EnableConfig -EnvironmentFile ./.env.routeContext.e2e -r -AddExtensionSecurityMetadata -IdentityProvider self-contained -AddDmsInstance
   ```

3. Run tests manually:
   ```bash
   cd ../../src/dms/tests/EdFi.InstanceManagement.Tests.E2E
   dotnet test --configuration Release --logger "console" --verbosity normal
   ```

---

## Previous Issues (Now Resolved)

### ❌ Issue 1: Test Validation Using Naive String Matching
**Status**: ✅ Fixed in previous commit
**Fix**: Changed from `message.Contains(instanceId.ToString())` to instance-specific data markers

### ❌ Issue 2: Duplicate Infrastructure Setup
**Status**: ✅ Fixed in this commit
**Fix**: Removed duplicate functions from build-dms.ps1

### ❌ Issue 3: Confusing Error Messages
**Status**: ✅ Fixed (eliminated duplicate setup that caused errors)

---

## Architecture Notes

### Topic-Per-Instance Design

Each DMS instance has complete isolation:

1. **Separate Database**: Own PostgreSQL database with DMS schema
2. **Separate Publication**: PostgreSQL publication for CDC
3. **Separate Connector**: Debezium connector watching that database
4. **Separate Topic**: Kafka topic with instance-specific prefix
5. **Route Context**: DistrictId + SchoolYear for request routing

### Data Flow

```
User Request
  ↓
DMS Frontend (analyzes route qualifiers)
  ↓
DmsConnectionStringProvider (queries Config Service for connection string)
  ↓
Instance-Specific Database (e.g., d255901_sy2024)
  ↓
PostgreSQL Publication (to_debezium_instance_1)
  ↓
Debezium Connector (postgresql-source-instance-1)
  ↓
Instance-Specific Kafka Topic (edfi.dms.1.document)
```

---

## Docker Containers

```
dms-postgresql         - PostgreSQL 16 (port 5435)
dms-kafka1            - Kafka broker (port 9092)
kafka-postgresql-source - Kafka Connect with Debezium (port 8083)
kafka-ui              - Kafka UI (port 8084)
dms-config-service    - DMS Configuration Service (port 8081)
dms-local-dms-1       - DMS API (port 8080)
```

---

## References

**Setup Scripts**:
- `build-dms.ps1` (lines 529-565) - Main build orchestration
- `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/setup-local-dms.ps1` - E2E wrapper
- `eng/docker-compose/start-local-dms.ps1` (lines 208-269) - Infrastructure creation with -AddDmsInstance
- `eng/docker-compose/setup-instance-kafka-connectors.ps1` - Kafka connector setup

**Test Implementation**:
- `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Features/InstanceKafkaMessaging.feature` - Gherkin scenarios
- `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/StepDefinitions/InstanceKafkaStepDefinitions.cs` - Test steps
- `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Management/KafkaTopicHelper.cs` - Validation logic

**Configuration**:
- `eng/docker-compose/.env.routeContext.e2e` - E2E environment settings
- `eng/docker-compose/instance_connector_template.json` - Debezium connector template

---

## Lessons Learned

### 1. DRY Principle Applies to Infrastructure Code
- Don't duplicate infrastructure setup across scripts
- One authoritative source for setup logic (start-local-dms.ps1)
- Build orchestration should call setup, not duplicate it

### 2. Confusing Errors Often Hide Simple Problems
- The 400 errors and schema errors made it seem complex
- The real problem was simple: doing the same work twice
- Trace execution flow completely before assuming infrastructure failure

### 3. Test What You Document
- Document claimed instances weren't created - they were
- Document claimed PascalCase/camelCase issues - never happened
- Always verify assumptions with direct queries

### 4. Wrapper Scripts Should Be Thin
- setup-local-dms.ps1 in E2E project should just call the real script
- Don't add logic to wrappers that duplicates the wrapped script

---

**Last Updated**: 2025-11-21 13:15 CST
**Tests Status**: ✅ **ALL FIXES APPLIED - READY FOR TESTING**
**Branch**: DMS-874-bugfix
