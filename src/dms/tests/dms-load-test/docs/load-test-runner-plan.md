# Enhanced DMS Load Test Runner System

## Key Insights from run-smoke-test.sh

The smoke test runner succeeds because it:
1. **Manages Authentication Properly**: Uses `setupLoadTestClient.js` to create a client with `E2E-NoFurtherAuthRequiredClaimSet`
2. **Uses Correct Environment File**: Works with `.env.load-test` (not `.env`)
3. **Handles Missing Dependencies**: Auto-installs npm packages and creates clients as needed
4. **Provides Clear Feedback**: Shows configuration and progress throughout execution

## Critical Issues to Fix

### 1. Authentication Problems
- **smoke.js**: Uses `SharedAuthManager` ✅ (works correctly)
- **load.js**: Uses regular `AuthManager` ❌ (causes token exhaustion)
- **readwrite.js**: Uses regular `AuthManager` ❌ (causes token exhaustion)

### 2. Environment File Mismatch
- Scripts look for `.env` but actual file is `.env.load-test`
- Missing profile support for different environments

## Proposed Implementation

### 1. Create Main Test Runner (`run-load-test.sh`)
```bash
#!/bin/bash
# Features:
- Pre-flight checks (k6, npm, DMS availability)
- Client setup using setupLoadTestClient.js
- Profile support (--profile dev|staging|prod)
- Phase selection (--phase load|readwrite|full)
- Progress monitoring
- Results aggregation
```

### 2. Fix Authentication in Scenarios
Update `load.js` and `readwrite.js` to use `SharedAuthManager`:
```javascript
// Replace this:
const authManager = new AuthManager({...});

// With this:
const sharedAuthManager = new SharedAuthManager({...});
```

### 3. Create Configuration Profiles
```
.env.profiles/
├── smoke.env     # Minimal: 2 schools, 2 students
├── dev.env       # Small: 10 schools, 100 students
├── staging.env   # Medium: 50 schools, 5000 students
└── prod.env      # Full: 130 schools, 75000 students
```

### 4. Add Helper Scripts
```
scripts/
├── run-load-phase.sh      # Load phase only
├── run-readwrite-phase.sh # Readwrite phase only
├── run-full-test.sh       # Complete test suite
└── lib/
    ├── common.sh          # Shared functions
    ├── auth.sh            # Authentication helpers
    └── monitoring.sh      # Progress tracking
```

### 5. Enhanced Features

#### Progress Monitoring
- Real-time resource creation counts
- Error rate tracking
- ETA calculations
- Health check warnings

#### Data Persistence
- Save load phase results to `results/load-phase-data.json`
- Load data in readwrite phase for realistic testing
- Support for resuming interrupted tests

#### Error Handling
- Retry logic for transient failures
- Graceful degradation on errors
- Detailed error logs with timestamps

### 6. Documentation Structure
```
docs/
├── load-test-guide.md      # Comprehensive user guide
├── architecture.md         # Technical deep dive
├── troubleshooting.md      # Common issues & solutions
└── performance-tuning.md   # Optimization tips
```

## Implementation Order

1. **Fix Authentication** (Priority: Critical)
   - Update load.js and readwrite.js to use SharedAuthManager
   - Test token sharing across VUs

2. **Create Main Runner Script** (Priority: High)
   - Model after run-smoke-test.sh
   - Add profile and phase support
   - Include progress monitoring

3. **Add Configuration Profiles** (Priority: Medium)
   - Create .env.profiles directory
   - Define standard configurations
   - Support custom profiles

4. **Create Documentation** (Priority: Medium)
   - User guide with examples
   - Architecture documentation
   - Troubleshooting guide

5. **Add Advanced Features** (Priority: Low)
   - HTML report generation
   - Slack/email notifications
   - Grafana dashboard integration

## Benefits

1. **Consistent Authentication**: No more 401/429 errors from token exhaustion
2. **Easy to Use**: Single command with sensible defaults
3. **Flexible**: Support for different scales and scenarios
4. **Reliable**: Proper error handling and recovery
5. **Observable**: Real-time progress and detailed metrics
6. **Maintainable**: Clear structure and documentation