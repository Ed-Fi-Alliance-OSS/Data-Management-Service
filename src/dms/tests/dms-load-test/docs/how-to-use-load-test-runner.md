# How to Use the DMS Load Test Runner

## Table of Contents
- [Quick Start](#quick-start)
- [Prerequisites](#prerequisites)
- [Basic Usage](#basic-usage)
- [Understanding Profiles](#understanding-profiles)
- [Test Phases Explained](#test-phases-explained)
- [Common Use Cases](#common-use-cases)
- [Reading Results](#reading-results)
- [Troubleshooting](#troubleshooting)
- [Advanced Usage](#advanced-usage)

## Quick Start

The fastest way to run a load test:

```bash
# Run a minimal smoke test (takes ~1 minute)
./run-load-test.sh --profile smoke
```

This command will:
1. Check prerequisites (k6, npm)
2. Create a test client if needed
3. Run a smoke test to verify connectivity
4. Execute minimal load testing
5. Save results in the `results/` directory

## Prerequisites

Before running load tests, ensure you have:

1. **k6 installed**
   ```bash
   # macOS
   brew install k6
   
   # Linux
   sudo apt-get update && sudo apt-get install k6
   
   # Or download from https://k6.io/docs/getting-started/installation/
   ```

2. **Node.js 18+ installed**
   ```bash
   node --version  # Should show v18.0.0 or higher
   ```

3. **A running DMS instance**
   ```bash
   # For local testing
   cd Data-Management-Service/eng/docker-compose
   ./start-local-dms.ps1 -EnableConfig
   ```

## Basic Usage

### Command Structure

```bash
./run-load-test.sh [OPTIONS]
```

### Available Options

| Option | Description | Default |
|--------|-------------|---------|
| `--profile <name>` | Test scale profile (smoke, dev, staging, prod) | dev |
| `--phase <phase>` | Which phase to run (load, readwrite, full) | full |
| `--help` | Show help message | - |

### Examples

```bash
# Run with default settings (dev profile, full test)
./run-load-test.sh

# Run a production-scale test
./run-load-test.sh --profile prod

# Run only the load phase with staging profile
./run-load-test.sh --profile staging --phase load

# Run only the read-write phase
./run-load-test.sh --phase readwrite

# Show help
./run-load-test.sh --help
```

## Understanding Profiles

Profiles control the scale and duration of your tests:

### Smoke Profile 🚬
```bash
./run-load-test.sh --profile smoke
```
- **Scale**: 2 schools, 2 students, 2 staff
- **Duration**: 30 seconds per phase
- **Use for**: Quick validation, CI/CD checks
- **Total time**: ~2 minutes

### Development Profile 💻
```bash
./run-load-test.sh --profile dev
```
- **Scale**: 10 schools, 100 students, 20 staff
- **Duration**: 5 minutes per phase
- **Use for**: Development testing, debugging
- **Total time**: ~10 minutes

### Staging Profile 🎭
```bash
./run-load-test.sh --profile staging
```
- **Scale**: 50 schools, 5,000 students, 800 staff
- **Duration**: 15 min load, 10 min read-write
- **Use for**: Pre-production validation
- **Total time**: ~25 minutes

### Production Profile 🏭
```bash
./run-load-test.sh --profile prod
```
- **Scale**: 130 schools, 75,000 students, 12,000 staff
- **Duration**: 30 min load, 20 min read-write
- **Use for**: Full-scale performance testing
- **Total time**: ~50 minutes

## Test Phases Explained

### Full Test (default)
Runs all phases in sequence:
1. Smoke test (connectivity check)
2. Load phase (create resources)
3. Read-write phase (mixed operations)

```bash
./run-load-test.sh --phase full
```

### Load Phase Only
Creates resources following Ed-Fi dependencies:
- POST operations only
- Builds test data from scratch
- Follows dependency order

```bash
./run-load-test.sh --phase load
```

### Read-Write Phase Only
Performs mixed CRUD operations:
- 50% GET (read)
- 20% POST (create)
- 20% PUT (update)
- 10% DELETE

```bash
./run-load-test.sh --phase readwrite
```

## Common Use Cases

### 1. First-Time Testing
Start with smoke test to verify setup:
```bash
./run-load-test.sh --profile smoke
```

### 2. Daily Development Testing
Use dev profile for regular testing:
```bash
./run-load-test.sh --profile dev
```

### 3. Pre-Release Validation
Test at staging scale:
```bash
./run-load-test.sh --profile staging
```

### 4. Performance Benchmarking
Full production scale:
```bash
./run-load-test.sh --profile prod
```

### 5. Debugging Specific Issues
Test individual phases:
```bash
# Test only resource creation
./run-load-test.sh --phase load --profile dev

# Test only CRUD operations
./run-load-test.sh --phase readwrite --profile dev
```

## Reading Results

### Console Output

During execution, you'll see:
```
🚀 DMS Load Test Runner
==========================

Configuration:
  Profile: dev
  Phase: full
  API URL: http://localhost:8080/api
  Client: 365aa634-2b04-4766-9b1d-5b5bdf894f3d

Test Scale:
  Schools: 10
  Students: 100
  Staff: 20
```

### Result Files

Results are saved in `results/` directory:
```
results/
├── smoke-20250117-143022.json
├── load-dev-20250117-143025.json
└── readwrite-dev-20250117-143532.json
```

### Key Metrics to Watch

1. **Response Time**: `http_req_duration`
   - Good: p95 < 2000ms
   - Warning: p95 > 5000ms

2. **Error Rate**: `http_req_failed`
   - Good: < 1%
   - Warning: > 5%

3. **Throughput**: `http_reqs`
   - Shows requests per second

### Analyzing Results

```bash
# View summary statistics
k6 inspect results/load-dev-20250117-143025.json

# Generate HTML report (if k6-reporter is installed)
k6-reporter results/load-dev-20250117-143025.json
```

## Troubleshooting

### Common Issues

#### "k6 is not installed"
```bash
# Install k6 first
brew install k6  # macOS
# or
sudo apt-get install k6  # Linux
```

#### "Failed to set up load test client"
```bash
# Check if DMS and Config Service are running
curl http://localhost:8080/api/metadata
curl http://localhost:8081/config/

# Remove old client and retry
rm .env.load-test
./run-load-test.sh
```

#### "401 Unauthorized" errors
```bash
# Recreate the client
rm .env.load-test
./run-load-test.sh
```

#### Test seems stuck
```bash
# Check DMS server resources
# The server might be overwhelmed
# Try a smaller profile:
./run-load-test.sh --profile smoke
```

## Advanced Usage

### Custom Environment Variables

Override specific settings:
```bash
# More virtual users
VUS_LOAD_PHASE=50 ./run-load-test.sh --profile dev

# Longer duration
DURATION_LOAD_PHASE=10m ./run-load-test.sh --profile dev

# Different API URL
API_BASE_URL=https://staging.example.com/api ./run-load-test.sh
```

### Creating Custom Profiles

1. Create a new profile file:
```bash
cat > .env.profiles/custom.env << EOF
# Custom Profile
SCHOOL_COUNT=25
STUDENT_COUNT=2500
STAFF_COUNT=400
VUS_LOAD_PHASE=15
VUS_READWRITE_PHASE=20
DURATION_LOAD_PHASE=10m
DURATION_READWRITE_PHASE=10m
EOF
```

2. Use your custom profile:
```bash
./run-load-test.sh --profile custom
```

### Running from Scripts Directory

Alternative ways to run tests:
```bash
# From scripts directory
cd scripts
./run-load-phase.sh --profile dev
./run-readwrite-phase.sh --profile dev
./run-full-test.sh --profile staging
```

### Continuous Integration

Example GitHub Actions workflow:
```yaml
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

### Monitoring During Tests

In another terminal, monitor the DMS server:
```bash
# Watch Docker containers
docker stats

# Monitor DMS logs
docker logs -f dms-local-dms

# Check system resources
htop
```

## Best Practices

1. **Start Small**: Always begin with smoke or dev profile
2. **Monitor Server**: Watch server resources during tests
3. **Clean Between Tests**: Consider database state
4. **Save Results**: Archive results for trend analysis
5. **Document Changes**: Note any configuration changes

## Getting Help

- Check the [Troubleshooting Guide](troubleshooting.md) for detailed solutions
- Review the [Architecture Documentation](architecture.md) for technical details
- See the main [Load Test Guide](load-test-guide.md) for comprehensive information

## Summary

The load test runner makes it easy to:
- Test at different scales (smoke → dev → staging → prod)
- Run specific phases (load, readwrite, or both)
- Get consistent, repeatable results
- Troubleshoot performance issues

Start with `./run-load-test.sh --profile smoke` and work your way up!