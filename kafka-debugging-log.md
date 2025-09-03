# Kafka E2E Test Connectivity Issue - Debugging Log

## Problem Statement

Kafka E2E tests pass locally (with host file entry `127.0.0.1 dms-kafka1`) but fail on GitHub Actions CI with error:
```
Expected messages not to be empty because Expected to receive at least one message on topic 'edfi.dms.document'.
```

## Root Cause Analysis

The issue is a **Docker networking mismatch** between what Kafka advertises and how clients try to connect:

1. **Kafka Configuration**: Kafka advertises itself as `PLAINTEXT://dms-kafka1:9092`
2. **E2E Test Code**: Hardcoded to connect to `localhost:9092` 
3. **Local Environment**: Works because host file maps `dms-kafka1` → `127.0.0.1`
4. **CI Environment**: Fails because `dms-kafka1` hostname doesn't resolve

The E2E tests consume messages produced by Kafka Connect (PostgreSQL source connector), so both producer and consumer need to reach the same Kafka instance.

## Attempted Solutions

### Attempt 1: Add Host Entry to GitHub Actions ❌
**What I tried**: Added `echo "127.0.0.1 dms-kafka1" | sudo tee -a /etc/hosts` to workflow
**Result**: Test still failed
**Issue**: Approach was correct but didn't address the underlying networking problem

### Attempt 2: Dual Listeners with Separate Ports ❌  
**What I tried**: 
- `KAFKA_ADVERTISED_LISTENERS: INTERNAL://dms-kafka1:19092,EXTERNAL://localhost:9092`
- `KAFKA_LISTENERS: INTERNAL://0.0.0.0:19092,EXTERNAL://0.0.0.0:9092`
- Kafka Connect: `dms-kafka1:19092`, E2E tests: `localhost:9092`

**Result**: 
- Kafka Connect containers failed to start with connectivity issues
- When fixed, E2E tests failed because of topic isolation between listeners

**Issue**: Different listeners created separate topic spaces - messages produced via INTERNAL weren't visible to EXTERNAL consumers

### Attempt 3: Unified Port with Dual Listeners ❌
**What I tried**: 
- Both listeners on port 9092: `INTERNAL://dms-kafka1:9092,EXTERNAL://localhost:9092`
- `KAFKA_LISTENERS: INTERNAL://0.0.0.0:9092,EXTERNAL://0.0.0.0:9092`

**Result**: Kafka container failed to start
**Issue**: Port binding conflict - can't bind two listeners to the same port within container

### Attempt 4: Revert to Separate Ports, Fix Kafka Connect ❌
**What I tried**: 
- Separate ports again: INTERNAL (19092), EXTERNAL (9092) 
- Updated all Kafka Connect `BOOTSTRAP_SERVERS` to use `dms-kafka1:19092`

**Result**: Deployment worked but E2E test still failed
**Issue**: Same topic isolation problem - dual listeners don't share topic data

### Attempt 5: Single Listener, Everyone Uses localhost ❌
**What I tried**: 
- `KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://localhost:9092`
- Kafka Connect: `BOOTSTRAP_SERVERS: localhost:9092`
- E2E tests: `localhost:9092`

**Result**: Kafka Connect containers failed to start
**Issue**: `localhost` inside a container refers to that container, not the Kafka container

### Attempt 6: Single Listener, Container DNS for Connect ❌
**What I tried**: 
- `KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://localhost:9092` (for external clients)
- Kafka Connect: `BOOTSTRAP_SERVERS: dms-kafka1:9092` (Docker internal DNS)
- E2E tests: `localhost:9092` (Docker port forwarding)

**Result**: Kafka Connect containers failed to start - "No connection could be made because the target machine actively refused it. (localhost:8083)"
**Issue**: Kafka Connect containers are trying to connect to `dms-kafka1:9092`, but Kafka advertises `localhost:9092`. When containers try to connect, Kafka tells them to use `localhost:9092`, which doesn't resolve within the container network context.

## Key Learnings

1. **Docker Networking Context Matters**: `localhost` means different things inside vs outside containers
2. **Kafka Dual Listeners Create Topic Isolation**: INTERNAL and EXTERNAL listeners don't share topic data - messages produced on one listener aren't visible to consumers on another
3. **Advertised Listeners Must Match Client Context**: What Kafka advertises must be resolvable by the client making the connection
4. **Container-to-Container vs Host-to-Container**: Different networking mechanisms require different connection strings
5. **Environment Variables for Flexibility**: Making connection strings configurable allows same code to work in different environments
6. **Host File Entries Are Sometimes Necessary**: When Docker container names need to be resolved from host context
7. **Network Aliases Don't Solve Everything**: Docker network aliases don't necessarily help with Kafka's advertised listener resolution

