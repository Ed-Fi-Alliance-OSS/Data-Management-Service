// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.IdentityModel.Protocols;

namespace EdFi.DataManagementService.Core.Security;

/// <summary>
/// Document retriever implementation for fetching OIDC metadata
/// </summary>
internal class HttpDocumentRetriever : IDocumentRetriever
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Whether to require HTTPS for metadata endpoints
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    public HttpDocumentRetriever()
    {
        _httpClient = new HttpClient();
    }

    public HttpDocumentRetriever(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        if (RequireHttps && !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"HTTPS is required but the address is not HTTPS: {address}");
        }

        var response = await _httpClient.GetAsync(address, cancel);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
