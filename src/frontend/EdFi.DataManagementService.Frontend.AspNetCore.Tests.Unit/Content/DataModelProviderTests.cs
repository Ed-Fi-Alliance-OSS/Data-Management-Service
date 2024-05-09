// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Tests.Shared;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Content;

public class DataModelProviderTests
{
    private IApiSchemaProvider? _apiSchemaProvider;
    private string _projectName = "ed-fi-test";
    private ILogger<DataModelProvider>? _logger;

    [SetUp]
    public void Setup()
    {
        var schemaBuilder = new ApiSchemaBuilder()
            .WithStartProject(_projectName, "29.0.0")
            .WithStartResource("School")
            .WithEndResource()
            .WithEndProject();

        _apiSchemaProvider = A.Fake<IApiSchemaProvider>();

        A.CallTo(() => _apiSchemaProvider.ApiSchemaRootNode).Returns(schemaBuilder.RootNode);
        _logger = A.Fake<ILogger<DataModelProvider>>();
    }

    [Test]
    public void Returns_Expected_Domain_Model()
    {
        // Arrange
        var domainModelProvider = new DataModelProvider(_logger!, _apiSchemaProvider!);

        // Act
        var response = domainModelProvider.GetDataModels();
        var dataModel = response.First();

        // Assert
        response.Should().NotBeNull();
        response.Count().Should().Be(1);
        dataModel.version.Should().Be("29.0.0");
        dataModel.name.ToLower().Should().Be(_projectName);
    }

    [Test]
    public void Returns_Error_When_No_Data_Model_Found()
    {
        // Arrange
        var schemaBuilder = new ApiSchemaBuilder();
        schemaBuilder.RootNode["projectSchemas"] = null;
        A.CallTo(() => _apiSchemaProvider!.ApiSchemaRootNode).Returns(schemaBuilder.RootNode);

        var domainModelProvider = new DataModelProvider(_logger!, _apiSchemaProvider!);

        // Act
        Action action = () => domainModelProvider.GetDataModels();

        // Assert
        action.Should().Throw<Exception>().WithMessage("No data model details found");
    }
}
