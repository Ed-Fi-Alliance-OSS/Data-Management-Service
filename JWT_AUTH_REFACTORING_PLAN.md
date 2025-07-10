# JWT Authentication Refactoring Plan: ASP.NET Core to DMS Core

## Executive Summary

This document outlines a comprehensive plan to refactor JWT authentication and authorization from the ASP.NET Core frontend layer into the DMS Core middleware layer. The goal is to remove ASP.NET Core dependencies while maintaining all existing functionality using Microsoft.IdentityModel libraries.

## Current Architecture Analysis

### Existing Implementation
1. **ASP.NET Core Frontend Layer**
   - Uses `Microsoft.AspNetCore.Authentication.JwtBearer` for JWT validation
   - Configures authentication in `WebApplicationBuilderExtensions.cs`
   - Validates tokens via middleware pipeline
   - Extracts claims in `OnTokenValidated` event
   - Stores `ClientAuthorizations` in `HttpContext.Items`

2. **Token Processing Flow**
   ```
   Client Request → ASP.NET Core Auth Middleware → Token Validation 
   → OnTokenValidated Event → Extract Claims → Create ClientAuthorizations 
   → Store in HttpContext → Pass to DMS Core
   ```

3. **Key Components**
   - `ApiClientDetailsProvider`: Extracts Ed-Fi specific claims from JWT
   - `ClientAuthorizations`: Contains TokenId, ClaimSetName, EducationOrganizationIds, NamespacePrefixes
   - `FrontendRequest`: Includes pre-processed ClientAuthorizations

### Dependencies to Remove
- `Microsoft.AspNetCore.Authentication.JwtBearer`
- `Microsoft.AspNetCore.Authentication`
- `Microsoft.AspNetCore.Authorization`
- HttpContext-based storage mechanisms

## Proposed Architecture

### New Implementation in DMS Core
1. **Core Libraries (No ASP.NET Dependencies)**
   - `Microsoft.IdentityModel.Tokens`
   - `Microsoft.IdentityModel.Protocols.OpenIdConnect`
   - `System.IdentityModel.Tokens.Jwt`

2. **New Token Processing Flow**
   ```
   Client Request → Frontend (pass-through) → DMS Core Pipeline 
   → JwtAuthenticationMiddleware → Token Validation → Extract Claims 
   → Create ClientAuthorizations → Continue Pipeline
   ```

3. **Key New Components**

#### JwtAuthenticationMiddleware
```csharp
public class JwtAuthenticationMiddleware : IPipelineStep
{
    private readonly IJwtValidationService _jwtValidationService;
    private readonly ILogger<JwtAuthenticationMiddleware> _logger;

    public JwtAuthenticationMiddleware(
        IJwtValidationService jwtValidationService,
        ILogger<JwtAuthenticationMiddleware> logger)
    {
        _jwtValidationService = jwtValidationService;
        _logger = logger;
    }

    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        // Extract Authorization header
        // Validate JWT token
        // Create ClientAuthorizations
        // Update FrontendRequest
        // Handle errors appropriately
    }
}
```

#### IJwtValidationService (Singleton)
```csharp
public interface IJwtValidationService
{
    Task<ClaimsPrincipal?> ValidateTokenAsync(string token, CancellationToken cancellationToken);
}

public class JwtValidationService : IJwtValidationService
{
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly TokenValidationParameters _baseValidationParameters;
    
    // Singleton service that manages OIDC metadata caching
    // and provides thread-safe token validation
}
```

## Implementation Plan

### Phase 1: Core Infrastructure (Week 1)

#### 1.1 Add Required NuGet Packages
- Add to `EdFi.DataManagementService.Core.csproj`:
  ```xml
  <PackageReference Include="Microsoft.IdentityModel.Tokens" Version="7.x.x" />
  <PackageReference Include="Microsoft.IdentityModel.Protocols.OpenIdConnect" Version="7.x.x" />
  <PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="7.x.x" />
  ```

