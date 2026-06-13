// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Content;

[TestFixture]
[Parallelizable]
public class ContentProviderTests
{
    private string _workspaceRoot = string.Empty;
    private ContentProvider _contentProvider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        FileModeWorkspaceBuilder.BuildWorkspace(_workspaceRoot);
        (_contentProvider, _) = FileModeWorkspaceBuilder.BuildProvider(_workspaceRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [Test]
    public void Returns_Expected_Json_Files()
    {
        // Act
        var response = _contentProvider.Files("ApiSchema", ".json", "ed-fi");

        // Assert
        response.Should().NotBeNull();
        response.Should().BeEmpty();
    }

    [Test]
    public void Returns_Expected_Xsd_Files()
    {
        // Act
        var response = _contentProvider.Files(@"EdFi\.DataStandard.*\.ApiSchema", ".xsd", "ed-fi");

        // Assert
        response.Should().NotBeNull();
        response.Count().Should().Be(2);
        response.Should().Contain(FileModeWorkspaceBuilder.CoreXsdFile1);
    }

    [Test]
    public void Returns_Expected_Json_File_Content()
    {
        // Arrange
        var expectedHost = "http://local:5000";
        var expectedOauthUrl = "http://local:5000/oauth/token";

        var content =
            """{"openapi":"3.0.1", "info":"descriptors","servers":[{"url":"http://local:5000/data/v3"}],"oauth":[{"url":"http://local:5000/oauth/token"}]}""";
        var mockJsonNode = JsonNode.Parse(content)!;

        var contentProvider = A.Fake<IContentProvider>();
        A.CallTo(() => contentProvider.LoadJsonContent(A<string>._, A<string>._, A<string>._))
            .Returns(mockJsonNode);
        // Act
        var response = contentProvider.LoadJsonContent(
            "EdFi.DataStandard52.ApiSchema.ApiSchema.json",
            expectedHost,
            expectedOauthUrl
        );
        var openApi = response?["openapi"]?.GetValue<string>();
        var serverUrl = response?["servers"]?.AsArray()?[0]?["url"]?.GetValue<string>();
        var oauthUrl = response?["oauth"]?.AsArray()?[0]?["url"]?.GetValue<string>();

        // Assert
        response.Should().NotBeNull();
        openApi.Should().Be("3.0.1");
        serverUrl.Should().Be($"{expectedHost}/data/v3");
        oauthUrl.Should().Be(expectedOauthUrl);
    }

    [Test]
    public void Returns_Error_With_Not_Existing_File()
    {
        // Act
        Action action = () => _contentProvider.LoadJsonContent("not-exists", string.Empty, string.Empty);

        // Assert
        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Unable to read and parse not-exists.json");
    }

    [Test]
    public void Returns_Expected_Xsd_File_Content()
    {
        // Arrange
        var content =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">\n  <xs:include schemaLocation=\"Ed-Fi-Core.xsd\" />\n";
        MemoryStream contentStream = new(Encoding.UTF8.GetBytes(content.ToString()));

        var contentProvider = A.Fake<IContentProvider>();
        A.CallTo(() => contentProvider.LoadXsdContent(A<string>._))
            .Returns(new Lazy<Stream>(() => contentStream));

        // Act
        var response = contentProvider.LoadXsdContent(
            "EdFi.DataStandard52.ApiSchema.xsd.Interchange-Contact.xsd"
        );
        var responseStream = response.Value;
        string line = string.Empty;
        using (var reader = new StreamReader(responseStream))
        {
            line = reader.ReadToEnd();
        }

        // Assert
        line.Should().NotBeNullOrWhiteSpace();
        line.Should().Be(content);
    }
}

/// <summary>
/// Helper that builds a real staged workspace for file-mode ContentProvider tests.
/// Uses real ApiSchemaAssetManifestProvider so
/// provider+reader integration is exercised end-to-end.
/// Manifest layout:
///   - "Ed-Fi"   core project: discoverySpecPath + xsdDirectory (two XSD files)
///   - "Sample"  extension project: xsdDirectory only (one XSD file, no discoverySpecPath)
///   - "Minimal" project: neither discoverySpecPath nor xsdDirectory (keys omitted)
/// </summary>
internal static class FileModeWorkspaceBuilder
{
    public const string CoreProjectName = "Ed-Fi";
    public const string ExtensionProjectName = "Sample";
    public const string MinimalProjectName = "Minimal";

