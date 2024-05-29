// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using ImpromptuInterface;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class XsdMetaDataModuleTests
{
    private IApiService? _apiService;
    private IContentProvider? _contentProvider;

    [SetUp]
    public void Setup()
    {
        IDataModelInfo expectededfiModel = (
            new
            {
                ProjectName = "Ed-Fi",
                ProjectVersion = "5.0.0",
                Description = "Ed-Fi data standard 5.0.0"
            }
        ).ActLike<IDataModelInfo>();
        IDataModelInfo expectedtpdmModel = (
            new
            {
                ProjectName = "Tpdm",
                ProjectVersion = "1.0.0",
                Description = "TPDM data standard 1.0.0"
            }
        ).ActLike<IDataModelInfo>();

        _apiService = A.Fake<IApiService>();
        A.CallTo(() => _apiService.GetDataModelInfo())
            .Returns(new[] { expectededfiModel, expectedtpdmModel });

        var files = new List<string> { "file1.xsd", "file2.xsd", "file3.xsd" };

        _contentProvider = A.Fake<IContentProvider>();
        A.CallTo(() => _contentProvider.Files(A<string>.Ignored, ".xsd")).Returns(files);
    }

    [Test]
    public async Task XsdMetaData_Endpoint_Returns_DataModel_Sections()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => _apiService!);
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
        section1.Should().Contain("ed-fi");
        section2.Should().Contain("tpdm");
    }

    [Test]
    public async Task XsdMetaData_Returns_Files()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => _apiService!);
                    collection.AddTransient((x) => _contentProvider!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/xsd/ed-fi/files");
        var content = await response.Content.ReadAsStringAsync();

        var files = JsonSerializer.Deserialize<List<string>>(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        files.Should().NotBeNull();
        files?.Count().Should().Be(3);
    }

    [Test]
    public async Task XsdMetaData_Files_Returns_Invalid_Resource_If_Missing_Section()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/xsd/test/test1/files");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task XsdMetaData_Files_Returns_Invalid_Resource_If_Wrong_Section()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => _apiService!);
                    collection.AddTransient((x) => _contentProvider!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/xsd/wrong-section/files");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task XsdMetaData_Files_Returns_Xsd_File_Content()
    {
        // Arrange
        Lazy<Stream> _fileStream =
            new(() =>
            {
                var content = "test-content";
                MemoryStream ms = new(Encoding.UTF8.GetBytes(content.ToString()));
                return ms;
            });

        A.CallTo(() => _contentProvider!.LoadXsdContent(A<string>.Ignored)).Returns(_fileStream);

        var files = new List<string> { "text.xsd" };
        A.CallTo(() => _contentProvider!.Files(A<string>.Ignored, ".xsd")).Returns(files);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => _apiService!);
                    collection.AddTransient((x) => _contentProvider!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/xsd/ed-fi/test.xsd");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().Contain("test-content");
    }

    [Test]
    public async Task XsdMetaData_Files_Returns_Invalid_Resource_With_Wrong_File()
    {
        // Arrange
        var files = new List<string>();
        A.CallTo(() => _contentProvider!.Files(A<string>.Ignored, ".xsd")).Returns(files);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    collection.AddTransient((x) => _apiService!);
                    collection.AddTransient((x) => _contentProvider!);
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/metadata/xsd/ed-fi/not-exists.xsd");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
