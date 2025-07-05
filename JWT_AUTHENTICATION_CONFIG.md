# JWT Authentication Configuration

After removing JWT authentication from the frontend, the following configuration needs to be added to enable JWT authentication in DMS Core.

## Configuration Changes

### appsettings.json

Add the following section to your appsettings.json:

```json
{
  "JwtAuthentication": {
    "Enabled": true,
    "Authority": "${IdentitySettings:Authority}",
    "Audience": "${IdentitySettings:Audience}",
    "MetadataAddress": "${IdentitySettings:Authority}/.well-known/openid-configuration",
    "RequireHttpsMetadata": true,
    "RoleClaimType": "role",
    "ClientRole": "service",
    "ClockSkewSeconds": 30,
    "RefreshIntervalMinutes": 60,
    "AutomaticRefreshIntervalHours": 24
  }
}
```

### Environment-specific settings

For development (appsettings.Development.json):
```json
{
  "JwtAuthentication": {
    "RequireHttpsMetadata": false
  }
}
```

## What Changed

1. **Frontend Changes:**
   - Removed all JWT authentication configuration from WebApplicationBuilderExtensions.cs
   - Removed RequireAuthorization from endpoint mappings
   - Removed UseAuthentication and UseAuthorization middleware
   - Deleted SecurityConstants.cs

2. **Core Changes:**
   - Added JwtRoleAuthenticationMiddleware for non-data endpoints
   - Added ClientRole property to JwtAuthenticationOptions
   - Existing JwtAuthenticationMiddleware handles data endpoints with ClientAuthorizations

## How It Works

- **Data endpoints (/data/**)**: Use JwtAuthenticationMiddleware which validates JWT and extracts ClientAuthorizations
- **Non-data endpoints**: Would need to be routed through Core to use JwtRoleAuthenticationMiddleware for role-based auth
- All JWT validation happens in Core using Microsoft.IdentityModel libraries only

## Migration Notes

1. The IdentitySettings values from the frontend configuration should be moved to JwtAuthentication section
2. The frontend now acts as a pure pass-through, forwarding the Authorization header to Core
3. All authentication decisions are made in Core based on the JWT token