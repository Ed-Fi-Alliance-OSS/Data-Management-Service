# Ed-Fi Instance Management E2E Tests

This project contains End-to-End tests for the Data Management Service instance management and route segment functionality, focusing on data segregation among instances.

## Purpose

These tests verify that:

- Multiple instances can be configured with route qualifiers (e.g., districtId, schoolYear)
- Data is properly segregated between instances
- Route segments correctly isolate data access
- Instance context is properly maintained throughout request processing
- Error handling for invalid route qualifiers

## Test Implementation

The tests are based on the workflow defined in `src/dms/tests/RestClient/multi-instance-route-qualifiers.http` and implement:

1. **Instance Setup**: Creating vendors, instances with route contexts, and applications via Configuration Service API
2. **Route Qualifier Segregation**: Testing data isolation across different instance/route combinations
3. **Error Handling**: Verifying appropriate error responses for invalid route qualifiers

## Prerequisites

- Docker Desktop running
- PowerShell Core (pwsh) 7.0 or higher
- .NET 8.0 SDK

## Running the Tests

**IMPORTANT**: These tests require a comprehensive setup involving Docker containers, database configuration, and route qualifier settings. Always use the build script from the repository root:

```powershell
./build-dms.ps1 InstanceE2ETest -Configuration Release
```

This script handles:
- Docker environment setup with proper configuration
- Building the DMS and Configuration Service
- Running the tests
- Cleanup of the Docker environment

**Do not attempt to run these tests directly with `dotnet test`** - the setup is too complex and requires specific environment configuration that is managed by the build script.

## Test Structure

Tests are organized using Reqnroll (SpecFlow successor) with Gherkin feature files:

### Features

- `Features/InstanceManagement/InstanceSetup.feature` - Vendor, instance, and application creation
- `Features/InstanceManagement/RouteQualifierSegregation.feature` - Data isolation testing
- `Features/InstanceManagement/RouteQualifierErrors.feature` - Error handling validation
- `Features/InstanceManagement/KafkaTopicPerInstance.feature` - Kafka topic-per-instance segregation testing

### Step Definitions

- `StepDefinitions/InstanceSetupStepDefinitions.cs` - Instance setup steps
- `StepDefinitions/RouteQualifierStepDefinitions.cs` - Data segregation steps
- `StepDefinitions/ErrorHandlingStepDefinitions.cs` - Error handling steps
- `StepDefinitions/InstanceKafkaStepDefinitions.cs` - Kafka messaging and topic isolation steps

### Management (Test Infrastructure)

- `Management/ConfigServiceClient.cs` - Configuration Service API client
- `Management/DmsApiClient.cs` - DMS API client with route qualifier support
- `Management/TokenHelper.cs` - Authentication token management
- `Management/InstanceManagementContext.cs` - Test data tracking across scenarios
- `Management/TestConfiguration.cs` - Test configuration constants
- `Management/InstanceKafkaMessageCollector.cs` - Kafka message collection for multi-instance topics
- `Management/InstanceKafkaTestConfiguration.cs` - Kafka test configuration
- `Management/KafkaTestMessage.cs` - Kafka message model with instance tracking
- `Management/KafkaTopicHelper.cs` - Utilities for topic naming and isolation validation

### Models

- Request/Response models for Configuration Service APIs (Vendor, Instance, RouteContext, Application)

### Hooks

- `Hooks/SetupHooks.cs` - Test run initialization and logging
- `Hooks/InstanceManagementCleanupHooks.cs` - Cleanup after each scenario

## Configuration

### Test Configuration (`appsettings.json`)

- `QueryHandler` - Database type (postgresql)
- `AuthenticationService` - URL for authentication endpoint
- `EnableClaimsetReload` - Whether to reload claimsets during tests
- `Kafka.BootstrapServers` - Kafka broker address (default: localhost:9092)
- `Kafka.Enabled` - Enable/disable Kafka testing
- `Kafka.TopicPrefix` - Topic prefix for instance topics (default: edfi.dms)
- `Kafka.TopicSuffix` - Topic suffix for instance topics (default: document)

### Environment Configuration (`.env.routeContext.e2e`)

Key setting: `ROUTE_QUALIFIER_SEGMENTS=districtId,schoolYear`

This enables route-based instance resolution in DMS.

## Test Data

Tests create the following instances:

- Instance 1: District 255901, School Year 2024 → Database: `edfi_datamanagementservice_d255901_sy2024`
- Instance 2: District 255901, School Year 2025 → Database: `edfi_datamanagementservice_d255901_sy2025`
- Instance 3: District 255902, School Year 2024 → Database: `edfi_datamanagementservice_d255902_sy2024`

