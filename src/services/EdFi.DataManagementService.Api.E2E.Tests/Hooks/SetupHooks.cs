// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Reqnroll;

namespace EdFi.DataManagementService.Api.E2E.Tests.Hooks
{
    [Binding]
    public class SetupHooks
    {
        public IAPIRequestContext? _requestContext;

        [BeforeTestRun]
        public async void BeforeTestRun()
        {

            var playwright = await Playwright.CreateAsync();

            _requestContext = await playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
            {
                BaseURL = "http://localhost:5198/",
                IgnoreHTTPSErrors = true
            });
        }
    }
}
