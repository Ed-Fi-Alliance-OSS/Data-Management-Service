# DMS Load Test Troubleshooting Guide

## Common Issues and Solutions

### Authentication Issues

#### 401 Unauthorized Errors

**Symptoms:**
- DELETE operations failing with 401
- Intermittent authentication failures
- Token expiration messages

**Causes:**
1. Using `AuthManager` instead of `SharedAuthManager`
2. Client not configured with proper claim set
3. Token expired or invalid

**Solutions:**
```bash
# Recreate the load test client
rm .env.load-test
./run-load-test.sh  # Will automatically create new client

# Verify SharedAuthManager is used in scenarios
grep -n "AuthManager" src/scenarios/*.js
```

#### 429 Too Many Requests

**Symptoms:**
- Rate limiting errors
- "Too many token requests" messages
- Test fails early with 429 status codes

**Causes:**
1. Each VU requesting its own token (AuthManager behavior)
2. Too many concurrent requests
3. Server-side rate limiting

**Solutions:**
1. Ensure scenarios use `SharedAuthManager`
2. Reduce VUS count in profile
3. Add delays between operations
4. Contact DMS admin to adjust rate limits

### Connection Issues

#### Connection Refused

**Symptoms:**
```
Request Failed error="dial tcp 127.0.0.1:8080: connect: connection refused"
```

**Causes:**
1. DMS not running
2. Wrong port/URL configuration
3. Firewall blocking connection

**Solutions:**
```bash
# Check if DMS is running
curl http://localhost:8080/api/metadata

# Verify configuration
cat .env.load-test | grep API_BASE_URL

# Test connectivity
nc -zv localhost 8080
```

#### SSL/TLS Errors

**Symptoms:**
- Certificate validation errors
- HTTPS connection failures

**Solutions:**
```bash
# For self-signed certificates, disable verification (NOT for production!)
export NODE_TLS_REJECT_UNAUTHORIZED=0

# Or add certificate to trust store
export NODE_EXTRA_CA_CERTS=/path/to/ca-cert.pem
```

### Performance Issues

#### Slow Response Times

**Symptoms:**
- p95 response times exceeding thresholds
- Timeouts during load phase
- Degrading performance over time

**Diagnosis:**
```bash
# Monitor server resources during test
# On DMS server:
top -p $(pgrep -f "dms")
iostat -x 1
docker stats  # if using Docker
```

**Solutions:**
1. Reduce concurrent VUs
2. Increase server resources
3. Check database performance
4. Review server logs for bottlenecks

#### Memory Issues

**Symptoms:**
- k6 crashes with out of memory
- JavaScript heap errors
- System becomes unresponsive

**Solutions:**
```bash
# Increase k6 memory limit
k6 run --max-memory=4G src/scenarios/load.js

# Reduce data retention
k6 run --no-summary src/scenarios/load.js
```

### Data Issues

#### Dependency Failures

**Symptoms:**
- "Failed to create X: dependency not found"
- Resources created out of order
- Foreign key constraint violations

**Diagnosis:**
```bash
# Check dependency resolution
k6 run --env LOG_LEVEL=debug src/scenarios/load.js 2>&1 | grep -i depend
```

**Solutions:**
1. Verify dependency data exists
2. Check `dataStore` has required resources
3. Review resource creation order
4. Ensure load phase completes before readwrite

#### Duplicate Key Errors

**Symptoms:**
- POST operations failing with 409 Conflict
- "Resource already exists" errors

**Solutions:**
```bash
# Clean database before test
# Or use unique identifiers
export UNIQUE_PREFIX=$(date +%s)
./run-load-test.sh
```

### k6 Specific Issues

#### Module Import Errors

**Symptoms:**
```
ERRO[0000] TypeError: Cannot read property 'default' of undefined
```

**Solutions:**
1. Verify all imports use correct paths
2. Check module.exports vs export syntax
3. Ensure Node.js dependencies aren't used in k6 scripts

#### Metric Collection Issues

**Symptoms:**
- Missing metrics in results
- Incorrect aggregations
- No data in output files

**Solutions:**
```bash
# Enable debug output
k6 run --verbose --out json=debug.json src/scenarios/load.js

# Check metric definitions
grep -n "Counter\|Trend\|Rate\|Gauge" src/scenarios/*.js
```

## Debugging Techniques

### Enable Verbose Logging

```bash
# k6 verbose mode
k6 run --verbose src/scenarios/smoke.js

# Debug specific components
LOG_LEVEL=debug ./run-load-test.sh

# Capture all output
./run-load-test.sh 2>&1 | tee debug.log
```

### Isolate Issues

```bash
# Test authentication only
k6 run -i 1 -u 1 src/scenarios/smoke.js

# Test single resource type
k6 run --env RESOURCE_TYPE=students src/scenarios/load.js

# Test with minimal load
./run-load-test.sh --profile smoke
```

### Monitor Real-time

```bash
# Watch k6 output
k6 run src/scenarios/load.js | grep -E "✓|✗|ERROR"

# Monitor server logs
tail -f /var/log/dms/*.log

# Watch Docker logs
docker logs -f dms-local-dms
```

## Getting Help

### Collect Diagnostics

When reporting issues, include:

```bash
# System information
uname -a
k6 version
node --version

# Test configuration
cat .env.load-test
ls -la .env.profiles/

# Error logs
tail -n 100 results/load-*.json

# Server state
curl -s http://localhost:8080/api/metadata | jq
```

### Common Log Locations

- k6 results: `./results/`
- DMS logs: Check DMS documentation
- Docker logs: `docker logs dms-local-dms`
- System logs: `/var/log/syslog` or journalctl

### Support Channels

1. GitHub Issues: Report bugs in the repository
2. Ed-Fi Community: https://edfi.atlassian.net/wiki/spaces/COMMUNITY
3. Internal team chat/documentation

## Prevention Tips

1. **Always run smoke test first**
2. **Monitor server resources during tests**
3. **Use appropriate profiles for your environment**
4. **Keep test data isolated from production**
5. **Regular cleanup between test runs**
6. **Document any custom configurations**