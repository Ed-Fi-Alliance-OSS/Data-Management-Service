// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Playwright;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public class PlaywrightContext
{
    private Task<IAPIRequestContext>? _requestContext;

    public string ApiUrl { get; set; } = "http://localhost:8987";

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
