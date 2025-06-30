// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;

namespace EdFi.DataManagementService.Tests.E2E.Authorization;

public static class SystemAdministrator
{
    public static string Token = string.Empty;

    private static readonly HttpClient _client = new()
    {
        BaseAddress = new Uri($"http://localhost:{AppSettings.ConfigServicePort}/"),
    };

    public static async Task Register(string clientId, string clientSecret)
    {
        var formContent = new FormUrlEncodedContent(
            new[]
            {
                new KeyValuePair<string, string>("ClientId", clientId),
                new KeyValuePair<string, string>("ClientSecret", clientSecret),
                new KeyValuePair<string, string>("DisplayName", clientId),
            }
        );

        var registerResult = await _client.PostAsync("connect/register", formContent);
        if (registerResult.IsSuccessStatusCode)
        {
            var tokenRequestFormContent = new FormUrlEncodedContent(
                new[]
                {
                    new KeyValuePair<string, string>("client_id", clientId),
                    new KeyValuePair<string, string>("client_secret", clientSecret),
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("scope", "edfi_admin_api/full_access"),
                }
            );

            var tokenResult = await _client.PostAsync("connect/token", tokenRequestFormContent);

            if (tokenResult.IsSuccessStatusCode)
            {
                var body = await tokenResult.Content.ReadAsStringAsync();
                var document = JsonDocument.Parse(body);
                Token = document.RootElement.GetProperty("access_token").GetString() ?? "";
            }
        }
    }
}
