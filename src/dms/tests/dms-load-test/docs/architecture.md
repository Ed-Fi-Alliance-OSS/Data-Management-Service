# DMS Load Test Architecture

## Overview

The DMS Load Test framework is built on k6, a modern load testing tool that executes tests written in JavaScript. The framework is designed to simulate realistic Ed-Fi API usage patterns at various scales.

## Architecture Components

### Core Components

```
┌─────────────────────────────────────────────────────────────┐
│                        Test Runner                           │
│  (run-load-test.sh / run-smoke-test.sh)                    │
├─────────────────────────────────────────────────────────────┤
│                    Configuration Layer                       │
│  (.env.load-test + .env.profiles/*.env)                    │
├─────────────────────────────────────────────────────────────┤
│                      k6 Test Engine                         │
│                 (scenarios / generators)                     │
├─────────────────────────────────────────────────────────────┤
│                   Authentication Layer                       │
│           (SharedAuthManager / OAuth 2.0)                   │
├─────────────────────────────────────────────────────────────┤
│                      DMS API Layer                          │
│              (Resources / Dependencies)                      │
└─────────────────────────────────────────────────────────────┘
```

### Directory Structure

```
dms-load-test/
├── src/
│   ├── scenarios/          # k6 test scenarios
│   │   ├── smoke.js       # Basic connectivity test
│   │   ├── load.js        # Resource creation phase
│   │   └── readwrite.js   # Mixed CRUD operations
│   ├── config/            # Configuration modules
│   │   ├── auth.js        # Basic auth manager
│   │   └── sharedAuth.js  # Shared token manager
│   ├── generators/        # Data generation
│   │   ├── students.js    # Student data generator
│   │   ├── schools.js     # School data generator
│   │   └── index.js       # Generator orchestration
│   └── utils/             # Utility modules
│       ├── api.js         # API client wrapper
│       ├── dataStore.js   # In-memory data storage
│       └── dependencies.js # Resource dependency resolver
├── scripts/               # Shell scripts
├── docs/                  # Documentation
├── results/               # Test results (gitignored)
└── .env.profiles/         # Configuration profiles
```

## Key Design Patterns

### 1. Shared Authentication

The framework uses a singleton pattern for authentication to prevent token exhaustion:

```javascript
// Global token store
const tokenStore = {
    token: null,
    tokenExpiry: null,
    isRefreshing: false
};

// SharedAuthManager ensures only one token is requested
class SharedAuthManager {
    getToken() {
        if (tokenStore.token && !isExpired) {
            return tokenStore.token;  // Reuse existing
        }
        // Request new token...
    }
}
```

**Benefits:**
- Prevents 429 rate limiting
- Reduces authentication overhead
- Simulates realistic client behavior

### 2. Dependency Resolution

Ed-Fi resources have complex dependencies. The framework resolves these automatically:

```javascript
// Dependency chain example
School → requires → LocalEducationAgency
Student → requires → School
StudentSchoolAssociation → requires → Student + School
Grade → requires → StudentSectionAssociation
```

**Implementation:**
- Fetches dependency metadata from `/metadata/dependencies`
- Builds directed acyclic graph (DAG)
- Processes resources in topological order

### 3. Data Generation

Realistic test data is generated using faker.js with Ed-Fi specific constraints:

```javascript
// Example: Generate valid student
{
    studentUniqueId: "S" + faker.number.int({min: 100000, max: 999999}),
    firstName: faker.person.firstName(),
    lastSurname: faker.person.lastName(),
    birthDate: generateBirthDate(ageRange),
    // Ed-Fi specific fields...
}
```

### 4. Virtual User Distribution

Load is distributed across virtual users (VUs) to simulate concurrent API clients:

```javascript
// Each VU processes a subset of resources
const resourcesPerVU = Math.ceil(totalResources / totalVUs);
const startIndex = (vuId - 1) * resourcesPerVU;
const endIndex = Math.min(startIndex + resourcesPerVU, totalResources);
```

## Test Phases

### Smoke Test Phase

- **Purpose**: Validate basic connectivity and authentication
- **Operations**: Simple CRUD cycle on descriptors
- **Duration**: 30 seconds
- **Success Criteria**: All basic operations succeed

### Load Phase

- **Purpose**: Populate database with test data
- **Strategy**: POST-only operations in dependency order
- **Scaling**: Distributed across VUs
- **Key Features**:
  - Respects Ed-Fi resource dependencies
  - Creates resources in correct order
  - Stores created resources for later use

### Read-Write Phase

- **Purpose**: Simulate production usage patterns
- **Operation Mix**:
  - 50% GET (read operations)
  - 20% POST (create new)
  - 20% PUT (updates)
  - 10% DELETE (selective deletion)
- **Strategy**: Random selection from available resources

## Performance Considerations

### Memory Management

- **SharedArray**: k6's memory-efficient data structure for read-only data
- **Data Store**: Selective storage of essential fields only
- **Streaming**: Process resources incrementally, not all at once

### Network Optimization

- **Connection Pooling**: k6 manages HTTP connections efficiently
- **Request Batching**: Groups related operations where possible
- **Compression**: Supports gzip/deflate for API responses

### Scalability Limits

| Component | Limit | Mitigation |
|-----------|-------|------------|
| VUs | ~1000 per instance | Distribute across multiple k6 instances |
| Memory | 4-8GB typical | Use --max-memory flag |
| Data Store | ~100k resources | Selective field storage |
| Token Cache | Single token | SharedAuthManager pattern |

## Configuration System

### Layered Configuration

1. **Base Layer** (`.env.load-test`)
   - API endpoints
   - Authentication credentials
   - Generated by setupLoadTestClient.js

2. **Profile Layer** (`.env.profiles/*.env`)
   - Overrides for different scales
   - Test-specific parameters
   - Named profiles (smoke, dev, staging, prod)

3. **Runtime Layer** (environment variables)
   - Command-line overrides
   - CI/CD injected values

### Configuration Precedence

```
Runtime ENV > Profile ENV > Base .env.load-test > Defaults
```

## Metrics and Monitoring

### Built-in k6 Metrics

- `http_req_duration`: Response time percentiles
- `http_req_failed`: Error rate
- `http_reqs`: Throughput
- `data_received/sent`: Network traffic

### Custom Metrics

```javascript
const operationCounter = new Counter('crud_operations');
const operationDuration = new Trend('crud_operation_duration');
```

### Thresholds

```javascript
thresholds: {
    http_req_duration: ['p(95)<5000'],  // 95% under 5s
    http_req_failed: ['rate<0.05'],     // Less than 5% errors
    errors: ['rate<0.05']               // Custom error rate
}
```

## Security Considerations

1. **Claim Sets**: Uses E2E-NoFurtherAuthRequiredClaimSet for testing
2. **Token Storage**: In-memory only, never persisted
3. **Credentials**: Stored in .env files (gitignored)
4. **Test Isolation**: Separate test data from production

## Extension Points

### Adding New Scenarios

1. Create new file in `src/scenarios/`
2. Import required utilities
3. Define test logic and metrics
4. Add to test runner

### Adding New Generators

1. Create generator in `src/generators/`
2. Follow Ed-Fi data model constraints
3. Register in `generators/index.js`

### Custom Profiles

1. Add `.env.profiles/custom.env`
2. Override desired parameters
3. Use with `--profile custom`

## Best Practices

1. **Use SharedAuthManager**: Prevents token exhaustion
2. **Respect Dependencies**: Let framework handle order
3. **Monitor Resources**: Watch server during tests
4. **Clean Between Runs**: Avoid data contamination
5. **Start Small**: Progress through profiles gradually