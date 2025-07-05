# JWT Authentication Configuration Example

## appsettings.json Configuration

Add the following section to your `appsettings.json` file to configure JWT authentication in DMS Core:

```json
{
  "JwtAuthentication": {
    "Enabled": false,
    "EnabledForClients": [],
    "Authority": "https://your-keycloak-instance.com/realms/edfi",
    "Audience": "ed-fi-ods-api",
    "MetadataAddress": "https://your-keycloak-instance.com/realms/edfi/.well-known/openid-configuration",
    "RequireHttpsMetadata": true,
    "RoleClaimType": "role",
    "ClockSkewSeconds": 30,
    "RefreshIntervalMinutes": 60,
    "AutomaticRefreshIntervalHours": 24
  }
}
```

## Configuration Options Explained

### Basic Settings
- **Enabled**: `false` - Feature flag to enable/disable JWT authentication in DMS Core
- **EnabledForClients**: `[]` - List of client IDs for gradual rollout (empty = all clients when enabled)

### OIDC/OAuth Settings
- **Authority**: The base URL of your identity provider (Keycloak realm URL)
- **Audience**: Expected audience value in JWT tokens (must match Keycloak client configuration)
- **MetadataAddress**: Full URL to OIDC discovery endpoint
- **RequireHttpsMetadata**: `true` - Enforce HTTPS for metadata endpoints (recommended for production)

### Token Validation Settings
- **RoleClaimType**: `"role"` - The claim type used for role-based authorization
- **ClockSkewSeconds**: `30` - Tolerance for time differences between servers (reduced from default 5 minutes)

### Performance Settings
- **RefreshIntervalMinutes**: `60` - How often to check for updated OIDC metadata
- **AutomaticRefreshIntervalHours**: `24` - Automatic background refresh interval

## Migration Strategy

### Phase 1: Deploy with JWT Disabled
```json
{
  "JwtAuthentication": {
    "Enabled": false
  }
}
```

### Phase 2: Enable for Specific Clients
```json
{
  "JwtAuthentication": {
    "Enabled": true,
    "EnabledForClients": ["test-client-1", "test-client-2"]
  }
}
```

### Phase 3: Enable for All Clients
```json
{
  "JwtAuthentication": {
    "Enabled": true,
    "EnabledForClients": []
  }
}
```

## Environment Variable Overrides

You can override these settings using environment variables:

```bash
# Enable JWT authentication
export JwtAuthentication__Enabled=true

# Set Keycloak authority
export JwtAuthentication__Authority=https://keycloak.example.com/realms/edfi

# Set audience
export JwtAuthentication__Audience=ed-fi-ods-api

# Set metadata address
export JwtAuthentication__MetadataAddress=https://keycloak.example.com/realms/edfi/.well-known/openid-configuration
```

## Docker Compose Example

```yaml
services:
  dms:
    image: edfi/data-management-service:latest
    environment:
      - JwtAuthentication__Enabled=true
      - JwtAuthentication__Authority=https://keycloak:8080/realms/edfi
      - JwtAuthentication__Audience=ed-fi-ods-api
      - JwtAuthentication__MetadataAddress=https://keycloak:8080/realms/edfi/.well-known/openid-configuration
      - JwtAuthentication__RequireHttpsMetadata=false  # Only for dev/test with self-signed certs
```

## Monitoring and Troubleshooting

### Log Messages to Watch For

1. **JWT Authentication Disabled**
   ```
   JWT authentication is disabled, skipping middleware
   ```

2. **Successful Token Validation**
   ```
   JWT authentication successful for TokenId: {TokenId} - {TraceId}
   ```

3. **Failed Token Validation**
   ```
   Token validation failed - {TraceId}
   ```

4. **Configuration Errors**
   ```
   JwtAuthentication:MetadataAddress must be configured when JWT authentication is enabled
   ```

### Common Issues

1. **Clock Skew Errors**: Increase `ClockSkewSeconds` if seeing "token used before issued" errors
2. **Metadata Fetch Failures**: Ensure `MetadataAddress` is reachable from the DMS container
3. **Audience Mismatch**: Verify `Audience` matches the Keycloak client configuration
4. **HTTPS Requirements**: Set `RequireHttpsMetadata` to `false` only in development environments