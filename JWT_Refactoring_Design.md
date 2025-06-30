# JWT Token Processing Refactoring Design

## Executive Summary

This document outlines the design for refactoring JWT token processing from the ASP.NET Core frontend layer to the DMS Core layer. The refactoring will move JWT validation and claims extraction into a new middleware component called `DecodeJwtToClientAuthorizationsMiddleware`, eliminating ASP.NET Core dependencies from the core authentication flow.

## Current Architecture

### Current Flow
1. **Frontend Layer** (ASP.NET Core)
   - JWT Bearer authentication configured in `WebApplicationBuilderExtensions.cs`
   - `OnTokenValidated` event extracts claims
   - `ClientAuthorizations` stored in `HttpContext.Items`
   - `AspNetCoreFrontend` retrieves and includes in `FrontendRequest`

2. **Core Layer**
   - Receives `ClientAuthorizations` via `FrontendRequest`
   - Authorization middlewares use `requestData.FrontendRequest.ClientAuthorizations`

### Dependencies
- Frontend: `Microsoft.AspNetCore.Authentication.JwtBearer`
- Core: No JWT-specific dependencies

## Proposed Architecture

### New Flow
1. **Frontend Layer** (ASP.NET Core)
   - Remove JWT Bearer authentication
   - Pass raw Authorization header in `FrontendRequest.Headers`
   - Remove `ClientAuthorizations` from `FrontendRequest` constructor

2. **Core Layer**
   - New `DecodeJwtToClientAuthorizationsMiddleware` validates JWT
   - Extracts claims and creates `ClientAuthorizations`
   - Stores in `RequestData.ClientAuthorizations` (new property)
   - Authorization middlewares use `requestData.ClientAuthorizations`

### New Dependencies
- Core: 
  - `System.IdentityModel.Tokens.Jwt` (for JWT parsing)
  - `Microsoft.IdentityModel.Tokens` (for token validation)

## Component Design

### 1. RequestData Modification

```csharp
// EdFi.DataManagementService.Core/Pipeline/RequestData.cs
internal class RequestData
{
    // Existing properties...
    
    /// <summary>
    /// Client authorization details extracted from JWT token.
    /// Populated by DecodeJwtToClientAuthorizationsMiddleware.
    /// </summary>
    public ClientAuthorizations? ClientAuthorizations { get; set; }
}
```

### 2. FrontendRequest Modification

```csharp
// EdFi.DataManagementService.Core.External/Frontend/FrontendRequest.cs
public record FrontendRequest(
    string Path,
    string? Body,
    Dictionary<string, string> Headers,
    Dictionary<string, string> QueryParameters,
    TraceId TraceId
    // REMOVED: ClientAuthorizations ClientAuthorizations
);
```

### 3. DecodeJwtToClientAuthorizationsMiddleware

```csharp
// EdFi.DataManagementService.Core/Middleware/DecodeJwtToClientAuthorizationsMiddleware.cs
internal class DecodeJwtToClientAuthorizationsMiddleware(
    ILogger<DecodeJwtToClientAuthorizationsMiddleware> _logger,
    IJwtTokenValidator _jwtTokenValidator,
    IApiClientDetailsProvider _apiClientDetailsProvider,
    IOptions<IdentitySettings> _identitySettings
) : IPipelineStep
{
    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        _logger.LogDebug("Entering DecodeJwtToClientAuthorizationsMiddleware - {TraceId}", 
            requestData.FrontendRequest.TraceId.Value);

        // Extract Authorization header
        if (!requestData.FrontendRequest.Headers.TryGetValue("Authorization", out var authHeader))
        {
            RespondUnauthorized(requestData, "Missing Authorization header");
            return;
        }

        // Validate Bearer format
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            RespondUnauthorized(requestData, "Invalid Authorization header format");
            return;
        }

        string token = authHeader["Bearer ".Length..];

        // Validate JWT
        var validationResult = await _jwtTokenValidator.ValidateTokenAsync(token, _identitySettings.Value);
        if (!validationResult.IsValid)
        {
            RespondUnauthorized(requestData, validationResult.ErrorMessage);
            return;
        }

        // Extract client authorizations from claims
        var tokenHash = token.GetHashCode().ToString();
        var clientAuthorizations = _apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(
            tokenHash,
            validationResult.Claims
        );

        // Store in RequestData
        requestData.ClientAuthorizations = clientAuthorizations;

        await next();
    }

    private static void RespondUnauthorized(RequestData requestData, string error)
    {
        requestData.FrontendResponse = new FrontendResponse(
            StatusCode: 401,
            Body: JsonSerializer.Serialize(new { error }),
            Headers: [],
            ContentType: MediaTypeNames.Application.Json
        );
    }
}
```

