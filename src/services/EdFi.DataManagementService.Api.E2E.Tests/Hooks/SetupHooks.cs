// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Reqnroll;

namespace EdFi.DataManagementService.Api.E2E.Tests.Hooks
{
    [Binding]
    public class SetupHooks
    {

        [BeforeTestRun]
        public static async Task BeforeTestRun(PlaywrightContext context, IReqnrollOutputHelper _outputHelper)
        {
            try
            {
                string imageName = "edfi-data-management-service";
                // Image needs to be previously built
                // docker build -t {image name} .
                var dockerImage = new ContainerBuilder()
                    .WithImage(imageName)
                    .WithPortBinding(8080)
                    .Build();

                await dockerImage.StartAsync();

                context.API_URL = (new UriBuilder(Uri.UriSchemeHttp, dockerImage.Hostname, dockerImage.GetMappedPublicPort(8080), "uuid").Uri).ToString();
                await context.CreateApiContext();
            }
            catch (Exception exception)
            {
                Assert.Fail($"Unable to configure environment\nError starting API: {exception}");
            }
        }

        [AfterTestRun]
        public static void AfterTestRun(PlaywrightContext context)
        {
            context.Dispose();
        }
    }
}
