// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Api.Core.ApiSchema;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using NUnit.Framework;

namespace EdFi.DataManagementService.Api.Tests.Integration;

[TestFixture]
public class APISchemaFileTests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public async Task WrongJSONObject()
    {
        await using var factory = new CustomWebApplicationFactory(services =>
        {
            //The goal here is to replace the ApiSchemaFileLoader Singleton from
            //the available service, but this is not working and it's not mocking the expected class.
            services.Replace(ServiceDescriptor.Scoped(_ =>
            {
                var jsonObject = new JsonObject
                {
                    ["name1"] = "value1",
                    ["name2"] = 2
                };

                var mock = new Mock<ApiSchemaFileLoader>();
                mock.Setup(m => m.ApiSchemaRootNode).Returns(jsonObject);

                return mock.Object;
            }));
        });
        var client = factory.CreateClient();
        var response = await client.GetAsync("/ping");

        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);


    }
}
