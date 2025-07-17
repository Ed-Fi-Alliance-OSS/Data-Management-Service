# Load Test Debugging Completion Strategy

## Current Situation Analysis
- **Working**: OAuth authentication, data generation, API communication, dependency resolution
- **Not Working**: Authorization (403 errors) - client lacks proper permissions in DMS
- **Root Cause**: Client credentials in .env.load-test don't have the required claim set

## Strategic Approach

### Phase 1: Investigation (15 minutes)
1. **Examine E2E Test Setup**
   - Check how E2E tests create authorized clients
   - Look for existing working clients we can borrow
   - Understand the claim set requirements

2. **Analyze Current Infrastructure**
   - Verify Keycloak is properly configured
   - Check if Config Service is running and accessible
   - Confirm the authorization flow between Keycloak → DMS

### Phase 2: Fix Authorization (30 minutes)
**Option A: Reuse E2E Client (Fastest)**
```bash
# 1. Find E2E test client credentials
grep -r "E2E-NoFurtherAuthRequiredClaimSet" ../../../
# 2. Look for existing client IDs in test files
# 3. Update .env.load-test with working credentials
```

**Option B: Fix Setup Script (Most Sustainable)**
```bash
# 1. Update setupLoadTestClient.js to use correct endpoints
# 2. Ensure it creates client in Keycloak (not DMS)
# 3. Run the script to generate new authorized client
```

**Option C: Manual Configuration (If needed)**
```bash
# 1. Access Keycloak admin
# 2. Create/update client with proper claims
# 3. Test incrementally
```

### Phase 3: Incremental Testing (20 minutes)
1. **Test Single Descriptor**
   - Use test-single.sh with a simple descriptor
   - Verify 201 response and location header
   - Check DMS logs for successful creation

2. **Test Multiple Resource Types**
   - Descriptors (should work first)
   - Organizations (LEA, Schools)
   - People (Students, Staff)
   - Associations

3. **Test Dependencies**
   - Ensure resources are created in correct order
   - Verify references are properly resolved

### Phase 4: Full Load Test (15 minutes)
1. **Small Scale Test**
   ```bash
   # Already configured in debug-load-test.sh
   SCHOOL_COUNT=2 STUDENT_COUNT=10 ./debug-load-test.sh
   ```

2. **Medium Scale Test**
   ```bash
   SCHOOL_COUNT=10 STUDENT_COUNT=100 ./run-load-test.sh
   ```

3. **Performance Validation**
   - Monitor error rates (< 5%)
   - Check response times (< 5s)
   - Verify resource creation counts

### Phase 5: Final Validation & Documentation (10 minutes)
1. **Run Complete Test Suite**
   - Smoke test
   - Load phase
   - Read/write phase

2. **Document Results**
   - Update FINAL_STATUS.md with solution
   - Record performance metrics
   - Note any remaining issues

## Implementation Steps

### Step 1: Check E2E Test Configuration
```bash
# Look for working test clients
cd /home/brad/work/dms-root/Data-Management-Service
find . -name "*.cs" -o -name "*.json" | xargs grep -l "E2E-NoFurtherAuthRequiredClaimSet"
```

### Step 2: Verify Infrastructure
```bash
# Check all services are running
docker ps | grep -E "(keycloak|config|dms)"

# Test Config Service accessibility
curl -I http://localhost:8081/config/v2/vendors
```

### Step 3: Fix Authorization
Based on findings, implement the most appropriate option (A, B, or C)

### Step 4: Validate Fix
```bash
# Test authentication
./test-auth.sh

# Test single resource
./test-single.sh

# If successful, run debug test
./debug-load-test.sh
```

## Success Criteria
- [ ] OAuth token successfully obtained from Keycloak
- [ ] No 403 authorization errors
- [ ] Resources created successfully (201 responses)
- [ ] Location headers returned
- [ ] Data properly stored in DMS
- [ ] Error rate < 5%
- [ ] Response time p95 < 5000ms

## Contingency Plans
1. **If Keycloak is misconfigured**: Check docker-compose setup
2. **If Config Service is down**: Restart the service
3. **If claims don't work**: Use superuser credentials temporarily
4. **If nothing works**: Analyze E2E test execution in detail

## Tools to Use
- Task tool for parallel investigations
- Grep/Read for code analysis
- Bash for testing
- Edit for fixing files
- AGENT_COMMUNICATION.md for progress tracking

## Expected Timeline
- Total time: ~90 minutes
- Critical path: Authorization fix
- Parallelizable: Investigation tasks