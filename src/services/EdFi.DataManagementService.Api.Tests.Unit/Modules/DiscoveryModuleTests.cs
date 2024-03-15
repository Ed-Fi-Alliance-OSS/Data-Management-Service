// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Api.Content;
using EdFi.DataManagementService.Api.Modules;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Unit.Modules;

[TestFixture]
public class DiscoveryModuleTests
{
    [Test]
    public async Task Discovery_Endpoint_Returns_Ok_Response()
    {
        // Arrange
        var versionProvider = A.Fake<IVersionProvider>();
        A.CallTo(() => versionProvider.Version).Returns("1.0");
        A.CallTo(() => versionProvider.Suite).Returns("DMS");

        var expectedDataModel = new DataModel("Ed-Fi", "5.0.0", "Ed-Fi data standard 5.0.0");
        var dataModelProvider = A.Fake<IDataModelProvider>();
        A.CallTo(() => dataModelProvider.GetDataModels()).Returns(new[] { expectedDataModel });

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => versionProvider);
                    collection.AddTransient((x) => dataModelProvider);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();
        var apiDetails = JsonSerializer.Deserialize<DiscoveryApiDetails>(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        apiDetails.Should().NotBeNull();
        apiDetails?.urls.Count.Should().Be(5);
        apiDetails?.suite.Should().Be("DMS");
        apiDetails?.dataModels.Should().NotBeNull();
        apiDetails?.dataModels.Count().Should().Be(1);
        apiDetails?.dataModels.First().name.Should().Be("Ed-Fi");
    }
}
