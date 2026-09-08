// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Service for validating JWT tokens using OIDC metadata
/// </summary>
internal class JwtValidationService(
    IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
    IOptions<JwtAuthenticationOptions> options,
    ILogger<JwtValidationService> logger,
    TimeProvider? timeProvider = null
) : IJwtValidationService
{
    private const int ValidationParametersCacheMaxEntries = 16;
    private static readonly TimeSpan CachePruneInterval = TimeSpan.FromSeconds(15);

    private readonly JwtSecurityTokenHandler _tokenHandler = new();
    private readonly JwtAuthenticationOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<TokenCacheKey, CachedTokenValidationResult> _validatedTokenCache =
        new();
    private readonly ConcurrentDictionary<string, CachedValidationParameters> _validationParametersCache = [];
    private readonly ConditionalWeakTable<SecurityKey, string> _signingKeyMaterialFingerprints = new();
    private readonly object _cacheMaintenanceLock = new();
    private DateTimeOffset _lastCacheMaintenance = DateTimeOffset.MinValue;

    internal int ValidatedTokenCacheCount => _validatedTokenCache.Count;
    internal int ValidationParametersCacheCount => _validationParametersCache.Count;

    /// <summary>
    /// Validates a JWT token and extracts client authorization information.
    /// </summary>
    /// <param name="token">The JWT token to validate</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>A tuple containing the validated ClaimsPrincipal and extracted ClientAuthorizations, or (null, null) if validation fails</returns>
    public Task<(
        ClaimsPrincipal? Principal,
        ClientAuthorizations? ClientAuthorizations
    )> ValidateAndExtractClientAuthorizationsAsync(string token, CancellationToken cancellationToken)
    {
        return ValidateAndExtractClientAuthorizationsAsync(token, 0, cancellationToken);
    }

    /// <summary>
    /// Validates a JWT token embedded in an Authorization header and extracts client authorization information.
    /// </summary>
    /// <param name="authorizationHeader">The full Authorization header value, or the token itself when tokenStartIndex is 0</param>
    /// <param name="tokenStartIndex">The index at which the bearer token starts</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>A tuple containing the validated ClaimsPrincipal and extracted ClientAuthorizations, or (null, null) if validation fails</returns>
    public async Task<(
        ClaimsPrincipal? Principal,
        ClientAuthorizations? ClientAuthorizations
    )> ValidateAndExtractClientAuthorizationsAsync(
        string authorizationHeader,
        int tokenStartIndex,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if ((uint)tokenStartIndex > (uint)authorizationHeader.Length)
            {
                logger.LogWarning("Token validation failed: bearer token offset was outside the header");
                return (null, null);
            }

            OpenIdConnectConfiguration oidcConfig = await configurationManager.GetConfigurationAsync(
                cancellationToken
            );

            string validationFingerprint = CreateValidationFingerprint(oidcConfig);
            TokenCacheKey cacheKey = CreateTokenCacheKey(
                authorizationHeader,
                tokenStartIndex,
                validationFingerprint
            );
            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (TryGetCachedValidation(cacheKey, now, out CachedTokenValidationResult? cachedResult))
            {
                CachedTokenValidationResult result = cachedResult!;
                logger.LogDebug(
                    "Token validation cache hit for TokenId: {TokenId}",
                    result.ClientAuthorizations.TokenId
                );

                return (result.CreatePrincipal(), result.ClientAuthorizations);
            }

            TokenValidationParameters validationParameters = GetValidationParameters(
                validationFingerprint,
                oidcConfig,
                now
            );

            string token = authorizationHeader[tokenStartIndex..];

            ClaimsPrincipal principal = _tokenHandler.ValidateToken(
                token,
                validationParameters,
                out SecurityToken validatedToken
            );

            string fallbackTokenId = CreateFallbackTokenId(authorizationHeader, tokenStartIndex);
            ClientAuthorizations clientAuthorizations = ExtractClientAuthorizations(
                principal,
                fallbackTokenId
            );

            CacheSuccessfulValidation(cacheKey, principal, clientAuthorizations, validatedToken, now);

            logger.LogDebug(
                "Token validation successful for TokenId: {TokenId}",
                clientAuthorizations.TokenId
            );

            return (principal, clientAuthorizations);
        }
        catch (SecurityTokenExpiredException ex)
        {
            logger.LogWarning(ex, "Token validation failed: Token expired");
            return (null, null);
        }
        catch (SecurityTokenException ex)
        {
            logger.LogWarning(ex, "Token validation failed");
            return (null, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during token validation");
            return (null, null);
        }
    }

    private TokenValidationParameters GetValidationParameters(
        string validationFingerprint,
        OpenIdConnectConfiguration oidcConfig,
        DateTimeOffset now
    )
    {
        CachedValidationParameters cachedValidationParameters = _validationParametersCache.GetOrAdd(
            validationFingerprint,
            _ => new CachedValidationParameters(
                new TokenValidationParameters
                {
                    // SECURITY CRITICAL: All must be true
                    ValidateIssuer = true,
                    ValidIssuer = oidcConfig.Issuer,

                    ValidateAudience = true,
                    ValidAudience = _options.Audience,

                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = [.. oidcConfig.SigningKeys],

                    ValidateLifetime = true,
                    LifetimeValidator = ValidateLifetime,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,

                    ClockSkew = TimeSpan.FromSeconds(_options.ClockSkewSeconds),

                    NameClaimType = ClaimTypes.Name,
                    RoleClaimType = _options.RoleClaimType,
                },
                now
            )
        );

        PruneValidationParametersCacheIfNeeded();

        return cachedValidationParameters.Parameters;
    }

    private bool ValidateLifetime(
        DateTime? notBefore,
        DateTime? expires,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters
    )
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        TimeSpan clockSkew = TimeSpan.FromSeconds(_options.ClockSkewSeconds);

        if (notBefore is not null && notBefore.Value > now.Add(clockSkew))
        {
            return false;
        }

        return expires is not null && expires.Value >= now.Subtract(clockSkew);
    }

    /// <summary>
    /// Extracts client authorization information from validated JWT claims.
    /// </summary>
    /// <param name="principal">The validated ClaimsPrincipal containing JWT claims</param>
    /// <param name="fallbackTokenId">A non-secret hash-derived token identifier used when the token has no jti claim</param>
    /// <returns>ClientAuthorizations object containing tokenId, claimSetName, educationOrganizationIds, and namespacePrefixes</returns>
    private static ClientAuthorizations ExtractClientAuthorizations(
        ClaimsPrincipal principal,
        string fallbackTokenId
    )
    {
        string claimSetName = string.Empty;
        string tokenId = fallbackTokenId;
        string clientId = string.Empty;
        string? namespacePrefixClaim = null;
        string? educationOrganizationIdsClaim = null;
        string? dataStoreIdsClaim = null;
        bool foundClaimSetName = false;
        bool foundTokenId = false;
        bool foundClientId = false;
        bool foundNamespacePrefixes = false;
        bool foundEducationOrganizationIds = false;
        bool foundDataStoreIds = false;

        foreach (Claim claim in principal.Claims)
        {
            switch (claim.Type)
            {
                case "scope" when !foundClaimSetName:
                    claimSetName = claim.Value;
                    foundClaimSetName = true;
                    break;
                case "jti" when !foundTokenId:
                    tokenId = claim.Value;
                    foundTokenId = true;
                    break;
                case "client_id" when !foundClientId:
                    clientId = claim.Value;
                    foundClientId = true;
                    break;
                case "namespacePrefixes" when !foundNamespacePrefixes:
                    namespacePrefixClaim = claim.Value;
                    foundNamespacePrefixes = true;
                    break;
                case "educationOrganizationIds" when !foundEducationOrganizationIds:
                    educationOrganizationIdsClaim = claim.Value;
                    foundEducationOrganizationIds = true;
                    break;
                case "dataStoreIds" when !foundDataStoreIds:
                    dataStoreIdsClaim = claim.Value;
                    foundDataStoreIds = true;
                    break;
            }
        }

        return new ClientAuthorizations(
            TokenId: tokenId,
            ClientId: clientId,
            ClaimSetName: claimSetName,
            EducationOrganizationIds: ParseEducationOrganizationIds(educationOrganizationIdsClaim),
            NamespacePrefixes: ParseNamespacePrefixes(namespacePrefixClaim),
            DataStoreIds: ParseDataStoreIds(dataStoreIdsClaim)
        );
    }

    private bool TryGetCachedValidation(
        TokenCacheKey cacheKey,
        DateTimeOffset now,
        out CachedTokenValidationResult? cachedResult
    )
    {
        if (_validatedTokenCache.TryGetValue(cacheKey, out cachedResult))
        {
            if (cachedResult.ExpiresAt > now)
            {
                return true;
            }

            _validatedTokenCache.TryRemove(cacheKey, out _);
        }

        cachedResult = null;
        return false;
    }

    private void CacheSuccessfulValidation(
        TokenCacheKey cacheKey,
        ClaimsPrincipal principal,
        ClientAuthorizations clientAuthorizations,
        SecurityToken validatedToken,
        DateTimeOffset now
    )
    {
        int maxEntries = _options.ValidatedTokenCacheMaxEntries;
        if (maxEntries <= 0)
        {
            return;
        }

        DateTimeOffset expiresAt = CalculateCacheExpiration(validatedToken, now);
        if (expiresAt <= now)
        {
            return;
        }

        _validatedTokenCache[cacheKey] = CachedTokenValidationResult.Create(
            principal,
            clientAuthorizations,
            now,
            expiresAt
        );

        PruneCacheIfNeeded(now, maxEntries);
    }

    private DateTimeOffset CalculateCacheExpiration(SecurityToken validatedToken, DateTimeOffset now)
    {
        TimeSpan maxLifetime = TimeSpan.FromSeconds(
            Math.Max(0, _options.ValidatedTokenCacheEntryMaxLifetimeSeconds)
        );
        if (maxLifetime == TimeSpan.Zero)
        {
            return now;
        }

        DateTime validToUtc = DateTime.SpecifyKind(validatedToken.ValidTo, DateTimeKind.Utc);
        DateTimeOffset tokenExpiresAt = new(validToUtc);
        DateTimeOffset cacheExpiresAt = tokenExpiresAt.Subtract(
            TimeSpan.FromSeconds(_options.ClockSkewSeconds)
        );
        DateTimeOffset maxExpiresAt = now.Add(maxLifetime);

        return cacheExpiresAt < maxExpiresAt ? cacheExpiresAt : maxExpiresAt;
    }

    private void PruneCacheIfNeeded(DateTimeOffset now, int maxEntries)
    {
        if (
            _validatedTokenCache.Count <= maxEntries
            && now.Subtract(_lastCacheMaintenance) < CachePruneInterval
        )
        {
            return;
        }

        lock (_cacheMaintenanceLock)
        {
            if (
                _validatedTokenCache.Count <= maxEntries
                && now.Subtract(_lastCacheMaintenance) < CachePruneInterval
            )
            {
                return;
            }

            foreach (
                (TokenCacheKey cacheKey, CachedTokenValidationResult cachedResult) in _validatedTokenCache
            )
            {
                if (cachedResult.ExpiresAt <= now)
                {
                    _validatedTokenCache.TryRemove(cacheKey, out _);
                }
            }

            while (_validatedTokenCache.Count > maxEntries)
            {
                TokenCacheKey? oldestKey = null;
                DateTimeOffset oldestCreatedAt = DateTimeOffset.MaxValue;
                foreach (
                    (TokenCacheKey cacheKey, CachedTokenValidationResult cachedResult) in _validatedTokenCache
                )
                {
                    if (cachedResult.CreatedAt < oldestCreatedAt)
                    {
                        oldestCreatedAt = cachedResult.CreatedAt;
                        oldestKey = cacheKey;
                    }
                }

                if (oldestKey is not TokenCacheKey cacheKeyToRemove)
                {
                    break;
                }

                _validatedTokenCache.TryRemove(cacheKeyToRemove, out _);
            }

            _lastCacheMaintenance = now;
        }
    }

    private void PruneValidationParametersCacheIfNeeded()
    {
        if (_validationParametersCache.Count <= ValidationParametersCacheMaxEntries)
        {
            return;
        }

        lock (_cacheMaintenanceLock)
        {
            while (_validationParametersCache.Count > ValidationParametersCacheMaxEntries)
            {
                string? oldestKey = null;
                DateTimeOffset oldestCreatedAt = DateTimeOffset.MaxValue;
                foreach (
                    (
                        string cacheKey,
                        CachedValidationParameters cachedParameters
                    ) in _validationParametersCache
                )
                {
                    if (cachedParameters.CreatedAt < oldestCreatedAt)
                    {
                        oldestCreatedAt = cachedParameters.CreatedAt;
                        oldestKey = cacheKey;
                    }
                }

                if (oldestKey is not string cacheKeyToRemove)
                {
                    break;
                }

                _validationParametersCache.TryRemove(cacheKeyToRemove, out _);
            }
        }
    }

    private string CreateValidationFingerprint(OpenIdConnectConfiguration oidcConfig)
    {
        StringBuilder builder = new();
        builder
            .Append(oidcConfig.Issuer)
            .Append('\u001f')
            .Append(_options.Audience)
            .Append('\u001f')
            .Append(_options.RoleClaimType)
            .Append('\u001f')
            .Append(_options.ClockSkewSeconds.ToString(CultureInfo.InvariantCulture));

        if (oidcConfig.SigningKeys.Count == 1)
        {
            foreach (SecurityKey signingKey in oidcConfig.SigningKeys)
            {
                builder.Append('\u001e').Append(GetSigningKeyFingerprint(signingKey));
            }

            return builder.ToString();
        }

        string[] signingKeyFingerprints = new string[oidcConfig.SigningKeys.Count];
        int signingKeyIndex = 0;
        foreach (SecurityKey signingKey in oidcConfig.SigningKeys)
        {
            signingKeyFingerprints[signingKeyIndex++] = GetSigningKeyFingerprint(signingKey);
        }

        Array.Sort(signingKeyFingerprints, StringComparer.Ordinal);
        foreach (string signingKeyFingerprint in signingKeyFingerprints)
        {
            builder.Append('\u001e').Append(signingKeyFingerprint);
        }

        return builder.ToString();
    }

    private string GetSigningKeyFingerprint(SecurityKey signingKey)
    {
        string keyType = signingKey.GetType().FullName ?? signingKey.GetType().Name;
        string keyId = signingKey.KeyId ?? string.Empty;
        string keyMaterialFingerprint = _signingKeyMaterialFingerprints.GetValue(
            signingKey,
            CreateSigningKeyMaterialFingerprint
        );

        return $"{keyType}:{keyId}:{keyMaterialFingerprint}";
    }

    private static string CreateSigningKeyMaterialFingerprint(SecurityKey signingKey)
    {
        // OIDC refreshes can recreate equivalent SecurityKey objects. Cache a stable
        // JWK thumbprint per key object so hot token-cache checks do not repeatedly hash
        // key material, while still invalidating cached validations when the material changes.
        // Opaque custom keys fall back to object identity so an unknown key replacement
        // cannot reuse successful validations created with a previous key object.
        return signingKey.CanComputeJwkThumbprint()
            ? $"jwk:{Base64UrlEncoder.Encode(signingKey.ComputeJwkThumbprint())}"
            : string.Create(CultureInfo.InvariantCulture, $"object:{RuntimeHelpers.GetHashCode(signingKey)}");
    }

    private static TokenCacheKey CreateTokenCacheKey(
        string authorizationHeader,
        int tokenStartIndex,
        string validationFingerprint
    )
    {
        ReadOnlySpan<char> token = authorizationHeader.AsSpan(tokenStartIndex);
        Span<byte> hash = stackalloc byte[32];
        _ = SHA256.HashData(MemoryMarshal.AsBytes(token), hash);

        return new TokenCacheKey(
            BinaryPrimitives.ReadUInt64LittleEndian(hash[..8]),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[8..16]),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[16..24]),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[24..32]),
            validationFingerprint
        );
    }

    private static string CreateFallbackTokenId(string authorizationHeader, int tokenStartIndex)
    {
        ReadOnlySpan<char> token = authorizationHeader.AsSpan(tokenStartIndex);
        Span<byte> hash = stackalloc byte[32];
        _ = SHA256.HashData(MemoryMarshal.AsBytes(token), hash);
        return Convert.ToHexString(hash[..8]);
    }

    private static NamespacePrefix[] ParseNamespacePrefixes(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        ReadOnlySpan<char> remaining = value.AsSpan();
        NamespacePrefix[] result = new NamespacePrefix[CountDelimitedValues(remaining)];
        int index = 0;

        while (TryReadNextDelimitedValue(ref remaining, out ReadOnlySpan<char> segment))
        {
            if (!segment.IsEmpty)
            {
                result[index++] = new NamespacePrefix(segment.ToString());
            }
        }

        return result;
    }

    private static EducationOrganizationId[] ParseEducationOrganizationIds(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        ReadOnlySpan<char> remaining = value.AsSpan();
        EducationOrganizationId[] result = new EducationOrganizationId[CountDelimitedValues(remaining)];
        int index = 0;

        while (TryReadNextDelimitedValue(ref remaining, out ReadOnlySpan<char> segment))
        {
            if (!segment.IsEmpty)
            {
                result[index++] = new EducationOrganizationId(
                    long.Parse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture)
                );
            }
        }

        return result;
    }

    private static DataStoreId[] ParseDataStoreIds(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        ReadOnlySpan<char> remaining = value.AsSpan();
        DataStoreId[] result = new DataStoreId[CountDelimitedValues(remaining)];
        int index = 0;

        while (TryReadNextDelimitedValue(ref remaining, out ReadOnlySpan<char> segment))
        {
            if (!segment.IsEmpty)
            {
                result[index++] = new DataStoreId(
                    long.Parse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture)
                );
            }
        }

        return result;
    }

    private static int CountDelimitedValues(ReadOnlySpan<char> value)
    {
        int count = 0;
        ReadOnlySpan<char> remaining = value;

        while (TryReadNextDelimitedValue(ref remaining, out ReadOnlySpan<char> segment))
        {
            if (!segment.IsEmpty)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryReadNextDelimitedValue(
        ref ReadOnlySpan<char> remaining,
        out ReadOnlySpan<char> segment
    )
    {
        if (remaining.IsEmpty)
        {
            segment = default;
            return false;
        }

        int commaIndex = remaining.IndexOf(',');
        if (commaIndex < 0)
        {
            segment = remaining;
            remaining = [];
            return true;
        }

        segment = remaining[..commaIndex];
        remaining = remaining[(commaIndex + 1)..];
        return true;
    }

    private readonly record struct TokenCacheKey(
        ulong Hash0,
        ulong Hash1,
        ulong Hash2,
        ulong Hash3,
        string ValidationFingerprint
    );

    private sealed record CachedValidationParameters(
        TokenValidationParameters Parameters,
        DateTimeOffset CreatedAt
    );

    private sealed record CachedTokenValidationResult(
        CachedPrincipal Principal,
        ClientAuthorizations ClientAuthorizations,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt
    )
    {
        public static CachedTokenValidationResult Create(
            ClaimsPrincipal principal,
            ClientAuthorizations clientAuthorizations,
            DateTimeOffset createdAt,
            DateTimeOffset expiresAt
        )
        {
            return new CachedTokenValidationResult(
                CachedPrincipal.Create(principal),
                clientAuthorizations,
                createdAt,
                expiresAt
            );
        }

        public ClaimsPrincipal CreatePrincipal() => Principal.CreatePrincipal();
    }

    private sealed record CachedPrincipal(CachedIdentity[] Identities)
    {
        public static CachedPrincipal Create(ClaimsPrincipal principal)
        {
            return new CachedPrincipal([.. principal.Identities.Select(CachedIdentity.Create)]);
        }

        public ClaimsPrincipal CreatePrincipal()
        {
            ClaimsIdentity[] identities = new ClaimsIdentity[Identities.Length];
            for (int i = 0; i < Identities.Length; i++)
            {
                identities[i] = Identities[i].CreateIdentity();
            }

            return new ClaimsPrincipal(identities);
        }
    }

    private sealed record CachedIdentity(
        string? AuthenticationType,
        string NameClaimType,
        string RoleClaimType,
        CachedClaim[] Claims
    )
    {
        public static CachedIdentity Create(ClaimsIdentity identity)
        {
            return new CachedIdentity(
                identity.AuthenticationType,
                identity.NameClaimType,
                identity.RoleClaimType,
                [.. identity.Claims.Select(CachedClaim.Create)]
            );
        }

        public ClaimsIdentity CreateIdentity()
        {
            Claim[] claims = new Claim[Claims.Length];
            for (int i = 0; i < Claims.Length; i++)
            {
                CachedClaim cachedClaim = Claims[i];
                claims[i] = new Claim(
                    cachedClaim.Type,
                    cachedClaim.Value,
                    cachedClaim.ValueType,
                    cachedClaim.Issuer,
                    cachedClaim.OriginalIssuer
                );
            }

            return new ClaimsIdentity(claims, AuthenticationType, NameClaimType, RoleClaimType);
        }
    }

    private readonly record struct CachedClaim(
        string Type,
        string Value,
        string ValueType,
        string Issuer,
        string OriginalIssuer
    )
    {
        public static CachedClaim Create(Claim claim)
        {
            return new CachedClaim(
                claim.Type,
                claim.Value,
                claim.ValueType,
                claim.Issuer,
                claim.OriginalIssuer
            );
        }
    }
}
