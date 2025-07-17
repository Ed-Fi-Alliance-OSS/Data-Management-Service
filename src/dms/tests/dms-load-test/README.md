# Ed-Fi DMS Load Testing Tool

A comprehensive load testing tool for the Ed-Fi Data Management Service API using Grafana k6. This tool simulates realistic educational data patterns at Austin ISD scale (~75,000 students, 130 schools, 12,000 staff).

## Features

- **OAuth2 Authentication**: Automatic token management with refresh
- **Dependency Resolution**: Respects Ed-Fi resource dependencies for correct data creation order
- **Realistic Data Generation**: Uses faker.js to generate authentic education data
- **Multi-Phase Testing**:
  - Smoke Test: Basic connectivity and API validation
  - Load Phase: POST-only operations following dependency order
  - Read-Write Phase: Mixed CRUD operations (50% GET, 20% POST, 20% PUT, 10% DELETE)
- **Domain Focus**: Prioritizes 5 key Ed-Fi domains:
  - Enrollment
  - Student Academic Record
  - Teaching and Learning
  - Assessment
  - Student Identification and Demographics
- **Configurable Endpoints**: Support for both cloud and local DMS instances
- **Performance Metrics**: Built-in k6 metrics plus custom CRUD operation tracking

## Prerequisites

- [k6](https://k6.io/docs/getting-started/installation/) installed
- Node.js 18+ (for data generation utilities)
- Access to Ed-Fi API (either cloud or local instance)

## Installation

```bash
cd src/dms/test/dms-load-test
npm install
```

## Configuration

Copy `.env.example` to `.env` and configure:

```bash
cp .env.example .env
```

### Environment Variables

```bash
# API Configuration
API_BASE_URL=https://api.ed-fi.org/v7.3/api  # or http://localhost:8080/api
OAUTH_TOKEN_URL=https://api.ed-fi.org/v7.3/api/oauth/token
CLIENT_ID=your_client_id
CLIENT_SECRET=your_client_secret

# Test Scale (Austin ISD defaults)
SCHOOL_COUNT=130
STUDENT_COUNT=75000
STAFF_COUNT=12000
COURSES_PER_SCHOOL=50
SECTIONS_PER_COURSE=4

# Load Test Configuration
VUS_LOAD_PHASE=100      # Virtual users for load phase
VUS_READWRITE_PHASE=50   # Virtual users for read-write phase
DURATION_LOAD_PHASE=30m  # Load phase duration
DURATION_READWRITE_PHASE=20m  # Read-write phase duration
```

## Usage

### Quick Start with Test Runner (Recommended)

```bash
# Run a smoke test (minimal configuration)
./run-load-test.sh --profile smoke

# Run a development-scale test
./run-load-test.sh --profile dev

# Run a production-scale test
./run-load-test.sh --profile prod

# See all options
./run-load-test.sh --help
```

See the [How to Use Guide](docs/how-to-use-load-test-runner.md) for detailed instructions.

### Run Individual Test Scenarios

```bash
# Smoke test - verify API connectivity
./run-smoke-test.sh

# Load phase only
./run-load-test.sh --phase load

# Read-write phase only
./run-load-test.sh --phase readwrite
```

### Direct k6 Execution (Advanced)

```bash
# Run scenarios directly with k6
k6 run src/scenarios/smoke.js
k6 run src/scenarios/load.js
k6 run src/scenarios/readwrite.js
```

### Running Against Local DMS

1. Start your local DMS instance:
```bash
cd Data-Management-Service/eng/docker-compose
./start-local-dms.ps1 -EnableConfig
```

2. Update `.env` with local settings:
```bash
API_BASE_URL=http://localhost:8080/api
OAUTH_TOKEN_URL=http://localhost:8080/api/oauth/token
CLIENT_ID=local_client_id
CLIENT_SECRET=local_client_secret
```

3. Run tests:
```bash
k6 run src/scenarios/smoke.js
```

## Test Scenarios

### Smoke Test
- Duration: 30 seconds
- VUs: 1
- Operations: Basic CRUD on descriptors, API metadata validation
- Purpose: Verify API connectivity and basic functionality

### Load Phase
- Duration: 30 minutes (configurable)
- VUs: 100 (ramps up gradually)
- Operations: POST-only, following dependency order
- Creates: 10% of total configured resources
- Purpose: Simulate initial data population

### Read-Write Phase
- Duration: 20 minutes (configurable)
- VUs: 50 (constant)
- Operations: 
  - 50% GET (read existing resources)
  - 20% POST (create new resources)
  - 20% PUT (update existing resources)
  - 10% DELETE (remove resources safely)
- Purpose: Simulate normal API usage patterns

## Performance Thresholds

Default thresholds (adjustable in test files):
- 95th percentile response time < 5s (load phase)
- 95th percentile response time < 3s (read-write phase)
- Error rate < 5%
- Minimum 10,000 CRUD operations (read-write phase)

## Data Generation

The tool generates realistic education data including:
- Local Education Agencies (Districts)
- Schools with appropriate grade levels
- Students with demographics and enrollment
- Parents/guardians with relationships
- Staff with qualifications and assignments
- Courses, sections, and enrollments
- Grades and assessments
- Calendar and scheduling data

## Output and Reporting

k6 provides detailed metrics including:
- Request duration (min, max, avg, p90, p95)
- Request rate
- Error rate
- Custom metrics for CRUD operations
- VU utilization

Results are displayed in the console and can be exported to various formats.

## Troubleshooting

### Authentication Failures
- Verify CLIENT_ID and CLIENT_SECRET are correct
- Check token URL matches your API instance
- Ensure client has appropriate permissions

#### Authorization (403) Errors
If you encounter 403 Forbidden errors, the client likely lacks proper claim sets. The load test tool includes a setup script that creates a properly authorized client:

```bash
# Automatically creates a client with E2E-NoFurtherAuthRequiredClaimSet
./run-smoke-test.sh  # Will set up client if .env.load-test doesn't exist

# Or manually run the setup
node src/utils/setupLoadTestClient.js

# To clean up the configuration
./clean-load-test.sh
```

The setup script:
1. Creates a sys-admin client
2. Uses it to create a vendor via Config Service
3. Creates an application with the E2E-NoFurtherAuthRequiredClaimSet
4. Saves credentials to .env.load-test

### Dependency Errors
- The tool automatically fetches dependencies from `/metadata/dependencies`
- Ensure this endpoint is accessible
- Check that all required descriptors are created first

### Rate Limiting
- The tool includes automatic rate limit handling
- Adjust VU count if seeing frequent 429 errors
- Add delays between operations if needed

### Local Testing Issues
- Ensure Docker containers are running
- Check that all required services are healthy
- Verify network connectivity between k6 and containers

## Development

### Project Structure
```
src/
├── config/         # Authentication and test configuration
├── generators/     # Data generation for each resource type
├── utils/          # Helper utilities (API client, data store, dependencies)
├── scenarios/      # Test scenarios (smoke, load, read-write)
└── main.js         # Entry point (future)
```

### Adding New Resource Types

1. Add generator in `src/generators/`
2. Update `src/generators/index.js` with new generator
3. Add dependency mappings in scenarios
4. Update resource counts in load phase

### Custom Scenarios

Create new scenarios in `src/scenarios/` following the k6 script structure:
- Export `options` for test configuration
- Export `setup()` for initialization
- Export default function for VU behavior
- Export `teardown()` for cleanup

## License

Licensed under the Apache License, Version 2.0.