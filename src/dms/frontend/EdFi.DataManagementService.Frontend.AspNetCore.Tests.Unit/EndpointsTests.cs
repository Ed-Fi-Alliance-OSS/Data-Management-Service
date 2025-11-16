// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[NonParallelizable]
public class EndpointsTests
{
    [Test]
    public async Task TestHealthEndpoint()
    {
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets()).Returns([]);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTransient((_) => claimSetProvider);
            });
        });

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("\"Description\": \"Application is up and running\"");
    }

    [Test]
    public async Task BatchEndpoint_Forwards_Request_To_ApiService()
    {
        var claimSetProvider = A.Fake<IClaimSetProvider>();
        A.CallTo(() => claimSetProvider.GetAllClaimSets()).Returns([]);

        var apiService = A.Fake<IApiService>();
        var frontendResponse = A.Fake<IFrontendResponse>();
        A.CallTo(() => frontendResponse.StatusCode).Returns(200);
        A.CallTo(() => frontendResponse.Body).Returns(null);
        A.CallTo(() => frontendResponse.Headers).Returns(new Dictionary<string, string>());
        A.CallTo(() => apiService.ExecuteBatchAsync(A<FrontendRequest>._)).Returns(frontendResponse);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTransient((_) => claimSetProvider);
                collection.AddTransient((_) => apiService);
            });
        });

        using var client = factory.CreateClient();
        var response = await client.PostAsync(
            "/batch",
            new StringContent("[]", Encoding.UTF8, "application/json")
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        A.CallTo(() => apiService.ExecuteBatchAsync(A<FrontendRequest>._)).MustHaveHappened();
    }
}