URL Pattern: `http://localhost:8080/{districtId}/{schoolYear}/data/ed-fi/{resource}`

Example: `http://localhost:8080/255901/2024/data/ed-fi/contentClassDescriptors`

## Cleanup

Tests tagged with `@InstanceCleanup` automatically clean up:

- Applications
- Instances (including route contexts)
- Vendors

Cleanup is performed after each scenario to ensure test isolation.

## Important Notes

- The build script handles all setup automatically
- Tests interact with Docker containers, so Docker must be running
- Route qualifiers must be enabled in the environment configuration (handled by build script)
- Check Docker logs if tests fail: `docker logs <container-name>`
- Tests use self-contained identity provider (no Keycloak required)

## Kafka Topic-Per-Instance Testing

### Overview

The Kafka tests validate that DMS instances publish messages to instance-specific topics and that no cross-instance data leakage occurs. This is critical for FERPA compliance and multi-tenant data isolation.

### Topic Naming Convention

Each DMS instance publishes to its own topic:
- Format: `edfi.dms.{instanceId}.document`
- Example: Instance with ID 123 → `edfi.dms.123.document`

### Prerequisites for Kafka Tests

1. **Kafka must be running** in the Docker stack
2. **Hosts file entry required**: Add `127.0.0.1 dms-kafka1` to your hosts file
   - Windows: `C:\Windows\System32\drivers\etc\hosts`
   - Linux/Mac: `/etc/hosts`
3. **Debezium connectors** must be configured for each instance with topic-per-instance routing

### Running Kafka Tests

Kafka tests are tagged with `@kafka`:

```powershell
# Run all Kafka tests
dotnet test --filter "TestCategory=kafka"

# Run specific Kafka scenario
dotnet test --filter "FullyQualifiedName~KafkaTopicPerInstance"
```

**Note:** Kafka tests should be run via the build script which ensures proper infrastructure setup.

### What Kafka Tests Validate

1. **Correct Topic Publishing**: Messages appear on the correct instance-specific topic
2. **No Cross-Instance Leakage**: Messages from one instance don't appear in other instance topics
3. **Message Content**: Kafka messages contain expected data with correct flags
4. **Delete Operations**: Deleted records have `__deleted: true` flag
5. **Multiple Instance Isolation**: All instances maintain proper segregation

### Kafka Test Infrastructure

**Message Collection:**
- `InstanceKafkaMessageCollector` subscribes to multiple instance topics
- Collects messages in background thread during test execution
- Provides filtering and querying capabilities

**Validation:**
- `KafkaTopicHelper` validates topic isolation
- Detects cross-instance message leakage
- Groups messages by instance for analysis

**Configuration:**
- `KAFKA_BOOTSTRAP_SERVERS` environment variable (default: localhost:9092)
- Consumer groups are unique per test run to avoid conflicts

### Debezium Configuration

For topic-per-instance architecture, each DMS instance needs its own Debezium connector or a routing transformation:

```json
{
  "name": "postgresql-connector-instance-{instanceId}",
  "config": {
    "connector.class": "io.debezium.connector.postgresql.PostgresConnector",
    "topic.prefix": "edfi.dms.{instanceId}",
    "database.dbname": "instance_{instanceId}_db",
    ...
  }
}
```

**Alternative:** Use Debezium SMT (Single Message Transform) for dynamic topic routing based on instance metadata.

## Troubleshooting

**Tests fail with 404 for all requests:**

- Re-run the build script: `./build-dms.ps1 InstanceE2ETest -Configuration Release`
- The build script ensures route qualifiers are properly configured

**Setup issues:**

- Re-run the build script - it handles full setup
- Check Configuration Service logs: `docker logs dms-config-service`
- Check DMS logs: `docker logs dms-dms-1`

**Instance creation fails:**

- Verify PostgreSQL is running: `docker ps | grep postgresql`
- Check database connection string in test data
- Re-run the build script to reset the environment

**Kafka tests failing:**

- Verify Kafka is running: `docker ps | grep kafka`
- Check Kafka connectivity: `docker logs dms-kafka1`
- Ensure hosts file has entry: `127.0.0.1 dms-kafka1`
- Verify Debezium connectors are running: `curl http://localhost:8083/connectors`
- Check Debezium connector logs for errors
- Validate topic naming matches expected pattern
- Use Kafka UI (if available) to inspect topics and messages: `http://localhost:8088`

**Kafka consumer not receiving messages:**

- Check if Debezium connectors are properly configured
- Verify database has logical replication enabled
- Check if topics exist: `docker exec dms-kafka1 kafka-topics --list --bootstrap-server localhost:9092`
- Increase wait time in tests if CDC has high latency
- Review Kafka message collector diagnostics in test output
