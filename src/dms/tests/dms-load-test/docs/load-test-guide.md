# DMS Load Test Guide

## Overview

The DMS Load Test framework provides comprehensive performance testing for the Ed-Fi Data Management Service. It simulates realistic educational data patterns at various scales, from small development environments to full Austin ISD-scale deployments.

## Quick Start

### Prerequisites

1. Install k6: https://k6.io/docs/getting-started/installation/
2. Ensure Node.js 18+ is installed
3. Have access to a running DMS instance

### Running Your First Test

```bash
# Run a smoke test (minimal configuration)
./run-load-test.sh --profile smoke

# Run a development-scale test
./run-load-test.sh --profile dev

# Run only the load phase
./run-load-test.sh --phase load --profile dev

# Run only the read-write phase
./run-load-test.sh --phase readwrite --profile dev
```

## Test Profiles

The framework includes pre-configured profiles for different testing scenarios:

### Smoke Profile (`--profile smoke`)
- **Purpose**: Quick validation of API connectivity and basic operations
- **Scale**: 2 schools, 2 students, 2 staff
- **Duration**: 30s per phase
- **Use Case**: CI/CD pipelines, quick health checks

### Development Profile (`--profile dev`)
- **Purpose**: Development and debugging
- **Scale**: 10 schools, 100 students, 20 staff
- **Duration**: 5 minutes per phase
- **Use Case**: Local development, feature testing

### Staging Profile (`--profile staging`)
- **Purpose**: Integration and pre-production testing
- **Scale**: 50 schools, 5,000 students, 800 staff
- **Duration**: 15 minutes load, 10 minutes read-write
- **Use Case**: Staging environment validation, performance regression testing

### Production Profile (`--profile prod`)
- **Purpose**: Full-scale performance testing
- **Scale**: 130 schools, 75,000 students, 12,000 staff (Austin ISD scale)
- **Duration**: 30 minutes load, 20 minutes read-write
- **Use Case**: Production readiness, capacity planning

## Test Phases

### Load Phase
- Creates resources following Ed-Fi dependency order
- POST-only operations
- Distributes resource creation across virtual users
- Populates the database with test data

### Read-Write Phase
- Performs mixed CRUD operations
- Operation mix: 50% GET, 20% POST, 20% PUT, 10% DELETE
- Tests real-world usage patterns
- Validates data integrity under load

### Full Test
- Runs both phases sequentially
- Smoke test validation before main phases
- Comprehensive performance assessment

## Configuration

### Environment Variables

The test framework uses a layered configuration approach:

1. **Base Configuration** (`.env.load-test`)
   - Created automatically by `setupLoadTestClient.js`
   - Contains API URLs and authentication credentials

2. **Profile Overrides** (`.env.profiles/*.env`)
   - Override specific values for different scales
   - Located in `.env.profiles/` directory

### Custom Profiles

Create custom profiles by adding files to `.env.profiles/`:

```bash
# .env.profiles/custom.env
SCHOOL_COUNT=25
STUDENT_COUNT=1000
STAFF_COUNT=150
VUS_LOAD_PHASE=10
VUS_READWRITE_PHASE=15
DURATION_LOAD_PHASE=10m
DURATION_READWRITE_PHASE=10m
```

Then run: `./run-load-test.sh --profile custom`

## Authentication

The framework uses the `E2E-NoFurtherAuthRequiredClaimSet` for testing, which provides full CRUD permissions. Authentication is handled through:

1. **Automatic Client Setup**: The `setupLoadTestClient.js` script creates a properly configured client
2. **Token Management**: `SharedAuthManager` ensures efficient token sharing across virtual users

## Results and Analysis

### Output Files

Results are saved in the `results/` directory:
- `smoke-{timestamp}.json` - Smoke test results
- `load-{profile}-{timestamp}.json` - Load phase results
- `readwrite-{profile}-{timestamp}.json` - Read-write phase results

### Analyzing Results

1. **Console Output**: Real-time metrics during test execution
2. **JSON Files**: Detailed metrics for post-analysis
3. **K6 Cloud**: Upload results for advanced visualization
   ```bash
   k6 cloud results/load-dev-20250117-102030.json
   ```

### Key Metrics

- **http_req_duration**: Response time (p95 < 2000ms for smoke, < 5000ms for load)
- **http_req_failed**: Error rate (< 10% for smoke, < 5% for load)
- **crud_operations**: Count of CRUD operations performed
- **errors**: Custom error tracking

## Troubleshooting

### Common Issues

1. **401 Unauthorized Errors**
   - Ensure client is created with proper claim set
   - Check that SharedAuthManager is being used
   - Verify token hasn't expired

2. **429 Too Many Requests**
   - Reduce VUS count in profile
   - Increase delays between operations
   - Check if using SharedAuthManager (prevents token exhaustion)

3. **Connection Refused**
   - Verify DMS is running
   - Check API_BASE_URL in .env.load-test
   - Ensure no firewall blocking

### Debug Mode

Enable verbose output for troubleshooting:
```bash
k6 run --verbose src/scenarios/load.js
```

## Best Practices

1. **Start Small**: Always run smoke test first
2. **Monitor Resources**: Watch DMS server resources during tests
3. **Incremental Testing**: Progress from dev → staging → prod profiles
4. **Clean Environment**: Consider database state between tests
5. **Result Tracking**: Save results for performance trend analysis

## Advanced Usage

### Running Specific Scenarios

```bash
# Direct k6 execution with custom options
k6 run --vus 50 --duration 10m src/scenarios/readwrite.js

# With environment override
VUS_LOAD_PHASE=200 ./run-load-test.sh --profile prod
```

### Integration with CI/CD

```yaml
# Example GitHub Actions workflow
- name: Run Load Test
  run: |
    cd src/dms/tests/dms-load-test
    ./run-load-test.sh --profile smoke
    
- name: Upload Results
  uses: actions/upload-artifact@v2
  with:
    name: load-test-results
    path: src/dms/tests/dms-load-test/results/
```

## Next Steps

1. Review the [Architecture Documentation](architecture.md) for technical details
2. See [Troubleshooting Guide](troubleshooting.md) for detailed problem resolution
3. Check [Performance Tuning](performance-tuning.md) for optimization tips
