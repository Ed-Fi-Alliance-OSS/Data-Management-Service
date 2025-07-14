# Token Caching Strategy for Ed-Fi Load Tests

## Problem
The Ed-Fi API has strict rate limits on OAuth token requests:
- Maximum 15 tokens can be requested
- Tokens are valid for 1800 seconds (30 minutes)
- Exceeding the limit results in HTTP 429 errors
- Must wait 5 minutes after hitting the limit

## Solution
The load test tool implements token caching using `SharedAuthManager`:

1. **Global Token Store**: Tokens are stored in a module-level object shared across all VUs and iterations
2. **Token Reuse**: Once obtained, a token is reused until 5 minutes before expiry
3. **Concurrency Protection**: Prevents multiple VUs from requesting tokens simultaneously
4. **Pre-warming**: The `setup()` function obtains the initial token before tests start

## Usage

### If Rate Limited
Wait 5 minutes before running tests:
```bash
sleep 300 && npm run test:smoke
```

### Best Practices
1. Run tests sequentially, not in parallel
2. Use the same token across all test phases
3. Monitor token expiry and refresh proactively
4. Consider using longer-lived tokens for load testing if possible

### Debugging
To see token caching in action, the smoke test logs token info every 10 iterations:
```
Iteration 0: Token cached=true, expires=2024-01-01T12:30:00.000Z
Iteration 10: Token cached=true, expires=2024-01-01T12:30:00.000Z
```

## Technical Details
- Token store is in-memory (resets between test runs)
- Tokens are shared across VUs within a single test run
- Each test scenario should use `SharedAuthManager` instead of `AuthManager`
- The token includes a 5-minute buffer before expiry to ensure smooth refresh