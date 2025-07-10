# JWT Authentication Migration Plan: Frontend to DMS Core

## Executive Summary

This document outlines the plan to move all JWT authentication and authorization logic from the ASP.NET Core frontend to DMS Core, ensuring the frontend becomes a pure pass-through layer with no authentication responsibilities.

## Current State

### Frontend (ASP.NET Core)
- Uses `Microsoft.AspNetCore.Authentication.JwtBearer` for JWT authentication
- Configures authentication in `WebApplicationBuilderExtensions.cs`
- Uses `RequireAuthorization()` on endpoint mappings
- Handles both data endpoints (needing ClientAuthorizations) and non-data endpoints (metadata, discovery)

### Core
- Already has JWT validation infrastructure using Microsoft.IdentityModel libraries
- `JwtAuthenticationMiddleware` extracts ClientAuthorizations for data endpoints
- Uses custom pipeline with `IPipelineStep` pattern
- Has singleton `ConfigurationManager` for OIDC metadata caching

## Architecture Design

### 1. Unified Authentication Middleware

Instead of multiple specialized middlewares, implement a single, configuration-driven `AuthenticationMiddleware` that handles all authentication scenarios.

#### Authentication Configuration Model

```csharp
namespace EdFi.DataManagementService.Core.Security.Configuration;

public enum AuthPolicy
{
    /// <summary>
    /// No authentication required - public endpoint
    /// </summary>
    AllowAnonymous,
    
    /// <summary>
    /// Valid JWT required, no specific claims needed
    /// </summary>
    Authenticated,
    
    /// <summary>
    /// Valid JWT with specific role claims required
    /// </summary>
    RequireRoles,
    
    /// <summary>
    /// Valid JWT required with ClientAuthorizations extraction for data access
    /// </summary>
    DataEndpoint
}

public class EndpointAuthConfig
{
    /// <summary>
    /// Path prefix to match (e.g., "/data", "/metadata")
    /// </summary>
    public string PathPrefix { get; set; } = "";
    
    /// <summary>
    /// Authentication policy for this endpoint
    /// </summary>
    public AuthPolicy Policy { get; set; } = AuthPolicy.AllowAnonymous;
    
    /// <summary>
    /// Required roles when Policy is RequireRoles
    /// </summary>
    public string[] RequiredRoles { get; set; } = Array.Empty<string>();
}

public class AuthenticationConfiguration
{
    /// <summary>
    /// Ordered list of endpoint configurations (most specific first)
    /// </summary>
    public List<EndpointAuthConfig> Endpoints { get; set; } = new();
    
    /// <summary>
    /// Default policy if no endpoint matches (security: deny by default)
    /// </summary>
    public AuthPolicy DefaultPolicy { get; set; } = AuthPolicy.Authenticated;
}
```

#### Unified Authentication Middleware

```csharp
namespace EdFi.DataManagementService.Core.Middleware;

internal class AuthenticationMiddleware : IPipelineStep
{
    private readonly IJwtValidationService _jwtValidationService;
    private readonly IApiClientDetailsProvider _clientDetailsProvider;
    private readonly AuthenticationConfiguration _authConfig;
    private readonly ILogger<AuthenticationMiddleware> _logger;

    public AuthenticationMiddleware(
        IJwtValidationService jwtValidationService,
        IApiClientDetailsProvider clientDetailsProvider,
        IOptions<AuthenticationConfiguration> authConfig,
        ILogger<AuthenticationMiddleware> logger)
    {
        _jwtValidationService = jwtValidationService;
        _clientDetailsProvider = clientDetailsProvider;
        _authConfig = authConfig.Value;
        _logger = logger;
    }

    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        var path = requestData.FrontendRequest.Path;
        var config = FindMatchingConfig(path);

        if (config.Policy == AuthPolicy.AllowAnonymous)
        {
            await next();
            return;
        }

        // Extract JWT from Authorization header
        if (!TryExtractToken(requestData.FrontendRequest.Headers, out var token))
        {
            requestData.FrontendResponse = CreateUnauthorizedResponse(
                "Bearer token required",
                requestData.FrontendRequest.TraceId
            );
            return;
        }

        // Validate token
        var (principal, clientAuthorizations) = await _jwtValidationService
            .ValidateAndExtractClientAuthorizationsAsync(token, CancellationToken.None);

        if (principal == null)
        {
            requestData.FrontendResponse = CreateUnauthorizedResponse(
                "Invalid token",
                requestData.FrontendRequest.TraceId
            );
            return;
        }

        // Apply policy-specific authorization
        switch (config.Policy)
        {
            case AuthPolicy.Authenticated:
                // Token validation is sufficient
                break;
                
            case AuthPolicy.RequireRoles:
                if (!HasRequiredRoles(principal, config.RequiredRoles))
                {
                    requestData.FrontendResponse = CreateForbiddenResponse(
                        "Insufficient permissions",
                        requestData.FrontendRequest.TraceId
                    );
                    return;
                }
                break;
                
            case AuthPolicy.DataEndpoint:
                if (clientAuthorizations == null)
                {
                    requestData.FrontendResponse = CreateForbiddenResponse(
                        "Client authorizations required",
                        requestData.FrontendRequest.TraceId
                    );
                    return;
                }
                // Update request with client authorizations
                requestData.FrontendRequest = requestData.FrontendRequest with
                {
                    ClientAuthorizations = clientAuthorizations
                };
                break;
        }

        await next();
    }

    private EndpointAuthConfig FindMatchingConfig(string path)
    {
        // Find most specific matching config
        var match = _authConfig.Endpoints
            .OrderByDescending(e => e.PathPrefix.Length)
            .FirstOrDefault(e => path.StartsWith(e.PathPrefix, StringComparison.OrdinalIgnoreCase));

        return match ?? new EndpointAuthConfig { Policy = _authConfig.DefaultPolicy };
    }

    private bool HasRequiredRoles(ClaimsPrincipal principal, string[] requiredRoles)
    {
        return requiredRoles.All(role => principal.IsInRole(role));
    }
}
```