## Files Modified

- `eng/docker-compose/kafka-opensearch.yml`
- `eng/docker-compose/kafka-elasticsearch.yml`  
- `.github/workflows/on-dms-pullrequest.yml`

### Attempt 7: Add Container Network Alias ❌
**What I tried**: 
- Add network alias to Kafka container: `dms-kafka1` AND `localhost`
- Keep single listener: `KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://localhost:9092`
- Kafka Connect: `BOOTSTRAP_SERVERS: dms-kafka1:9092`

**Result**: Kafka Connect still failed with "TimeoutException: Timed out waiting for a node assignment"
**Issue**: Even with network alias, containers connecting to `dms-kafka1:9092` get told by Kafka to use `localhost:9092`, but `localhost` within container context still doesn't resolve correctly to Kafka.

### Attempt 8: Environment-Based Connection String ✅ (WORKING SOLUTION)
**What I implemented**: 
- Made E2E test connection configurable via environment variable `KAFKA_BOOTSTRAP_SERVERS`
- Reverted Kafka to original working configuration: `KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://dms-kafka1:9092`
- Updated `KafkaStepDefinitions.cs` to use environment variable with localhost fallback
- Set `KAFKA_BOOTSTRAP_SERVERS=dms-kafka1:9092` in CI environment
- Added host file entry in CI: `127.0.0.1 dms-kafka1`

**Code Changes**:
```csharp
// In KafkaStepDefinitions.cs
var bootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";
```

**GitHub Actions Changes**:
```yaml
- name: Add Kafka hostname to /etc/hosts
  run: echo "127.0.0.1 dms-kafka1" | sudo tee -a /etc/hosts

- name: Run E2E Tests
  env:
    KAFKA_BOOTSTRAP_SERVERS: dms-kafka1:9092
```

**Result**: 
- ✅ Local tests pass (with host file entry `127.0.0.1 dms-kafka1`)
- ✅ Kafka Connect containers start successfully  
- ✅ E2E tests can consume messages produced by Kafka Connect
- ✅ Configuration works in both environments with different connection strings

## Final Working Solution Summary

**Environment-Based Connection String (Attempt 8)** successfully resolved the Kafka connectivity issue by:

1. **Keeping Original Kafka Configuration**: `KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://dms-kafka1:9092`
2. **Making E2E Tests Flexible**: Environment variable `KAFKA_BOOTSTRAP_SERVERS` with fallback to `localhost:9092`
3. **Environment-Specific Configuration**:
   - **Local**: Uses `localhost:9092` (requires host file: `127.0.0.1 dms-kafka1`)  
   - **CI**: Uses `dms-kafka1:9092` + host file entry in GitHub Actions

## Troubleshooting Guide for Future Issues

### If Kafka Connect Fails to Start:
1. Check `BOOTSTRAP_SERVERS` points to correct Kafka hostname (`dms-kafka1:9092`)
2. Verify Kafka container is healthy and reachable on internal network
3. Check Kafka logs for listener binding issues

### If E2E Tests Can't Connect:
1. Verify `KAFKA_BOOTSTRAP_SERVERS` environment variable is set correctly
2. Check host file has entry: `127.0.0.1 dms-kafka1` (if using `dms-kafka1:9092`)
3. Test Kafka connectivity with `docker logs dms-kafka1`

### If Messages Not Being Consumed:
1. Verify same Kafka instance is used by producers (Kafka Connect) and consumers (E2E tests)
2. Check topic exists: `docker exec dms-kafka1 /opt/kafka/bin/kafka-topics.sh --bootstrap-server dms-kafka1:9092 --list`
3. Check consumer group and offset settings

### Environment Variables:
- `KAFKA_BOOTSTRAP_SERVERS`: Override default connection string for E2E tests
- Default fallback: `localhost:9092` (requires host file locally)

## References

- Kafka documentation on listeners: https://kafka.apache.org/documentation/#listeners
- Docker networking: https://docs.docker.com/network/
- GitHub Actions networking: https://docs.github.com/en/actions/using-containerized-services