### 4. JWT Token Validator

```csharp
// EdFi.DataManagementService.Core/Security/JwtTokenValidator.cs
public interface IJwtTokenValidator
{
    Task<JwtValidationResult> ValidateTokenAsync(string token, IdentitySettings settings);
}

public record JwtValidationResult(
    bool IsValid,
    List<Claim> Claims,
    string ErrorMessage = ""
);

internal class JwtTokenValidator : IJwtTokenValidator
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<JwtTokenValidator> _logger;
    private readonly SemaphoreSlim _configSemaphore = new(1, 1);
    private ConfigurationManager<OpenIdConnectConfiguration>? _configurationManager;

    public JwtTokenValidator(HttpClient httpClient, ILogger<JwtTokenValidator> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<JwtValidationResult> ValidateTokenAsync(string token, IdentitySettings settings)
    {
        try
        {
            var configurationManager = await GetConfigurationManagerAsync(settings);
            var config = await configurationManager.GetConfigurationAsync();

            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = settings.Authority,
                ValidateAudience = true,
                ValidAudience = settings.Audience,
                ValidateLifetime = true,
                IssuerSigningKeys = config.SigningKeys,
                ClockSkew = TimeSpan.FromMinutes(5)
            };

            var principal = tokenHandler.ValidateToken(token, validationParameters, out _);
            return new JwtValidationResult(true, principal.Claims.ToList());
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogDebug(ex, "JWT validation failed");
            return new JwtValidationResult(false, [], ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during JWT validation");
            return new JwtValidationResult(false, [], "Token validation failed");
        }
    }

    private async Task<ConfigurationManager<OpenIdConnectConfiguration>> GetConfigurationManagerAsync(
        IdentitySettings settings)
    {
        await _configSemaphore.WaitAsync();
        try
        {
            if (_configurationManager == null)
            {
                var metadataAddress = $"{settings.Authority}/.well-known/openid-configuration";
                _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    metadataAddress,
                    new OpenIdConnectConfigurationRetriever(),
                    new HttpDocumentRetriever(_httpClient) { RequireHttps = settings.RequireHttpsMetadata }
                );
            }
            return _configurationManager;
        }
        finally
        {
            _configSemaphore.Release();
        }
    }
}
```

### 5. IdentitySettings Migration

```csharp
// EdFi.DataManagementService.Core/Configuration/IdentitySettings.cs
namespace EdFi.DataManagementService.Core.Configuration;

public class IdentitySettings
{
    public required string Authority { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
    public required string Audience { get; set; }
    public required string RoleClaimType { get; set; }
    public required string ClientRole { get; set; }
}
```

## Implementation Steps

### Phase 1: Core Infrastructure (No Breaking Changes)
1. Add JWT package references to Core project
2. Create `IdentitySettings` in Core (duplicate for now)
3. Implement `IJwtTokenValidator` and `JwtTokenValidator`
4. Add `ClientAuthorizations` property to `RequestData`
5. Create `DecodeJwtToClientAuthorizationsMiddleware`
6. Register new services in `DmsCoreServiceExtensions`

### Phase 2: Pipeline Integration
1. Add middleware to pipeline in `ApiService.cs`
   - Position after `ValidateEndpointMiddleware`
   - Position before `ResourceActionAuthorizationMiddleware`
2. Update authorization middlewares to check both locations:
   ```csharp
   var clientAuth = requestData.ClientAuthorizations 
       ?? requestData.FrontendRequest.ClientAuthorizations;
   ```

