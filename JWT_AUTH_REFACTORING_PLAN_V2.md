# JWT Authentication Refactoring Plan V2: ASP.NET Core to DMS Core

## Executive Summary

This document outlines a comprehensive plan to refactor JWT authentication and authorization from the ASP.NET Core frontend layer into the DMS Core middleware layer. The goal is to remove ASP.NET Core dependencies while maintaining all existing functionality using Microsoft.IdentityModel libraries.

**Key Improvements in V2:**
- Enhanced security configuration with detailed TokenValidationParameters
- Characterization testing for ClientAuthorizations parity
- Feature flag-driven migration strategy
- Improved error handling and logging practices
- Performance optimization considerations

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
   - `Microsoft.IdentityModel.Tokens` (v7.x.x)
   - `Microsoft.IdentityModel.Protocols.OpenIdConnect` (v7.x.x)
   - `System.IdentityModel.Tokens.Jwt` (v7.x.x)

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
    private readonly bool _authenticationEnabled;

    public JwtAuthenticationMiddleware(
        IJwtValidationService jwtValidationService,
        ILogger<JwtAuthenticationMiddleware> logger,
        IOptions<JwtAuthenticationOptions> options)
    {
        _jwtValidationService = jwtValidationService;
        _logger = logger;
        _authenticationEnabled = options.Value.Enabled; // Feature flag
    }

    public async Task Execute(RequestData requestData, Func<Task> next)
    {
        // Feature flag check for gradual rollout
        if (!_authenticationEnabled)
        {
            await next();
            return;
        }

        // Extract Authorization header
        // Validate JWT token
        // Create ClientAuthorizations
        // Update FrontendRequest
        // Handle errors with secure error responses
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
- Configure with secure TokenValidationParameters:
  ```csharp
  var validationParameters = new TokenValidationParameters
  {
      // SECURITY CRITICAL: All must be true
      ValidateIssuer = true,
      ValidateAudience = true,
      ValidateIssuerSigningKey = true,
      ValidateLifetime = true,
      RequireExpirationTime = true,
      RequireSignedTokens = true,
      
      // Configured values
      ValidIssuer = oidcConfig.Issuer,
      ValidAudience = _audience,
      IssuerSigningKeys = oidcConfig.SigningKeys,
      
      // Tighten clock skew from default 5 minutes
      ClockSkew = TimeSpan.FromSeconds(30),
      
      // Optional: Add additional validation
      NameClaimType = "name",
      RoleClaimType = _roleClaimType
  };
  ```

#### 1.3 Create JWT Authentication Middleware
- Location: `src/dms/core/EdFi.DataManagementService.Core/Middleware/JwtAuthenticationMiddleware.cs`
- Implement `IPipelineStep` interface
- Extract Authorization header from `FrontendRequest.Headers`
- Validate token using `IJwtValidationService`
- Create `ClientAuthorizations` from validated claims
- Error handling:
  ```csharp
  catch (SecurityTokenExpiredException)
  {
      _logger.LogWarning("Token expired for request {TraceId}", requestData.FrontendRequest.TraceId);
      requestData.FrontendResponse = CreateUnauthorizedResponse("Token expired");
      return;
  }
  catch (SecurityTokenException ex)
  {
      _logger.LogWarning(ex, "Token validation failed for request {TraceId}", requestData.FrontendRequest.TraceId);
      requestData.FrontendResponse = CreateUnauthorizedResponse("Invalid token");
      return;
  }
  ```

#### 1.4 Port ApiClientDetailsProvider Logic
- Move claim extraction logic from `ApiClientDetailsProvider` into middleware
- Extract: scope, jti, namespacePrefixes, educationOrganizationIds
- Maintain exact same claim parsing behavior

### Phase 2: Configuration & DI Setup (Week 1-2)

#### 2.1 Add Configuration Models
```csharp
public class JwtAuthenticationOptions
{
    public bool Enabled { get; set; } = false; // Feature flag - start disabled
    public string Authority { get; set; }
    public string Audience { get; set; }
    public string MetadataAddress { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
    public string RoleClaimType { get; set; }
    public int ClockSkewSeconds { get; set; } = 30;
    
    // ConfigurationManager tuning
    public int RefreshIntervalMinutes { get; set; } = 60;
    public int AutomaticRefreshIntervalHours { get; set; } = 24;
}
```

#### 2.2 Update DI Registration
```csharp
// Configure ConfigurationManager with proper lifecycle
services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(sp =>
{
    var options = sp.GetRequiredService<IOptions<JwtAuthenticationOptions>>().Value;
    var configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
        options.MetadataAddress,
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever { RequireHttps = options.RequireHttpsMetadata })
    {
        RefreshInterval = TimeSpan.FromMinutes(options.RefreshIntervalMinutes),
        AutomaticRefreshInterval = TimeSpan.FromHours(options.AutomaticRefreshIntervalHours)
    };
    
    // Warm up the cache on startup
    _ = configManager.GetConfigurationAsync(CancellationToken.None);
    
    return configManager;
});

// Register services
services.AddSingleton<IJwtValidationService, JwtValidationService>();
services.AddTransient<JwtAuthenticationMiddleware>();
```

#### 2.3 Update Pipeline Configuration
- Add `JwtAuthenticationMiddleware` as first step in pipeline
- Ensure it runs before any authorization-dependent middleware

### Phase 3: Testing Strategy (Week 2)

#### 3.1 Characterization Tests (Golden Master)
```csharp
[Theory]
[InlineData("standard_user_token.jwt", "standard_user_expected.json")]
[InlineData("admin_token.jwt", "admin_expected.json")]
[InlineData("limited_scope_token.jwt", "limited_scope_expected.json")]
public async Task ValidateToken_ProducesIdenticalClientAuthorizations(string tokenFile, string expectedFile)
{
    // Arrange
    var token = File.ReadAllText($"TestData/Tokens/{tokenFile}");
    var expectedJson = File.ReadAllText($"TestData/Expected/{expectedFile}");
    var expected = JsonSerializer.Deserialize<ClientAuthorizations>(expectedJson);
    
    // Act
    var result = await _jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(token);
    
    // Assert
    result.Should().BeEquivalentTo(expected);
}
```

#### 3.2 Security Tests
- Test with expired tokens
- Test with invalid signatures
- Test with wrong audience/issuer
- Test concurrent validation scenarios
- Test OIDC metadata refresh during key rotation

#### 3.3 Performance Tests
- Baseline current ASP.NET Core performance
- Compare with new implementation
- Test cache effectiveness with high concurrency
- Measure first-request latency

### Phase 4: Migration Strategy (Week 2-3)

#### 4.1 Feature Flag Implementation
```json
{
  "JwtAuthentication": {
    "Enabled": false,  // Start disabled
    "EnabledForClients": ["client-id-1", "client-id-2"], // Gradual rollout
    "Authority": "https://keycloak.example.com/realms/edfi",
    "Audience": "ed-fi-ods-api",
    "MetadataAddress": "https://keycloak.example.com/realms/edfi/.well-known/openid-configuration"
  }
}
```

#### 4.2 Parallel Running Phase
1. Deploy with feature flag disabled
2. Enable for internal testing clients
3. Monitor logs and metrics
4. Gradually increase percentage of clients
5. Full rollout once confidence established

#### 4.3 Frontend Cleanup (After Validation)
- Remove JWT Bearer setup from `WebApplicationBuilderExtensions.cs`
- Remove `OnTokenValidated` event handler
- Remove `ApiClientDetailsProvider` registration
- Pass Authorization header unchanged

### Phase 5: Monitoring & Observability

#### 5.1 Metrics to Track
```csharp
// Add metrics collection
services.AddSingleton<IMetrics>(new MetricsBuilder()
    .Report.ToConsole()
    .Build());

// In JwtValidationService
_metrics.Measure.Counter.Increment("jwt.validation.attempts");
_metrics.Measure.Counter.Increment("jwt.validation.success");
_metrics.Measure.Counter.Increment("jwt.validation.failed", new MetricTags("reason", "expired"));
_metrics.Measure.Timer.Time("jwt.validation.duration", async () => { ... });
```

#### 5.2 Logging Best Practices
```csharp
// Good: Log metadata only
_logger.LogWarning("Token validation failed. TokenId: {TokenId}, Reason: {Reason}", 
    GetTokenId(token), ex.GetType().Name);

// Bad: Never log the token itself
_logger.LogWarning("Token validation failed for token: {Token}", token); // NEVER DO THIS
```

## Critical Implementation Details

### Performance Optimizations
1. **OIDC Metadata Caching**
   - ConfigurationManager MUST be singleton
   - Warm up cache on startup
   - Monitor cache hit/miss rates
   - Consider pre-fetching on deployment

2. **Thread Safety**
   - JwtSecurityTokenHandler is thread-safe and reusable
   - No request-specific state in middleware
   - Use concurrent collections where needed

### Security Checklist
- [ ] ValidateIssuer = true
- [ ] ValidateAudience = true  
- [ ] ValidateIssuerSigningKey = true
- [ ] ValidateLifetime = true
- [ ] RequireExpirationTime = true
- [ ] RequireSignedTokens = true (never set to false)
- [ ] ClockSkew <= 60 seconds
- [ ] Never log tokens or sensitive claims
- [ ] Generic error messages to clients
- [ ] Detailed error logging server-side only
- [ ] HTTPS required for OIDC metadata
- [ ] Regular security scans of dependencies

### Error Response Standards
```csharp
private FrontendResponse CreateUnauthorizedResponse(string errorDetail)
{
    return new FrontendResponse(
        StatusCode: 401,
        Body: JsonSerializer.Serialize(new { error = "Unauthorized" }), // Generic to client
        Headers: new Dictionary<string, string> 
        { 
            ["WWW-Authenticate"] = "Bearer error=\"invalid_token\""
        },
        LocationHeaderPath: null,
        ContentType: "application/json"
    );
}
```

## Rollback Plan

### Quick Rollback via Feature Flag
1. Set `JwtAuthentication:Enabled` to `false`
2. Frontend continues to handle authentication
3. No deployment needed

### Full Rollback Steps
1. Revert frontend changes if deployed
2. Remove middleware from pipeline
3. Keep code for future retry

## Success Criteria
1. All characterization tests passing (100% parity)
2. No performance degradation (<5ms added latency)
3. Zero security vulnerabilities in scan
4. Successful canary deployment for 24 hours
5. All integration tests passing
6. Monitoring shows healthy metrics

## Appendix: Complete Implementation Examples

### JWT Validation Service
```csharp
public class JwtValidationService : IJwtValidationService
{
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly JwtSecurityTokenHandler _tokenHandler;
    private readonly JwtAuthenticationOptions _options;
    private readonly ILogger<JwtValidationService> _logger;
    private readonly IMetrics _metrics;

    public JwtValidationService(
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
        IOptions<JwtAuthenticationOptions> options,
        ILogger<JwtValidationService> logger,
        IMetrics metrics)
    {
        _configurationManager = configurationManager;
        _tokenHandler = new JwtSecurityTokenHandler();
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
    }

    public async Task<(ClaimsPrincipal? Principal, ClientAuthorizations? ClientAuthorizations)> 
        ValidateAndExtractClientAuthorizationsAsync(string token, CancellationToken cancellationToken)
    {
        using var timer = _metrics.Measure.Timer.Time("jwt.validation.duration");
        
        try
        {
            _metrics.Measure.Counter.Increment("jwt.validation.attempts");
            
            var oidcConfig = await _configurationManager.GetConfigurationAsync(cancellationToken);
            
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = oidcConfig.Issuer,
                
                ValidateAudience = true,
                ValidAudience = _options.Audience,
                
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = oidcConfig.SigningKeys,
                
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                
                ClockSkew = TimeSpan.FromSeconds(_options.ClockSkewSeconds),
                
                NameClaimType = ClaimTypes.Name,
                RoleClaimType = _options.RoleClaimType
            };

            var principal = _tokenHandler.ValidateToken(
                token, 
                validationParameters, 
                out SecurityToken validatedToken);
            
            var clientAuthorizations = ExtractClientAuthorizations(principal, validatedToken);
            
            _metrics.Measure.Counter.Increment("jwt.validation.success");
            
            return (principal, clientAuthorizations);
        }
        catch (SecurityTokenExpiredException)
        {
            _metrics.Measure.Counter.Increment("jwt.validation.failed", 
                new MetricTags("reason", "expired"));
            _logger.LogWarning("Token validation failed: Token expired");
            return (null, null);
        }
        catch (SecurityTokenException ex)
        {
            _metrics.Measure.Counter.Increment("jwt.validation.failed", 
                new MetricTags("reason", "invalid"));
            _logger.LogWarning(ex, "Token validation failed");
            return (null, null);
        }
    }

    private ClientAuthorizations ExtractClientAuthorizations(
        ClaimsPrincipal principal, 
        SecurityToken validatedToken)
    {
        var jwtToken = validatedToken as JwtSecurityToken;
        var claims = principal.Claims.ToList();
        
        // Port exact logic from ApiClientDetailsProvider
        var claimSetName = claims.FirstOrDefault(c => c.Type == "scope")?.Value ?? string.Empty;
        var tokenId = claims.FirstOrDefault(c => c.Type == "jti")?.Value 
            ?? jwtToken?.RawData?.GetHashCode().ToString() 
            ?? string.Empty;
            
        var namespacePrefixes = claims
            .FirstOrDefault(c => c.Type == "namespacePrefixes")?.Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            
        var educationOrganizationIds = claims
            .FirstOrDefault(c => c.Type == "educationOrganizationIds")?.Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(id => new EducationOrganizationId(long.Parse(id)))
            .ToList() ?? new List<EducationOrganizationId>();

        return new ClientAuthorizations(
            TokenId: tokenId,
            ClaimSetName: claimSetName,
            EducationOrganizationIds: educationOrganizationIds,
            NamespacePrefixes: namespacePrefixes.Select(np => new NamespacePrefix(np)).ToList()
        );
    }
}
```
