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

            var formContent = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("ClientId", clientId),
                new KeyValuePair<string, string>("ClientSecret", clientSecret),
                new KeyValuePair<string, string>("DisplayName", clientId),
            ]);

            await _client.PostAsync("connect/register", formContent);

            // Client may already exist, which is OK - try to get token anyway
            var tokenRequestFormContent = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
            ]);

            var tokenResult = await _client.PostAsync("connect/token", tokenRequestFormContent);

            if (!tokenResult.IsSuccessStatusCode)
            {
                var errorBody = await tokenResult.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Failed to obtain token for client '{clientId}'. Status: {tokenResult.StatusCode}, Error: {errorBody}"
                );
            }

            var body = await tokenResult.Content.ReadAsStringAsync();
            var document = JsonDocument.Parse(body);
            Token = document.RootElement.GetProperty("access_token").GetString() ?? "";
            int expiresInSeconds = document.RootElement.TryGetProperty(
                "expires_in",
                out JsonElement expiresIn
            )
                ? expiresIn.GetInt32()
                : 1800;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);
        }
        finally
        {
            _registrationLock.Release();
        }
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