### 2. Configuration Setup

Default authentication configuration for DMS endpoints:

```json
{
  "Authentication": {
    "DefaultPolicy": "Authenticated",
    "Endpoints": [
      {
        "PathPrefix": "/oauth/token",
        "Policy": "AllowAnonymous"
      },
      {
        "PathPrefix": "/data",
        "Policy": "DataEndpoint"
      },
      {
        "PathPrefix": "/metadata",
        "Policy": "RequireRoles",
        "RequiredRoles": ["service"]
      },
      {
        "PathPrefix": "/",
        "Policy": "AllowAnonymous"
      }
    ]
  }
}
```

### 3. Service Registration Updates

Update `DmsCoreServiceExtensions.cs`:

```csharp
public static IServiceCollection AddDmsAuthentication(
    this IServiceCollection services,
    IConfiguration configuration)
{
    // Configure authentication options
    services.Configure<AuthenticationConfiguration>(
        configuration.GetSection("Authentication"));
    
    // Existing JWT services
    services.AddJwtAuthentication(configuration);
    
    // Register unified authentication middleware
    services.AddTransient<AuthenticationMiddleware>();
    
    return services;
}
```

### 4. Pipeline Integration

Update `ApiService.cs` to use the unified middleware:

```csharp
private List<IPipelineStep> GetCommonInitialSteps()
{
    var steps = new List<IPipelineStep> 
    { 
        new CoreExceptionLoggingMiddleware(_logger),
        // Always add authentication middleware - it handles all scenarios
        _serviceProvider.GetRequiredService<AuthenticationMiddleware>()
    };
    
    return steps;
}
```

## Frontend Changes

### Remove Authentication Configuration

In `WebApplicationBuilderExtensions.cs`, remove:
- All JWT Bearer authentication setup (lines 150-178)
- Authorization policy configuration
- Any authentication-related middleware

### Update Endpoint Modules

Remove all `.RequireAuthorization()` calls from:
- `CoreEndpointModule.cs`
- `MetadataEndpointModule.cs`
- `DiscoveryEndpointModule.cs`
- `TokenEndpointModule.cs`

The endpoints should simply forward requests to Core without any auth checks.

## Migration Strategy

### Phase 1: Shadow Mode (Week 1-2)
1. Implement the unified `AuthenticationMiddleware` in Core
2. Deploy in "audit mode" - log auth decisions but don't enforce
3. Compare with existing frontend auth behavior
4. Identify and fix any discrepancies

### Phase 2: Gradual Rollout (Week 3-4)
1. Enable enforcement for low-risk endpoints first (metadata, discovery)
2. Monitor for any authentication issues
3. Gradually expand to more critical endpoints
4. Use feature flags to control rollout per endpoint

### Phase 3: Data Endpoints (Week 5-6)
1. Migrate data endpoint authentication
2. Ensure ClientAuthorizations extraction works correctly
3. Extensive testing with different client scenarios

### Phase 4: Cleanup (Week 7)
1. Remove all authentication code from frontend
2. Remove ASP.NET Core authentication dependencies
3. Update documentation
4. Performance testing and optimization

## Security Considerations

1. **Deny by Default**: Unmatched endpoints require authentication
2. **Token Validation**: All security checks from ASP.NET Core implementation preserved
3. **Audit Logging**: Log all authentication decisions for security monitoring
4. **Error Responses**: Consistent 401/403 responses with appropriate error details
5. **CORS**: Ensure proper CORS headers for authentication endpoints

## Performance Optimizations

1. **Singleton Services**: ConfigurationManager and JwtValidationService remain singletons
2. **Path Matching**: Use efficient prefix matching with pre-sorted configurations
3. **Caching**: OIDC metadata caching already implemented
4. **Early Exit**: Anonymous endpoints skip all auth processing

## Testing Strategy

### Unit Tests
- Test each AuthPolicy scenario
- Test path matching logic
- Test error responses
- Test role checking

### Integration Tests
- Test full request flow through authentication
- Test with valid/invalid/expired tokens
- Test ClientAuthorizations extraction
- Test endpoint-specific policies

### Performance Tests
- Measure auth overhead for different endpoint types
- Ensure no regression from current implementation
- Load test with concurrent requests

## Rollback Plan

If issues arise during migration:
1. Feature flags allow instant rollback per endpoint
2. Frontend auth code remains until Phase 4
3. Shadow mode logging helps identify issues before enforcement
4. Gradual rollout limits blast radius

## Success Metrics

1. Zero authentication-related code in frontend
2. All endpoints properly secured in Core
3. No performance degradation
4. Successful security audit
5. Simplified deployment and configuration

## Next Steps

1. Review and approve this plan
2. Create implementation tasks
3. Set up feature flags infrastructure
4. Begin Phase 1 implementation
5. Schedule security review checkpoints