    public const string CoreXsdFile1 = "Ed-Fi-Core.xsd";
    public const string CoreXsdFile2 = "Interchange-Student.xsd";
    public const string ExtensionXsdFile = "Sample-Extension.xsd";
    public const string DuplicateExtensionXsdFile = "Shared-Extension.xsd";

    public const string CoreXsdFile1Content = "<xs:schema id=\"core\"/>";
    public const string CoreXsdFile2Content = "<xs:schema id=\"interchange-student\"/>";
    public const string ExtensionXsdFileContent = "<xs:schema id=\"sample-extension\"/>";
    public const string SampleDuplicateExtensionXsdContent = "<xs:schema id=\"sample-shared\"/>";
    public const string SecondDuplicateExtensionXsdContent = "<xs:schema id=\"second-shared\"/>";

    public const string DiscoverySpecJson =
        """{"host":"HOST_URL/data/v3","token":"HOST_URL/oauth/token","version":"1.0"}""";

    public static string BuildWorkspace(string workspaceRoot)
    {
        Directory.CreateDirectory(workspaceRoot);

        // Manifest
        var manifestJson = """
            {
              "version": 1,
              "projects": [
                {
                  "projectName": "Ed-Fi",
                  "projectEndpointName": "ed-fi",
                  "isExtensionProject": false,
                  "schemaPath": "schemas/Ed-Fi/ApiSchema.json",
                  "discoverySpecPath": "content/Ed-Fi/discovery-spec.json",
                  "xsdDirectory": "content/Ed-Fi/xsd"
                },
                {
                  "projectName": "Sample",
                  "projectEndpointName": "sample",
                  "isExtensionProject": true,
                  "schemaPath": "schemas/Sample/ApiSchema.json",
                  "xsdDirectory": "content/Sample/xsd"
                },
                {
                  "projectName": "Minimal",
                  "projectEndpointName": "minimal",
                  "isExtensionProject": true,
                  "schemaPath": "schemas/Minimal/ApiSchema.json"
                }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(workspaceRoot, "bootstrap-api-schema-manifest.json"), manifestJson);

        // Schema stubs
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "schemas", "Ed-Fi"));
        File.WriteAllText(Path.Combine(workspaceRoot, "schemas", "Ed-Fi", "ApiSchema.json"), "{}");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "schemas", "Sample"));
        File.WriteAllText(Path.Combine(workspaceRoot, "schemas", "Sample", "ApiSchema.json"), "{}");
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "schemas", "Minimal"));
        File.WriteAllText(Path.Combine(workspaceRoot, "schemas", "Minimal", "ApiSchema.json"), "{}");

        // Discovery spec (with HOST_URL placeholders)
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "content", "Ed-Fi"));
        File.WriteAllText(
            Path.Combine(workspaceRoot, "content", "Ed-Fi", "discovery-spec.json"),
            DiscoverySpecJson
        );

        // Core XSD files
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "content", "Ed-Fi", "xsd"));
        File.WriteAllText(
            Path.Combine(workspaceRoot, "content", "Ed-Fi", "xsd", CoreXsdFile1),
            CoreXsdFile1Content
        );
        File.WriteAllText(
            Path.Combine(workspaceRoot, "content", "Ed-Fi", "xsd", CoreXsdFile2),
            CoreXsdFile2Content
        );

        // Extension XSD file
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "content", "Sample", "xsd"));
        File.WriteAllText(
            Path.Combine(workspaceRoot, "content", "Sample", "xsd", ExtensionXsdFile),
            ExtensionXsdFileContent
        );

        return workspaceRoot;
    }

    public static void AddExtensionProjectWithXsd(
        string workspaceRoot,
        string projectName,
        string fileName,
        string content
    )
    {
        var manifestPath = Path.Combine(workspaceRoot, "bootstrap-api-schema-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var projects = manifest["projects"]!.AsArray();
        projects.Add(
            new JsonObject
            {
                ["projectName"] = projectName,
                ["projectEndpointName"] = projectName.ToLowerInvariant(),
                ["isExtensionProject"] = true,
                ["schemaPath"] = $"schemas/{projectName}/ApiSchema.json",
                ["xsdDirectory"] = $"content/{projectName}/xsd",
            }
        );

        File.WriteAllText(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
        );

        Directory.CreateDirectory(Path.Combine(workspaceRoot, "schemas", projectName));
        File.WriteAllText(Path.Combine(workspaceRoot, "schemas", projectName, "ApiSchema.json"), "{}");
        AddXsdFile(workspaceRoot, projectName, fileName, content);
    }

    public static void AddXsdFile(string workspaceRoot, string projectName, string fileName, string content)
    {
        var xsdDirectory = Path.Combine(workspaceRoot, "content", projectName, "xsd");
        Directory.CreateDirectory(xsdDirectory);
        File.WriteAllText(Path.Combine(xsdDirectory, fileName), content);
    }

    public static (ContentProvider provider, IApiSchemaAssetManifestProvider manifestProvider) BuildProvider(
        string workspaceRoot
    )
    {
        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var manifestLogger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        var manifestProvider = new ApiSchemaAssetManifestProvider(appSettings, manifestLogger);

        var logger = A.Fake<ILogger<ContentProvider>>();
        var provider = new ContentProvider(logger, manifestProvider);

        return (provider, manifestProvider);
    }
}

[TestFixture]
public class Given_file_mode_discovery_spec_load
{
    private string _workspaceRoot = string.Empty;
    private ContentProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        FileModeWorkspaceBuilder.BuildWorkspace(_workspaceRoot);
        (_provider, _) = FileModeWorkspaceBuilder.BuildProvider(_workspaceRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [Test]
    public void It_loads_discovery_spec_and_replaces_host_url_placeholder()
    {
        var hostUrl = "http://testserver:8080";
        var oAuthUrl = "http://testserver:8080/oauth/token";

        var result = _provider.LoadJsonContent("discovery", hostUrl, oAuthUrl);

        result.Should().NotBeNull();
        // LoadJsonContent replaces "HOST_URL/data/v3" with "{hostUrl}/data"
        result["host"]!.GetValue<string>().Should().Be($"{hostUrl}/data");
    }

    [Test]
    public void It_loads_discovery_spec_and_replaces_oauth_url_placeholder()
    {
        var hostUrl = "http://testserver:8080";
        var oAuthUrl = "http://testserver:8080/oauth/token";

        var result = _provider.LoadJsonContent("discovery", hostUrl, oAuthUrl);

        result.Should().NotBeNull();
        result["token"]!.GetValue<string>().Should().Be(oAuthUrl);
    }

    [Test]
    public void It_throws_for_missing_discovery_spec_when_no_project_has_discoverySpecPath()
    {
        // Build a workspace where no project provides discoverySpecPath
        var workspaceNoSpec = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(workspaceNoSpec);

        var manifestJson = """
            {
              "version": 1,
              "projects": [
                {
                  "projectName": "Ed-Fi",
                  "projectEndpointName": "ed-fi",
                  "isExtensionProject": false,
                  "schemaPath": "schemas/Ed-Fi/ApiSchema.json"
                }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(workspaceNoSpec, "bootstrap-api-schema-manifest.json"), manifestJson);

        try
        {
            var appSettings = Options.Create(
                new AppSettings
                {
                    ApiSchemaPath = workspaceNoSpec,
                    UseApiSchemaPath = true,
                    AllowIdentityUpdateOverrides = "",
                }
            );
            var manifestLogger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
            var manifestProvider = new ApiSchemaAssetManifestProvider(appSettings, manifestLogger);
            var logger = A.Fake<ILogger<ContentProvider>>();
            var provider = new ContentProvider(logger, manifestProvider);

            Action action = () => provider.LoadJsonContent("discovery", string.Empty, string.Empty);

            action.Should().Throw<InvalidOperationException>().WithMessage("Couldn't load find the resource");
        }
        finally
        {
            Directory.Delete(workspaceNoSpec, recursive: true);
        }
    }

    [Test]
    public void It_throws_for_declared_missing_discovery_spec_instead_of_falling_back_to_later_project()
    {
        File.Delete(Path.Combine(_workspaceRoot, "content", "Ed-Fi", "discovery-spec.json"));

        var extensionDiscoverySpecPath = Path.Combine(
            _workspaceRoot,
            "content",
            "Sample",
            "discovery-spec.json"
        );
        File.WriteAllText(extensionDiscoverySpecPath, """{"extension":"available"}""");

        var manifestPath = Path.Combine(_workspaceRoot, "bootstrap-api-schema-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var sampleProject = manifest["projects"]!.AsArray()[1]!.AsObject();
        sampleProject["discoverySpecPath"] = "content/Sample/discovery-spec.json";
        File.WriteAllText(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
        );

        Action action = () => _provider.LoadJsonContent("discovery", string.Empty, string.Empty);

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*Ed-Fi*discoverySpecPath*content/Ed-Fi/discovery-spec.json*does not exist*");
    }
}

[TestFixture]
public class Given_file_mode_xsd_listing_for_core_section
{
    private string _workspaceRoot = string.Empty;
    private ContentProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        FileModeWorkspaceBuilder.BuildWorkspace(_workspaceRoot);
        (_provider, _) = FileModeWorkspaceBuilder.BuildProvider(_workspaceRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [Test]
    public void It_lists_only_core_xsd_files_for_the_core_section()
    {
        // XsdMetadataEndpointModule passes an assembly-name regex pattern for the full listing
        var files = _provider.Files(@"EdFi\.DataStandard.*\.ApiSchema", ".xsd", "ed-fi").ToList();

        files.Should().Equal(FileModeWorkspaceBuilder.CoreXsdFile1, FileModeWorkspaceBuilder.CoreXsdFile2);
    }

    [Test]
    public void It_does_not_include_extension_files_for_core_section()
    {
        var files = _provider.Files(@"EdFi\.DataStandard.*\.ApiSchema", ".xsd", "ed-fi").ToList();

        files.Should().NotContain(FileModeWorkspaceBuilder.ExtensionXsdFile);
    }

    [Test]
    public void It_filters_by_bare_file_name()
    {
        var files = _provider.Files(FileModeWorkspaceBuilder.CoreXsdFile1, ".xsd", "ed-fi").ToList();

        files.Should().ContainSingle();
        files[0].Should().Be(FileModeWorkspaceBuilder.CoreXsdFile1);
    }

    [Test]
    public void It_filters_by_legacy_assembly_resource_prefixed_file_name()
    {
        var legacyName = $"EdFi.DataStandard52.ApiSchema.xsd.{FileModeWorkspaceBuilder.CoreXsdFile1}";

        var files = _provider.Files(legacyName, ".xsd", "ed-fi").ToList();

        files.Should().ContainSingle();
        files[0].Should().Be(FileModeWorkspaceBuilder.CoreXsdFile1);
    }

    [Test]
    public void It_returns_no_core_files_for_an_unknown_section()
    {
        var files = _provider.Files(FileModeWorkspaceBuilder.CoreXsdFile1, ".xsd", "unknown").ToList();

        files.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_file_mode_xsd_listing_for_extension_section
{
    private string _workspaceRoot = string.Empty;
    private ContentProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        FileModeWorkspaceBuilder.BuildWorkspace(_workspaceRoot);
        (_provider, _) = FileModeWorkspaceBuilder.BuildProvider(_workspaceRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [Test]
    public void It_blends_core_and_extension_xsd_files_for_extension_section()
    {
        var files = _provider
            .Files(@"EdFi\.DataStandard.*\.ApiSchema|EdFi.sample.ApiSchema", ".xsd", "sample")
            .ToList();

        files
            .Should()
            .Equal(
                FileModeWorkspaceBuilder.CoreXsdFile1,
                FileModeWorkspaceBuilder.CoreXsdFile2,
                FileModeWorkspaceBuilder.ExtensionXsdFile
            );
    }

    [Test]
    public void It_filters_extension_section_by_bare_extension_file_name()
    {
        var files = _provider.Files(FileModeWorkspaceBuilder.ExtensionXsdFile, ".xsd", "sample").ToList();

        files.Should().ContainSingle();
        files[0].Should().Be(FileModeWorkspaceBuilder.ExtensionXsdFile);
    }

    [Test]
    public void It_filters_extension_section_by_legacy_prefixed_core_file_name()
    {
        var legacyName = $"EdFi.DataStandard52.ApiSchema.xsd.{FileModeWorkspaceBuilder.CoreXsdFile2}";

        var files = _provider.Files(legacyName, ".xsd", "sample").ToList();

        files.Should().ContainSingle();
        files[0].Should().Be(FileModeWorkspaceBuilder.CoreXsdFile2);
    }
}

[TestFixture]
public class Given_file_mode_xsd_stream_loading
{
    private string _workspaceRoot = string.Empty;
    private ContentProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        FileModeWorkspaceBuilder.BuildWorkspace(_workspaceRoot);
        (_provider, _) = FileModeWorkspaceBuilder.BuildProvider(_workspaceRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [Test]
    public void It_returns_stream_content_for_bare_file_name()
    {
        var lazy = _provider.LoadXsdContent(FileModeWorkspaceBuilder.CoreXsdFile1);
        using var reader = new StreamReader(lazy.Value);
        var content = reader.ReadToEnd();

        content.Should().Be(FileModeWorkspaceBuilder.CoreXsdFile1Content);
    }

    [Test]
    public void It_returns_stream_content_for_legacy_prefixed_file_name()
    {
        var legacyName = $"EdFi.DataStandard52.ApiSchema.xsd.{FileModeWorkspaceBuilder.CoreXsdFile2}";

        var lazy = _provider.LoadXsdContent(legacyName);
        using var reader = new StreamReader(lazy.Value);
        var content = reader.ReadToEnd();

        content.Should().Be(FileModeWorkspaceBuilder.CoreXsdFile2Content);
    }

    [Test]
    public void It_returns_extension_file_stream_content()
    {
        var lazy = _provider.LoadXsdContent(FileModeWorkspaceBuilder.ExtensionXsdFile);
        using var reader = new StreamReader(lazy.Value);
        var content = reader.ReadToEnd();

        content.Should().Be(FileModeWorkspaceBuilder.ExtensionXsdFileContent);
    }

    [Test]
    public void It_returns_core_file_stream_content_for_extension_section()
    {
        var lazy = _provider.LoadXsdContent(FileModeWorkspaceBuilder.CoreXsdFile1, "sample");
        using var reader = new StreamReader(lazy.Value);
        var content = reader.ReadToEnd();

        content.Should().Be(FileModeWorkspaceBuilder.CoreXsdFile1Content);
    }

    [Test]
    public void It_returns_requested_extension_stream_content_for_duplicate_extension_file_name()
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

        var sampleContent = ReadXsdContent(
            _provider.LoadXsdContent(FileModeWorkspaceBuilder.DuplicateExtensionXsdFile, "sample")
        );
        var secondContent = ReadXsdContent(
            _provider.LoadXsdContent(FileModeWorkspaceBuilder.DuplicateExtensionXsdFile, "second")
        );

        sampleContent.Should().Be(FileModeWorkspaceBuilder.SampleDuplicateExtensionXsdContent);
        secondContent.Should().Be(FileModeWorkspaceBuilder.SecondDuplicateExtensionXsdContent);
    }

    [Test]
    public void It_returns_section_stream_content_for_legacy_prefixed_file_name()
    {
        var legacyName = $"EdFi.Sample.ApiSchema.xsd.{FileModeWorkspaceBuilder.ExtensionXsdFile}";

        var content = ReadXsdContent(_provider.LoadXsdContent(legacyName, "sample"));

        content.Should().Be(FileModeWorkspaceBuilder.ExtensionXsdFileContent);
    }

    [Test]
    public void It_throws_for_unknown_xsd_file_name()
    {
        Action action = () => _provider.LoadXsdContent("DoesNotExist.xsd");

        action.Should().Throw<InvalidOperationException>().WithMessage("Couldn't load find the resource");
    }

    private static string ReadXsdContent(Lazy<Stream> xsdContent)
    {
        using var reader = new StreamReader(xsdContent.Value);
        return reader.ReadToEnd();
    }
}

[TestFixture]
public class Given_file_mode_missing_optional_content
{
    private string _workspaceRoot = string.Empty;
    private ContentProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        FileModeWorkspaceBuilder.BuildWorkspace(_workspaceRoot);
        (_provider, _) = FileModeWorkspaceBuilder.BuildProvider(_workspaceRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [Test]
    public void It_returns_empty_files_for_project_without_xsd_directory()
    {
        // "minimal" project has no xsdDirectory key in the manifest
        var files = _provider.Files(@"EdFi\.DataStandard.*\.ApiSchema", ".xsd", "minimal").ToList();

        // Minimal is an extension project but has no xsdDirectory; only core files are returned
        // because no extension xsdDirectory is present (core files always included if core exists)
        files.Should().Equal(FileModeWorkspaceBuilder.CoreXsdFile1, FileModeWorkspaceBuilder.CoreXsdFile2);
    }

    [Test]
    public void It_returns_empty_xsd_list_for_non_xsd_extension()
    {
        var files = _provider.Files("anything", ".json", "ed-fi").ToList();

        files.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_file_mode_path_escape_rejection
{
    private string _workspaceRoot = string.Empty;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        // Build a workspace where the xsdDirectory points outside the workspace root
        Directory.CreateDirectory(_workspaceRoot);

        var manifestJson = """
            {
              "version": 1,
              "projects": [
                {
                  "projectName": "Ed-Fi",
                  "projectEndpointName": "ed-fi",
                  "isExtensionProject": false,
                  "schemaPath": "schemas/Ed-Fi/ApiSchema.json",
                  "xsdDirectory": "../outside"
                }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(_workspaceRoot, "bootstrap-api-schema-manifest.json"), manifestJson);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }
    }

    [Test]
    public void It_throws_InvalidOperationException_when_xsd_directory_escapes_workspace()
    {
        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var manifestLogger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        var manifestProvider = new ApiSchemaAssetManifestProvider(appSettings, manifestLogger);
        var logger = A.Fake<ILogger<ContentProvider>>();
        var provider = new ContentProvider(logger, manifestProvider);

        // Requesting files for core section triggers EnumerateValidatedXsdFiles which validates the path
        Action action = () => provider.Files(@"EdFi\.DataStandard.*\.ApiSchema", ".xsd", "ed-fi").ToList();

        action.Should().Throw<InvalidOperationException>().WithMessage("*parent-directory traversal*");
    }
}
