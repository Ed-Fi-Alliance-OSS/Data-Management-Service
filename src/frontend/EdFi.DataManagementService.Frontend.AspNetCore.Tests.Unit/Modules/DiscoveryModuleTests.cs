// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using EdFi.DataManagementService.Core;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Frontend.AspNetCore.Modules;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class DiscoveryModuleTests
{
    [Test]
    public async Task Discovery_Endpoint_Returns_Ok_Response()
    {
        // Arrange
        var versionProvider = A.Fake<IVersionProvider>();
        A.CallTo(() => versionProvider.Version).Returns("1.0");
        A.CallTo(() => versionProvider.ApplicationName).Returns("DMS");

        var expectedDataModelInfo = new DataModelInfo("Ed-Fi", "5.0.0", "Ed-Fi data standard 5.0.0");
        var coreFacade = A.Fake<ICoreFacade>();
        A.CallTo(() => coreFacade.GetDataModelInfo()).Returns(new[] { expectedDataModelInfo });

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => versionProvider);
                    collection.AddTransient((x) => coreFacade);
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
        apiDetails?.applicationName.Should().Be("DMS");
        apiDetails?.dataModels.Should().NotBeNull();
        apiDetails?.dataModels.Count().Should().Be(1);
        apiDetails?.dataModels.First().name.Should().Be("Ed-Fi");
    }
}
