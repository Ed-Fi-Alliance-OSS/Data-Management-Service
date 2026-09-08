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
    /// Base URL of the Configuration Service under test. Defaults to the standard local stack and
    /// is overridable so a suite can run against an isolated stack published on another port.
    /// </summary>
    public string ApiUrl { get; set; } =
        Environment.GetEnvironmentVariable("DMS_CONFIG_E2E_API_URL") is { Length: > 0 } apiUrl
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