#### 1.2 Create JWT Validation Service
- Location: `src/dms/core/EdFi.DataManagementService.Core/Security/JwtValidationService.cs`
- Implement singleton service with proper OIDC metadata caching
- Handle token validation with configurable parameters
- Ensure thread-safety for high-performance scenarios

#### 1.3 Create JWT Authentication Middleware
- Location: `src/dms/core/EdFi.DataManagementService.Core/Middleware/JwtAuthenticationMiddleware.cs`
- Implement `IPipelineStep` interface
- Extract Authorization header from `FrontendRequest.Headers`
- Validate token using `IJwtValidationService`
- Create `ClientAuthorizations` from validated claims
- Update `RequestData.FrontendRequest` with authorization info

#### 1.4 Port ApiClientDetailsProvider Logic
- Move claim extraction logic from `ApiClientDetailsProvider` into middleware
- Extract: scope, jti, namespacePrefixes, educationOrganizationIds
- Maintain exact same claim parsing behavior

### Phase 2: Configuration & DI Setup (Week 1-2)

#### 2.1 Add Configuration Models
```csharp
public class JwtAuthenticationOptions
{
    public string Authority { get; set; }
    public string Audience { get; set; }
    public string MetadataAddress { get; set; }
    public bool RequireHttpsMetadata { get; set; }
    public string RoleClaimType { get; set; }
    public int ClockSkewMinutes { get; set; } = 5;
}
```

#### 2.2 Update DI Registration
- Register `JwtValidationService` as Singleton
- Register `JwtAuthenticationMiddleware` as Transient
- Configure `TokenValidationParameters` from settings
- Set up `ConfigurationManager<OpenIdConnectConfiguration>` as Singleton

#### 2.3 Update Pipeline Configuration
- Add `JwtAuthenticationMiddleware` as first step in pipeline
- Ensure it runs before any authorization-dependent middleware

### Phase 3: Frontend Refactoring (Week 2)

#### 3.1 Remove Authentication Configuration
- Remove JWT Bearer setup from `WebApplicationBuilderExtensions.cs`
- Remove `OnTokenValidated` event handler
- Remove `ApiClientDetailsProvider` registration

#### 3.2 Update AspNetCoreFrontend
- Remove extraction of ClientAuthorizations from HttpContext
- Pass Authorization header unchanged in FrontendRequest
- Remove dependency on authentication-related HttpContext items

#### 3.3 Clean Up Dependencies
- Remove NuGet references to ASP.NET Core authentication packages
- Update using statements to remove authentication namespaces

### Phase 4: Testing & Validation (Week 2-3)

#### 4.1 Unit Tests
- Test `JwtValidationService` with valid/invalid tokens
- Test `JwtAuthenticationMiddleware` pipeline behavior
- Test claim extraction and ClientAuthorizations creation
- Test error handling for various failure scenarios

#### 4.2 Integration Tests
- End-to-end authentication flow testing
- Test with real Keycloak instance
- Verify backward compatibility
- Performance testing for concurrent requests

#### 4.3 Security Validation
- Ensure tokens are not logged
- Verify proper error messages (no sensitive data)
- Test token expiration handling
- Validate OIDC metadata refresh

### Phase 5: Deployment & Migration (Week 3)

#### 5.1 Configuration Migration
- Update appsettings.json with new JWT configuration section
- Document environment variable mappings
- Create migration guide for operators

#### 5.2 Rollout Strategy
- Deploy to development environment first
- Run parallel testing with existing system
- Gradual rollout to staging/production
- Maintain rollback capability

## Critical Implementation Details

### Performance Considerations
1. **OIDC Metadata Caching**
   - ConfigurationManager MUST be singleton
   - Respects HTTP cache headers from identity provider
   - Prevents redundant metadata fetches

2. **Thread Safety**
   - JwtSecurityTokenHandler is thread-safe and reusable
   - No request-specific state in middleware class
   - Proper async/await usage throughout

