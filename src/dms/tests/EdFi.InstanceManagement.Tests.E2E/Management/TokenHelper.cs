// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text.Json;
using EdFi.InstanceManagement.Tests.E2E.Models;

namespace EdFi.InstanceManagement.Tests.E2E.Management;

/// <summary>
/// Helper class for authentication token management
/// </summary>
public static class TokenHelper
{
    private static readonly HttpClient HttpClient = new();
    private static readonly DmsTokenCache DmsTokens = new(AcquireDmsTokenAsync, TimeProvider.System);

    /// <summary>
    /// Get access token from Config Service using client credentials
    /// </summary>
    public static async Task<string> GetConfigServiceTokenAsync(
        string tokenUrl,
        string clientId,
        string clientSecret
    )
    {
        var requestContent = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                { "client_id", clientId },
                { "client_secret", clientSecret },
                { "grant_type", "client_credentials" },
                { "scope", "edfi_admin_api/full_access" },
            }
        );

        var response = await HttpClient.PostAsync(tokenUrl, requestContent);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(
            content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        return tokenResponse?.AccessToken
            ?? throw new InvalidOperationException("Failed to get access token");
    }

    /// <summary>
    /// Get access token from DMS using Basic authentication
    /// </summary>
    public static async Task<string> GetDmsTokenAsync(
        string tokenUrl,
        string clientKey,
        string clientSecret
    ) =>
        (await AcquireDmsTokenAsync(tokenUrl, clientKey, clientSecret)).AccessToken
        ?? throw new InvalidOperationException("Failed to get DMS access token");

    /// <summary>
    /// Reuse a DMS token until its conservative refresh deadline, sharing concurrent acquisitions.
    /// </summary>
    public static Task<string> GetReusableDmsTokenAsync(
        string tokenUrl,
        string clientKey,
        string clientSecret
    ) => DmsTokens.GetReusableDmsTokenAsync(tokenUrl, clientKey, clientSecret);

    private static async Task<TokenResponse> AcquireDmsTokenAsync(
        string tokenUrl,
        string clientKey,
        string clientSecret
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);

        // Basic authentication: base64(key:secret)
        var credentials = OAuthClientCredentialsEncoder.CreateBasicSchemeParameter(clientKey, clientSecret);
        request.Headers.Authorization = new AuthenticationHeaderValue($"Basic", credentials);

        var requestContent = new FormUrlEncodedContent(
            new Dictionary<string, string> { { "grant_type", "client_credentials" } }
        );

        request.Content = requestContent;

        using var response = await HttpClient.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(
            content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        return tokenResponse ?? throw new InvalidOperationException("Failed to get DMS access token");
    }
}

internal sealed class DmsTokenCache(
    Func<string, string, string, Task<TokenResponse>> acquireToken,
    TimeProvider timeProvider
)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string TokenUrl, string ClientKey), Lazy<Task<CachedToken>>> _tokens = [];

    public async Task<string> GetReusableDmsTokenAsync(string tokenUrl, string clientKey, string clientSecret)
    {
        var key = (tokenUrl, clientKey);
        Lazy<Task<CachedToken>> acquisition;
        lock (_gate)
        {
            if (
                _tokens.TryGetValue(key, out var existing)
                && (
                    !existing.IsValueCreated
                    || !existing.Value.IsCompletedSuccessfully
                    || timeProvider.GetUtcNow() < existing.Value.Result.RefreshAt
                )
            )
            {
                acquisition = existing;
            }
            else
            {
                acquisition = new(() => AcquireReusableTokenAsync(tokenUrl, clientKey, clientSecret));
                _tokens[key] = acquisition;
            }
        }

        try
        {
            return (await acquisition.Value).AccessToken;
        }
        catch
        {
            lock (_gate)
            {
                // A failing waiter must not evict a newer acquisition created by another caller.
                if (_tokens.TryGetValue(key, out var current) && ReferenceEquals(current, acquisition))
                {
                    _tokens.Remove(key);
                }
            }

            throw;
        }
    }

    private async Task<CachedToken> AcquireReusableTokenAsync(
        string tokenUrl,
        string clientKey,
        string clientSecret
    )
    {
        // Start the lifetime before the HTTP call so acquisition latency cannot extend token validity.
        var requestedAt = timeProvider.GetUtcNow();
        var token = await acquireToken(tokenUrl, clientKey, clientSecret);
        var lifetimeSeconds = Math.Max(0, token.ExpiresIn);
        // Reserve 30 seconds for normal tokens, and half the lifetime for short-lived tokens.
        var refreshSkewSeconds = Math.Min(30, lifetimeSeconds / 2.0);
        return new(
            token.AccessToken ?? throw new InvalidOperationException("Failed to get DMS access token"),
            requestedAt.AddSeconds(lifetimeSeconds - refreshSkewSeconds)
        );
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset RefreshAt);
}
