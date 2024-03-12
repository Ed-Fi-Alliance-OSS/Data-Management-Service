// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Unit.Modules;

[TestFixture]
public class XsdMetaDataModuleTests
{
    [Test]
    public async Task TestXsdMetaDataEndpoint()
    {
        // Arrange
        var expectededfiModel = new DataModel("Ed-Fi", "5.0.0", "Ed-Fi data standard 5.0.0");
        var expectedtpdmModel = new DataModel("Tpdm", "1.0.0", "Tpdm data standard 1.0.0");

        var domainModelProvider = A.Fake<IDomainModelProvider>();
        A.CallTo(() => domainModelProvider.GetDataModels())
            .Returns(new[] { expectededfiModel, expectedtpdmModel });

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => domainModelProvider);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/xsd");
        var content = await response.Content.ReadAsStringAsync();

        var jsonContent = JsonNode.Parse(content);
        var section1 = jsonContent?[0]?["name"]?.GetValue<string>();
        var section2 = jsonContent?[1]?["name"]?.GetValue<string>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        section1.Should().Contain("Ed-Fi");
        section2.Should().Contain("Tpdm");
    }
}
