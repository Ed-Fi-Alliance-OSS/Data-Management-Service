// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Playwright;

namespace EdFi.DmsConfigurationService.Tests.E2E.Management;

/// <summary>Requests shared by Reqnroll steps and isolated NUnit service scenarios.</summary>
internal static class CmsTestRequests
{
    internal static async Task<IAPIResponse> GetClientAccessTokenAsync(
        IAPIRequestContext context,
        string clientId,
        string clientSecret,
        string scope
    )
    {
        using var content = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "client_credentials",
                ["scope"] = scope,
            }
        );
        return await context.PostAsync(
            "/connect/token",
            new()
            {
                Headers = new Dictionary<string, string>
                {
                    ["Content-Type"] = "application/x-www-form-urlencoded",
                },
                Data = await content.ReadAsStringAsync(),
            }
        );
    }

    internal static Task<IAPIResponse> PostVerbatimBodyAsync(
        IAPIRequestContext context,
        string url,
        string token,
        string body
    ) => context.PostAsync(url, new() { Headers = Headers(token), Data = body });

    internal static Task<IAPIResponse> PostVerbatimBodyAsync(
        IAPIRequestContext context,
        string url,
        string token,
        byte[] body
    ) => context.PostAsync(url, new() { Headers = Headers(token), DataByte = body });

    private static Dictionary<string, string> Headers(string token) =>
        new() { ["Content-Type"] = "application/json", ["Authorization"] = $"Bearer {token}" };
}
