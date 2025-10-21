# ABORT_ON_4XX Feature Documentation

## Overview
The ABORT_ON_4XX feature provides automatic test termination when any HTTP request (GET, POST, PUT, DELETE) returns a 4xx client error status code. This helps quickly identify data validation issues, authorization problems, or API misconfigurations during load testing.

## Purpose
- **Early Detection**: Immediately identifies client-side errors that would invalidate test results
- **Resource Conservation**: Prevents wasting time and resources on tests that are failing due to bad data or configuration
- **Clear Diagnostics**: Provides detailed error information for debugging
- **CI/CD Integration**: Ensures tests fail fast in automated pipelines

## Configuration

### Environment Variable
```bash
ABORT_ON_4XX=true  # Enable abort on 4xx errors (default)
ABORT_ON_4XX=false # Disable abort on 4xx errors
```

### Setting the Variable

#### In `.env.load-test`
```bash
# Error handling configuration
ABORT_ON_4XX=true
```

#### Via Command Line
```bash
ABORT_ON_4XX=false ./run-load-test.sh
```

#### In k6 Command
```bash
k6 run -e ABORT_ON_4XX=false src/scenarios/load.js
```

## Behavior

### When Enabled (ABORT_ON_4XX=true)
1. Any 4xx response (400-499) from GET, POST, PUT, or DELETE operations triggers immediate test abortion
2. Detailed error information is logged before abortion:
   - HTTP status code
   - Endpoint URL
   - Resource type (for POST operations)
   - Response body with error details
   - Request body (for POST/PUT operations)
3. Test exits with k6's ScriptAborted exit code
4. Teardown functions still execute for cleanup

### When Disabled (ABORT_ON_4XX=false)
1. 4xx errors are logged but test continues
2. Errors are tracked in metrics but don't stop execution
3. Test completes normally unless other thresholds are crossed

## Error Output Example

When a 4xx error triggers abortion:
```
CRITICAL: 400 error detected. Test will be aborted.
Endpoint: /ed-fi/students
Resource Type: students
Response: {
  "detail": "Data validation failed. See 'validationErrors' for details.",
  "type": "urn:ed-fi:api:bad-request:data-validation-failed",
  "title": "Data Validation Failed",
  "status": 400,
  "correlationId": "abc123",
  "validationErrors": {
    "$.birthDate": ["The field birthDate must be a valid date."]
  }
}
Request Body: {"firstName":"John","lastName":"Doe","birthDate":"invalid-date"}

Test aborted due to 400 error on POST /ed-fi/students: [full error details]
```

## Use Cases

### Recommended to Enable:
- Development and debugging of test data generators
- Initial test setup and configuration
- CI/CD pipelines where data quality must be ensured
- When testing against strict APIs with validation

### Recommended to Disable:
- Stress testing to measure error rates
- Testing API error handling capabilities
- When some 4xx errors are expected (e.g., testing duplicate prevention)
- Performance testing of error paths

## Implementation Details

The feature is implemented in `src/utils/apiDataClient.js` and affects all HTTP methods:
- `post()` - Checks after request failure
- `get()` - Checks after request failure
- `put()` - Checks after request failure
- `delete()` - Checks after request failure

The abort is performed using k6's official `exec.test.abort()` function, ensuring:
- Graceful shutdown
- Teardown execution
- Proper exit codes
- Test result preservation

## Exit Codes
When ABORT_ON_4XX triggers:
- k6 exits with the ScriptAborted exit code
- This is detectable by CI/CD systems
- Different from threshold failures or script errors

## Best Practices

1. **Enable During Development**: Keep ABORT_ON_4XX=true while developing and debugging test scripts
2. **Review Before Production**: Decide whether to keep enabled based on test goals
3. **Monitor Logs**: When disabled, regularly review logs for 4xx errors that might indicate problems
4. **Use with Thresholds**: Combine with k6 thresholds for comprehensive error handling:
   ```javascript
   export const options = {
       thresholds: {
           http_req_failed: ['rate<0.05'], // Less than 5% errors
           errors: ['rate<0.05']
       }
   };
   ```

## Troubleshooting

### Test Aborts Immediately
- Check data generators for invalid data formats
- Verify API endpoints are correct
- Ensure proper authentication/authorization
- Review API documentation for required fields

### Need to Continue Despite 4xx Errors
- Set `ABORT_ON_4XX=false` temporarily
- Fix underlying issues before re-enabling
- Consider if 4xx errors are expected behavior

### Debugging Specific Failures
- Enable debug mode: `DEBUG=true ABORT_ON_4XX=true ./run-load-test.sh`
- Check request/response bodies in error output
- Verify against API documentation

## Related Configuration
- `DEBUG=true` - Enables verbose logging
- `http_req_failed` threshold - Controls overall error rate limits
- Error rate metrics - Tracked regardless of ABORT_ON_4XX setting