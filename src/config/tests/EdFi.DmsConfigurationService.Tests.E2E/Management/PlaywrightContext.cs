// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Playwright;

namespace EdFi.DmsConfigurationService.Tests.E2E.Management;

public class PlaywrightContext
{
    private Task<IAPIRequestContext>? _requestContext;

    /// <summary>
    /// The Configuration Service the suite calls: CMS_E2E_API_URL when an isolated deployment sets it,
    /// and the stock stack's address otherwise.
    /// </summary>
    public string ApiUrl { get; set; } =
        Environment.GetEnvironmentVariable("CMS_E2E_API_URL") is { Length: > 0 } apiUrl
            ? apiUrl
            : "http://localhost:8081";

    public IAPIRequestContext? ApiRequestContext => _requestContext?.GetAwaiter().GetResult();

    public void Dispose()
    {
        _requestContext?.Dispose();
    }

    public async Task InitializeApiContext()
    {
        var playwright = await Playwright.CreateAsync();

        _requestContext = playwright.APIRequest.NewContextAsync(
            new APIRequestNewContextOptions { BaseURL = ApiUrl, IgnoreHTTPSErrors = true }
        );
    }
}