### Phase 3: Frontend Simplification
1. Remove JWT Bearer authentication from `WebApplicationBuilderExtensions`
2. Remove `OnTokenValidated` event handler
3. Update `AspNetCoreFrontend` to not retrieve `ApiClientDetails`
4. Remove `ClientAuthorizations` parameter from `FrontendRequest` constructor

### Phase 4: Cleanup
1. Update all authorization middlewares to use only `requestData.ClientAuthorizations`
2. Remove `IdentitySettings` from frontend project
3. Remove JWT Bearer package reference from frontend
4. Update all tests

## Service Registration

```csharp
// In DmsCoreServiceExtensions.cs
public static IServiceCollection AddDmsCore(this IServiceCollection services, IConfiguration configuration)
{
    // Existing registrations...
    
    // JWT validation services
    services.Configure<IdentitySettings>(configuration.GetSection("IdentitySettings"));
    services.AddHttpClient<IJwtTokenValidator, JwtTokenValidator>();
    services.AddSingleton<IJwtTokenValidator, JwtTokenValidator>();
    
    // Middleware registration
    services.AddTransient<DecodeJwtToClientAuthorizationsMiddleware>();
    
    return services;
}
```

## Pipeline Configuration

```csharp
// In ApiService.cs
List<IPipelineStep> pipeline =
[
    // Basic validation
    _serviceProvider.GetRequiredService<ValidateEndpointMiddleware>(),
    _serviceProvider.GetRequiredService<ValidateContentTypeMiddleware>(),
    
    // JWT validation (NEW)
    _serviceProvider.GetRequiredService<DecodeJwtToClientAuthorizationsMiddleware>(),
    
    // Authorization
    _serviceProvider.GetRequiredService<ResourceActionAuthorizationMiddleware>(),
    // ... rest of pipeline
];
```

## Testing Strategy

### Unit Tests
1. `JwtTokenValidator` tests
   - Valid token scenarios
   - Expired token handling
   - Invalid signature handling
   - Missing claims handling

2. `DecodeJwtToClientAuthorizationsMiddleware` tests
   - Missing Authorization header
   - Invalid Bearer format
   - Valid token processing
   - Invalid token handling

### Integration Tests
1. End-to-end JWT validation flow
2. Authorization middleware compatibility
3. Performance impact assessment

## Security Considerations

1. **Token Caching**: Consider caching validated tokens to improve performance
2. **Clock Skew**: Set appropriate clock skew (5 minutes recommended)
3. **HTTPS Requirement**: Maintain `RequireHttpsMetadata` setting
4. **Error Messages**: Avoid exposing detailed validation errors to clients

## Migration Risks and Mitigations

### Risks
1. **Performance Impact**: Additional HTTP call for OIDC configuration
   - **Mitigation**: Cache configuration with appropriate refresh interval

2. **Breaking Changes**: Removing `ClientAuthorizations` from `FrontendRequest`
   - **Mitigation**: Phased approach with backward compatibility

3. **Testing Coverage**: Ensuring all authorization scenarios still work
   - **Mitigation**: Comprehensive test suite before migration

## Success Criteria

1. JWT validation works identically to current implementation
2. No breaking changes for API consumers
3. Frontend has no JWT-specific dependencies
4. All existing authorization logic continues to function
5. Performance impact is minimal (< 5ms per request)

## Timeline Estimate

- Phase 1: 2-3 days (Core infrastructure)
- Phase 2: 1-2 days (Pipeline integration)
- Phase 3: 1 day (Frontend cleanup)
- Phase 4: 2-3 days (Testing and cleanup)
- **Total**: 6-9 days

## Alternative Approaches Considered

1. **Keep JWT validation in frontend**: Rejected due to requirement to move to Core
2. **Create separate auth service**: Rejected as overly complex
3. **Use custom JWT library**: Rejected in favor of Microsoft's official libraries

## Conclusion

This design provides a clean separation of concerns, moving JWT validation logic from the ASP.NET Core frontend into the DMS Core pipeline. The phased approach ensures backward compatibility while gradually migrating to the new architecture. The use of standard .NET JWT libraries ensures maintainability and security.