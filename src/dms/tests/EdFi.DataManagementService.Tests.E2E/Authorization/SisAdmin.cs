// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EdFi.DataManagementService.Tests.E2E.Authorization;

public static class SisAdmin
{
    public static ClientCredentials? ClientCredentials { get; set; }

    private static readonly HttpClient _client = new()
    {
        BaseAddress = new Uri("http://localhost:8081/"),
    };

    public static async Task Create(string company, string contactName, string contactEmailAddress, string namespacePrefixes, string systemAdministratorToken)
    {
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", systemAdministratorToken);

        using StringContent vendorContent = new(
            JsonSerializer.Serialize(new
            {
                company,
                contactName,
                contactEmailAddress,
                namespacePrefixes
            }),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage vendorPostResponse = await _client.PostAsync("v2/vendors", vendorContent);

        var vendorLocation = vendorPostResponse.Headers.Location?.AbsoluteUri;

        if (vendorLocation != null)
        {
            using HttpResponseMessage vendorGetResponse = await _client.GetAsync(vendorLocation);
            string vendorBody = await vendorGetResponse.Content.ReadAsStringAsync();

            int vendorId = JsonDocument.Parse(vendorBody).RootElement.GetProperty("id").GetInt32();

            using StringContent applicationContent = new(
                JsonSerializer.Serialize(new
                {
                    vendorId,
                    applicationName = "E2E",
                    claimSetName = "SIS-Vendor"
                }),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage applicationPostResponse =
                await _client.PostAsync("v2/applications", applicationContent);

            string applicationBody = await applicationPostResponse.Content.ReadAsStringAsync();
            var applicationJson = JsonDocument.Parse(applicationBody);

            var credentials = new ClientCredentials(
                applicationJson.RootElement.GetProperty("key").GetString() ?? "",
                applicationJson.RootElement.GetProperty("secret").GetString() ?? "");

            ClientCredentials = credentials;
        }
    }
}