### Error Handling Strategy
1. **Invalid/Missing Token**: Return 401 Unauthorized
2. **Expired Token**: Return 401 with specific error message
3. **Malformed Token**: Return 401 with parsing error
4. **OIDC Metadata Fetch Failure**: Return 503 Service Unavailable
5. **Configuration Errors**: Fail fast during startup

### Security Best Practices
1. Never log token contents
2. Use secure defaults for token validation
3. Implement proper clock skew handling
4. Validate all required claims
5. Use HTTPS for OIDC metadata fetches

## Risks and Mitigation

### Risk 1: Performance Degradation
- **Mitigation**: Proper singleton usage for caching components
- **Monitoring**: Add performance metrics for auth operations

### Risk 2: Breaking Changes
- **Mitigation**: Maintain exact same ClientAuthorizations structure
- **Testing**: Comprehensive integration tests

### Risk 3: OIDC Provider Compatibility
- **Mitigation**: Test with actual Keycloak instance early
- **Flexibility**: Make validation parameters configurable

## Success Criteria
1. All existing authentication functionality preserved
2. No ASP.NET Core authentication dependencies in Core
3. Performance equal or better than current implementation
4. All tests passing with >95% code coverage
5. Successfully deployed to all environments

## Timeline
- **Week 1**: Core infrastructure and middleware development
- **Week 2**: Frontend refactoring and integration
- **Week 3**: Testing, validation, and deployment

## Appendix: Code Examples

### Example: JWT Validation Service Implementation
```csharp
public class JwtValidationService : IJwtValidationService
{
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly TokenValidationParameters _baseValidationParameters;
    private readonly ILogger<JwtValidationService> _logger;

    public JwtValidationService(
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
        IOptions<JwtAuthenticationOptions> options,
        ILogger<JwtValidationService> logger)
    {
        _configurationManager = configurationManager;
        _tokenHandler = new JwtSecurityTokenHandler();
        _logger = logger;
        
        _baseValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudience = options.Value.Audience,
            ValidateIssuer = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(options.Value.ClockSkewMinutes)
        };
    }

    public async Task<ClaimsPrincipal?> ValidateTokenAsync(
        string token, 
        CancellationToken cancellationToken)
    {
        try
        {
            var oidcConfig = await _configurationManager.GetConfigurationAsync(cancellationToken);
            
            var validationParameters = _baseValidationParameters.Clone();
            validationParameters.ValidIssuer = oidcConfig.Issuer;
            validationParameters.IssuerSigningKeys = oidcConfig.SigningKeys;

            var principal = _tokenHandler.ValidateToken(
                token, 
                validationParameters, 
                out SecurityToken validatedToken);
                
            return principal;
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Token validation failed");
            return null;
        }
    }
}
```

### Example: Middleware Integration
```csharp
public async Task Execute(RequestData requestData, Func<Task> next)
{
    if (!requestData.FrontendRequest.Headers.TryGetValue("Authorization", out var authHeader) 
        || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        requestData.FrontendResponse = new FrontendResponse(
            StatusCode: 401,
            Body: null,
            Headers: new Dictionary<string, string> { ["WWW-Authenticate"] = "Bearer" },
            LocationHeaderPath: null,
            ContentType: "application/json"
        );
        return;
    }

    var token = authHeader.Substring("Bearer ".Length);
    var principal = await _jwtValidationService.ValidateTokenAsync(token, CancellationToken.None);
    
    if (principal == null)
    {
        requestData.FrontendResponse = new FrontendResponse(
            StatusCode: 401,
            Body: JsonSerializer.Serialize(new { error = "Invalid token" }),
            Headers: new Dictionary<string, string> { ["WWW-Authenticate"] = "Bearer error=\"invalid_token\"" },
            LocationHeaderPath: null,
            ContentType: "application/json"
        );
        return;
    }

    var clientAuthorizations = ExtractClientAuthorizations(principal);
    requestData.FrontendRequest = requestData.FrontendRequest with 
    { 
        ClientAuthorizations = clientAuthorizations 
    };
    
    await next();
}
```