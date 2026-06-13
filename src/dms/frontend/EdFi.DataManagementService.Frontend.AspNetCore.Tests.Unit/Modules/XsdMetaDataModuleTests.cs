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
using EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Content;
using FakeItEasy;
using FluentAssertions;
using ImpromptuInterface;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
[NonParallelizable]
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
                Description = "Ed-Fi data standard 5.0.0",
                IsCoreProject = true,
            }
        ).ActLike<IDataModelInfo>();
        IDataModelInfo expectedtpdmModel = (
            new
            {
                ProjectName = "Tpdm",
                ProjectVersion = "1.0.0",
                Description = "TPDM data standard 1.0.0",
                IsCoreProject = false,
            }
        ).ActLike<IDataModelInfo>();

        _apiService = A.Fake<IApiService>();
        A.CallTo(() => _apiService.GetDataModelInfo())
            .Returns(new[] { expectededfiModel, expectedtpdmModel });

        var files = new List<string> { "file1.xsd", "file2.xsd", "file3.xsd" };

        _contentProvider = A.Fake<IContentProvider>();
        A.CallTo(() => _contentProvider.Files(A<string>.Ignored, ".xsd", "ed-fi")).Returns(files);
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
                    TestMockHelper.AddEssentialMocks(collection);
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
                    TestMockHelper.AddEssentialMocks(collection);
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
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
                }
            );
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
                    TestMockHelper.AddEssentialMocks(collection);
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
        Lazy<Stream> _fileStream = new(() =>
        {
            var content = "test-content";
            MemoryStream ms = new(Encoding.UTF8.GetBytes(content.ToString()));
            return ms;
        });

        A.CallTo(() => _contentProvider!.LoadXsdContent(A<string>.Ignored, "ed-fi")).Returns(_fileStream);

        var files = new List<string> { "text.xsd" };
        A.CallTo(() => _contentProvider!.Files(A<string>.Ignored, ".xsd", "ed-fi")).Returns(files);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
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
        A.CallTo(() => _contentProvider!.Files(A<string>.Ignored, ".xsd", "ed-fi")).Returns(files);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(
                (collection) =>
                {
                    TestMockHelper.AddEssentialMocks(collection);
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

/// <summary>
/// File-mode XSD metadata module tests. Uses a real ContentProvider wired to a faked
/// IApiSchemaAssetManifestProvider backed by a temp-dir staged workspace. The faked manifest
/// provider is pre-configured to serve the workspace content. IApiService is faked with
/// ProjectName values matching the manifest projectName values so section routing works
/// end-to-end. The approach keeps AppSettings untouched (no DI override) so the
/// AppSettingsValidator is not disturbed.
/// </summary>
[TestFixture]
[NonParallelizable]
public class Given_file_mode_xsd_metadata_endpoint
{
    private string _workspaceRoot = string.Empty;
    private IApiService _apiService = null!;
    private IContentProvider _fileModeContentProvider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        FileModeWorkspaceBuilder.BuildWorkspace(_workspaceRoot);

        // Build a real ContentProvider backed by a real ApiSchemaAssetManifestProvider
        // using Options.Create so no app-startup DI is involved.
        (_fileModeContentProvider, _) = FileModeWorkspaceBuilder.BuildProvider(_workspaceRoot);

        // Fake IApiService with ProjectName values matching the manifest projectName values.
        // Section route values are ProjectName.ToLower() per XsdMetadataEndpointModule.
        IDataModelInfo edFiModel = (
            new
            {
                ProjectName = FileModeWorkspaceBuilder.CoreProjectName, // "Ed-Fi"
                ProjectVersion = "5.0.0",
                Description = "Ed-Fi data standard",
                IsCoreProject = true,
            }
        ).ActLike<IDataModelInfo>();

        IDataModelInfo sampleModel = (
            new
            {
                ProjectName = FileModeWorkspaceBuilder.ExtensionProjectName, // "Sample"
                ProjectVersion = "1.0.0",
                Description = "Sample extension",
                IsCoreProject = false,
            }
        ).ActLike<IDataModelInfo>();

        _apiService = A.Fake<IApiService>();
        A.CallTo(() => _apiService.GetDataModelInfo()).Returns(new[] { edFiModel, sampleModel });
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        var fileModeContentProvider = _fileModeContentProvider;
        var apiService = _apiService;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                TestMockHelper.AddEssentialMocks(collection);
                collection.AddTransient(x => apiService);
                // Inject the pre-built file-mode ContentProvider directly; no AppSettings change.
                collection.AddTransient(x => fileModeContentProvider);
            });
        });
    }

    [Test]
    public async Task It_returns_sections_including_core_and_extension()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metadata/xsd");
        var content = await response.Content.ReadAsStringAsync();
        var jsonContent = JsonNode.Parse(content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonContent.Should().NotBeNull();
        var names = jsonContent!.AsArray().Select(n => n!["name"]!.GetValue<string>()).ToList();
        names.Should().Contain("ed-fi");
        names.Should().Contain("sample");
    }

    [Test]
    public async Task It_returns_bare_staged_file_names_with_full_urls_for_core_section()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metadata/xsd/ed-fi/files");
        var content = await response.Content.ReadAsStringAsync();
        var files = JsonSerializer.Deserialize<List<string>>(content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        files.Should().NotBeNull();
        files!
            .Should()
            .Equal(
                $"http://localhost/metadata/xsd/ed-fi/{FileModeWorkspaceBuilder.CoreXsdFile1}",
                $"http://localhost/metadata/xsd/ed-fi/{FileModeWorkspaceBuilder.CoreXsdFile2}"
            );
    }

    [Test]
    public async Task It_returns_blended_core_and_extension_files_for_extension_section()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metadata/xsd/sample/files");
        var content = await response.Content.ReadAsStringAsync();
        var files = JsonSerializer.Deserialize<List<string>>(content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        files.Should().NotBeNull();
        files!
            .Should()
            .Equal(
                $"http://localhost/metadata/xsd/sample/{FileModeWorkspaceBuilder.CoreXsdFile1}",
                $"http://localhost/metadata/xsd/sample/{FileModeWorkspaceBuilder.CoreXsdFile2}",
                $"http://localhost/metadata/xsd/sample/{FileModeWorkspaceBuilder.ExtensionXsdFile}"
            );
    }

    [Test]
    public async Task It_returns_application_xml_stream_for_bare_xsd_file_name()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Route: /metadata/xsd/{section}/{fileName}.xsd — bare staged name without extension in route
        var bareNameWithoutExtension = Path.GetFileNameWithoutExtension(
            FileModeWorkspaceBuilder.CoreXsdFile1
        );
        var response = await client.GetAsync($"/metadata/xsd/ed-fi/{bareNameWithoutExtension}.xsd");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        content.Should().Be(FileModeWorkspaceBuilder.CoreXsdFile1Content);
    }

    [Test]
    public async Task It_returns_404_when_existing_core_file_is_requested_from_unknown_section()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var bareNameWithoutExtension = Path.GetFileNameWithoutExtension(
            FileModeWorkspaceBuilder.CoreXsdFile1
        );
        var response = await client.GetAsync($"/metadata/xsd/unknown/{bareNameWithoutExtension}.xsd");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task It_returns_requested_extension_content_for_duplicate_extension_file_name()
    {
        FileModeWorkspaceBuilder.AddXsdFile(
            _workspaceRoot,
            FileModeWorkspaceBuilder.ExtensionProjectName,
            FileModeWorkspaceBuilder.DuplicateExtensionXsdFile,
            FileModeWorkspaceBuilder.SampleDuplicateExtensionXsdContent
        );
        FileModeWorkspaceBuilder.AddExtensionProjectWithXsd(
            _workspaceRoot,
            "Second",
            FileModeWorkspaceBuilder.DuplicateExtensionXsdFile,
            FileModeWorkspaceBuilder.SecondDuplicateExtensionXsdContent
        );

        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var bareNameWithoutExtension = Path.GetFileNameWithoutExtension(
            FileModeWorkspaceBuilder.DuplicateExtensionXsdFile
        );
        var sampleResponse = await client.GetAsync($"/metadata/xsd/sample/{bareNameWithoutExtension}.xsd");
        var secondResponse = await client.GetAsync($"/metadata/xsd/second/{bareNameWithoutExtension}.xsd");
        var sampleContent = await sampleResponse.Content.ReadAsStringAsync();
        var secondContent = await secondResponse.Content.ReadAsStringAsync();

        sampleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        sampleContent.Should().Be(FileModeWorkspaceBuilder.SampleDuplicateExtensionXsdContent);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondContent.Should().Be(FileModeWorkspaceBuilder.SecondDuplicateExtensionXsdContent);
    }

    [Test]
    public async Task It_returns_404_for_unknown_xsd_file()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/metadata/xsd/ed-fi/DoesNotExist.xsd");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
