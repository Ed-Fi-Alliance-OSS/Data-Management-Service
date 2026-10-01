// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Tests.E2E.Authorization;

public static class SystemAdministrator
{
    public const string DefaultClientSecret = "ValidSystemAdministratorSecret123456!Abcd";
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(2);
    private static string _token = string.Empty;
    private static DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private static string _clientId = string.Empty;
    private static string _clientSecret = string.Empty;
    private static readonly SemaphoreSlim _registrationLock = new(1, 1);

    public static string Token
    {
        get => _token;
        private set => _token = value;
    }

    private static readonly HttpClient _client = new()
    {
        BaseAddress = new Uri($"http://localhost:{AppSettings.ConfigServicePort}/"),
    };

    public static async Task Register(string clientId, string clientSecret)
    {
        // Prevent concurrent registration attempts
        await _registrationLock.WaitAsync();
        try
        {
            bool clientChanged = clientId != _clientId || clientSecret != _clientSecret;
            _clientId = clientId;
            _clientSecret = clientSecret;

            // If we already have a valid token for this client, reuse it.
            if (!clientChanged && HasUsableToken())
            {
                return;
            }

            var registration = await RegisterAsync(_client, clientId, clientSecret, CancellationToken.None);
            Token = registration.Token;
            _tokenExpiresAt = registration.ExpiresAt;
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    // Endpoint-driven registration for attached fixtures. Does not alter the legacy static session.
    internal static async Task<(string Token, DateTimeOffset ExpiresAt)> RegisterAsync(
        HttpClient client,
        string clientId,
        string clientSecret,
        CancellationToken token
    )
    {
        using var formContent = new FormUrlEncodedContent([
            new("ClientId", clientId),
            new("ClientSecret", clientSecret),
            new("DisplayName", clientId),
        ]);
        using HttpResponseMessage registration = await client.PostAsync(
            "connect/register",
            formContent,
            token
        );
        // An existing client is allowed; token acquisition establishes success.
        using var tokenRequest = new FormUrlEncodedContent([
            new("client_id", clientId),
            new("client_secret", clientSecret),
            new("grant_type", "client_credentials"),
            new("scope", "edfi_admin_api/full_access"),
        ]);
        using HttpResponseMessage response = await client.PostAsync("connect/token", tokenRequest, token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"CMS token acquisition failed: HTTP {(int)response.StatusCode}."
            );
        }
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        string accessToken =
            document.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("CMS token response is missing a token.");
        int expiresIn = document.RootElement.TryGetProperty("expires_in", out JsonElement expiry)
            ? expiry.GetInt32()
            : 1800;
        return (accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    public static async Task<string> GetToken()
    {
        await _registrationLock.WaitAsync();
        try
        {
            if (HasUsableToken())
            {
                return Token;
            }

            if (string.IsNullOrEmpty(_clientId) || string.IsNullOrEmpty(_clientSecret))
            {
                throw new InvalidOperationException(
                    "SystemAdministrator must be registered before requesting a token."
                );
            }

            Token = string.Empty;
        }
        finally
        {
            _registrationLock.Release();
        }

        await Register(_clientId, _clientSecret);
        return Token;
    }

    private static bool HasUsableToken() =>
        !string.IsNullOrEmpty(Token) && DateTimeOffset.UtcNow.Add(TokenRefreshBuffer) < _tokenExpiresAt;
}
