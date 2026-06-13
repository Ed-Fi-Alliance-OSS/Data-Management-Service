// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Frontend.AspNetCore.Content;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Content;

[TestFixture]
public class Given_a_valid_manifest_workspace
{
    private string _workspaceRoot = string.Empty;
    private IApiSchemaAssetManifestProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workspaceRoot);

        var manifestJson = """
            {
              "version": 1,
              "projects": [
                {
                  "projectName": "EdFi",
                  "projectEndpointName": "ed-fi",
                  "isExtensionProject": false,
                  "schemaPath": "schemas/EdFi/ApiSchema.json",
                  "discoverySpecPath": "content/EdFi/discovery-spec.json",
                  "xsdDirectory": "content/EdFi/xsd"
                },
                {
                  "projectName": "Sample",
                  "projectEndpointName": "sample",
                  "isExtensionProject": true,
                  "schemaPath": "schemas/Sample/ApiSchema.json"
                }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(_workspaceRoot, "bootstrap-api-schema-manifest.json"), manifestJson);

        Directory.CreateDirectory(Path.Combine(_workspaceRoot, "content", "EdFi", "xsd"));
        File.WriteAllText(
            Path.Combine(_workspaceRoot, "content", "EdFi", "xsd", "Interchange-Student.xsd"),
            "<schema/>"
        );
        File.WriteAllText(
            Path.Combine(_workspaceRoot, "content", "EdFi", "xsd", "Ed-Fi-Core.xsd"),
            "<schema/>"
        );

        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var logger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        _provider = new ApiSchemaAssetManifestProvider(appSettings, logger);
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
    public void It_returns_the_manifest_with_correct_version()
    {
        var manifest = _provider.GetManifest();

        manifest.Version.Should().Be(1);
    }

    [Test]
    public void It_returns_all_projects()
    {
        var manifest = _provider.GetManifest();

        manifest.Projects.Should().HaveCount(2);
    }

    [Test]
    public void It_returns_core_project_fields_correctly()
    {
        var manifest = _provider.GetManifest();
        var core = manifest.Projects[0];

        core.ProjectName.Should().Be("EdFi");
        core.ProjectEndpointName.Should().Be("ed-fi");
        core.IsExtensionProject.Should().BeFalse();
        core.SchemaPath.Should().Be("schemas/EdFi/ApiSchema.json");
        core.DiscoverySpecPath.Should().Be("content/EdFi/discovery-spec.json");
        core.XsdDirectory.Should().Be("content/EdFi/xsd");
    }

    [Test]
    public void It_returns_extension_project_with_optional_fields_absent()
    {
        var manifest = _provider.GetManifest();
        var extension = manifest.Projects[1];

        extension.ProjectName.Should().Be("Sample");
        extension.IsExtensionProject.Should().BeTrue();
        extension.DiscoverySpecPath.Should().BeNull();
        extension.XsdDirectory.Should().BeNull();
    }

    [Test]
    public void It_resolves_a_valid_relative_path()
    {
        var resolved = _provider.ResolveValidatedPath("schemas/EdFi/ApiSchema.json");

        resolved.Should().StartWith(_workspaceRoot);
        resolved.Should().EndWith("ApiSchema.json");
    }

    [Test]
    public void It_enumerates_xsd_files_for_a_project_with_xsd_directory()
    {
        var manifest = _provider.GetManifest();
        var coreProject = manifest.Projects[0];

        var xsdFiles = _provider.EnumerateValidatedXsdFiles(coreProject).ToList();

        xsdFiles.Should().HaveCount(2);
        xsdFiles.Select(Path.GetFileName).Should().Equal("Ed-Fi-Core.xsd", "Interchange-Student.xsd");
    }

    [Test]
    public void It_returns_empty_enumeration_for_project_without_xsd_directory()
    {
        var manifest = _provider.GetManifest();
        var extensionProject = manifest.Projects[1];

        var xsdFiles = _provider.EnumerateValidatedXsdFiles(extensionProject).ToList();

        xsdFiles.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_a_manifest_with_declared_missing_xsd_directory
{
    private string _workspaceRoot = string.Empty;
    private IApiSchemaAssetManifestProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workspaceRoot);

        var manifestJson = """
            {
              "version": 1,
              "projects": [
                {
                  "projectName": "EdFi",
                  "projectEndpointName": "ed-fi",
                  "isExtensionProject": false,
                  "schemaPath": "schemas/EdFi/ApiSchema.json",
                  "xsdDirectory": "content/EdFi/missing-xsd"
                }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(_workspaceRoot, "bootstrap-api-schema-manifest.json"), manifestJson);

        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var logger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        _provider = new ApiSchemaAssetManifestProvider(appSettings, logger);
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
    public void It_throws_for_declared_missing_xsd_directory()
    {
        var project = _provider.GetManifest().Projects[0];

        Action action = () => _provider.EnumerateValidatedXsdFiles(project).ToList();

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*EdFi*xsdDirectory*content/EdFi/missing-xsd*does not exist*");
    }
}

[TestFixture]
public class Given_a_manifest_with_nested_xsd_files
{
    private string _workspaceRoot = string.Empty;
    private IApiSchemaAssetManifestProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workspaceRoot);

        var manifestJson = """
            {
              "version": 1,
              "projects": [
                {
                  "projectName": "EdFi",
                  "projectEndpointName": "ed-fi",
                  "isExtensionProject": false,
                  "schemaPath": "schemas/EdFi/ApiSchema.json",
                  "xsdDirectory": "content/EdFi/xsd"
                }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(_workspaceRoot, "bootstrap-api-schema-manifest.json"), manifestJson);

        var xsdDirectory = Path.Combine(_workspaceRoot, "content", "EdFi", "xsd");
        Directory.CreateDirectory(xsdDirectory);
        File.WriteAllText(Path.Combine(xsdDirectory, "Ed-Fi-Core.xsd"), "<schema/>");

        var nestedDirectory = Path.Combine(xsdDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(Path.Combine(nestedDirectory, "Interchange-Student.xsd"), "<schema/>");

        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var logger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        _provider = new ApiSchemaAssetManifestProvider(appSettings, logger);
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
    public void It_throws_for_nested_xsd_files_under_declared_xsd_directory()
    {
        var project = _provider.GetManifest().Projects[0];

        Action action = () => _provider.EnumerateValidatedXsdFiles(project).ToList();

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*EdFi*nested XSD file*Interchange-Student.xsd*flattened*xsdDirectory*");
    }
}

[TestFixture]
public class Given_package_manifests_without_a_bootstrap_manifest
{
    private string _workspaceRoot = string.Empty;
    private IApiSchemaAssetManifestProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workspaceRoot);

        var corePackageRoot = Path.Combine(_workspaceRoot, "Packages", "EdFi.DataStandard52.ApiSchema");
        Directory.CreateDirectory(Path.Combine(corePackageRoot, "xsd"));
        File.WriteAllText(Path.Combine(corePackageRoot, "ApiSchema.json"), "{}");
        File.WriteAllText(Path.Combine(corePackageRoot, "discovery-spec.json"), "{}");
        File.WriteAllText(Path.Combine(corePackageRoot, "xsd", "Ed-Fi-Core.xsd"), "<schema/>");
        File.WriteAllText(
            Path.Combine(corePackageRoot, "package-manifest.json"),
            """
            {
              "version": 1,
              "packageId": "EdFi.DataStandard52.ApiSchema",
              "projectName": "Ed-Fi",
              "projectEndpointName": "ed-fi",
              "isExtensionProject": false,
              "schemaPath": "ApiSchema.json",
              "discoverySpecPath": "discovery-spec.json",
              "xsdDirectory": "xsd"
            }
            """
        );

        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var logger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        _provider = new ApiSchemaAssetManifestProvider(appSettings, logger);
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
    public void It_throws_instead_of_synthesizing_a_manifest_from_package_manifests()
    {
        Action action = () => _provider.GetManifest();

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*bootstrap-api-schema-manifest.json*not found*");
    }
}

[TestFixture]
public class Given_a_manifest_with_explicit_null_optional_fields
{
    private string _workspaceRoot = string.Empty;
    private IApiSchemaAssetManifestProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workspaceRoot);

        var manifestJson = """
            {
              "version": 1,
              "projects": [
                {
                  "projectName": "EdFi",
                  "projectEndpointName": "ed-fi",
                  "isExtensionProject": false,
                  "schemaPath": "schemas/EdFi/ApiSchema.json",
                  "discoverySpecPath": null,
                  "xsdDirectory": null
                }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(_workspaceRoot, "bootstrap-api-schema-manifest.json"), manifestJson);

        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var logger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        _provider = new ApiSchemaAssetManifestProvider(appSettings, logger);
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
    public void It_treats_explicit_null_as_not_provided()
    {
        var manifest = _provider.GetManifest();
        var project = manifest.Projects[0];

        project.DiscoverySpecPath.Should().BeNull();
        project.XsdDirectory.Should().BeNull();
    }

    [Test]
    public void It_returns_empty_xsd_enumeration_for_null_xsd_directory()
    {
        var manifest = _provider.GetManifest();
        var project = manifest.Projects[0];

        var xsdFiles = _provider.EnumerateValidatedXsdFiles(project).ToList();

        xsdFiles.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_a_missing_manifest_file
{
    private string _workspaceRoot = string.Empty;
    private IApiSchemaAssetManifestProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workspaceRoot);

        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var logger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        _provider = new ApiSchemaAssetManifestProvider(appSettings, logger);
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
    public void It_throws_InvalidOperationException()
    {
        Action action = () => _provider.GetManifest();

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*bootstrap-api-schema-manifest.json*not found*");
    }
}

[TestFixture]
public class Given_a_malformed_manifest_file
{
    private string _workspaceRoot = string.Empty;
    private IApiSchemaAssetManifestProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workspaceRoot);

        File.WriteAllText(
            Path.Combine(_workspaceRoot, "bootstrap-api-schema-manifest.json"),
            "{ this is not valid JSON }"
        );

        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var logger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        _provider = new ApiSchemaAssetManifestProvider(appSettings, logger);
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
    public void It_throws_InvalidOperationException_with_malformed_json_message()
    {
        Action action = () => _provider.GetManifest();

        action.Should().Throw<InvalidOperationException>().WithMessage("*malformed JSON*");
    }
}

[TestFixture]
public class Given_a_manifest_with_unsupported_version
{
    private string _workspaceRoot = string.Empty;
    private IApiSchemaAssetManifestProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workspaceRoot);

        var manifestJson = """
            {
              "version": 99,
              "projects": [
                {
                  "projectName": "EdFi",
                  "projectEndpointName": "ed-fi",
                  "isExtensionProject": false,
                  "schemaPath": "schemas/EdFi/ApiSchema.json"
                }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(_workspaceRoot, "bootstrap-api-schema-manifest.json"), manifestJson);

        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var logger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        _provider = new ApiSchemaAssetManifestProvider(appSettings, logger);
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
    public void It_throws_InvalidOperationException_with_version_message()
    {
        Action action = () => _provider.GetManifest();

        action.Should().Throw<InvalidOperationException>().WithMessage("*version 99*not supported*");
    }
}

[TestFixture]
public class Given_a_manifest_with_zero_projects
{
    private string _workspaceRoot = string.Empty;
    private IApiSchemaAssetManifestProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workspaceRoot);

        var manifestJson = """
            {
              "version": 1,
              "projects": []
            }
            """;

        File.WriteAllText(Path.Combine(_workspaceRoot, "bootstrap-api-schema-manifest.json"), manifestJson);

        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var logger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        _provider = new ApiSchemaAssetManifestProvider(appSettings, logger);
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
    public void It_throws_InvalidOperationException_with_zero_projects_message()
    {
        Action action = () => _provider.GetManifest();

        action.Should().Throw<InvalidOperationException>().WithMessage("*zero projects*");
    }
}

[TestFixture]
public class Given_path_validation_in_manifest_provider
{
    private string _workspaceRoot = string.Empty;
    private IApiSchemaAssetManifestProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workspaceRoot);

        var manifestJson = """
            {
              "version": 1,
              "projects": [
                {
                  "projectName": "EdFi",
                  "projectEndpointName": "ed-fi",
                  "isExtensionProject": false,
                  "schemaPath": "schemas/EdFi/ApiSchema.json"
                }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(_workspaceRoot, "bootstrap-api-schema-manifest.json"), manifestJson);

        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var logger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        _provider = new ApiSchemaAssetManifestProvider(appSettings, logger);
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
    public void It_rejects_a_rooted_absolute_path()
    {
        Action action = () => _provider.ResolveValidatedPath("/etc/passwd");

        action.Should().Throw<InvalidOperationException>().WithMessage("*absolute (rooted) path*");
    }

    [Test]
    public void It_rejects_a_parent_directory_traversal_path()
    {
        Action action = () => _provider.ResolveValidatedPath("../outside/file.json");

        action.Should().Throw<InvalidOperationException>().WithMessage("*parent-directory traversal*");
    }

    [Test]
    public void It_rejects_a_deeply_nested_traversal_that_resolves_outside_root()
    {
        Action action = () => _provider.ResolveValidatedPath("subdir/../../outside/file.json");

        action.Should().Throw<InvalidOperationException>().WithMessage("*parent-directory traversal*");
    }

    [Test]
    public void It_resolves_a_valid_nested_relative_path()
    {
        var resolved = _provider.ResolveValidatedPath("schemas/EdFi/ApiSchema.json");

        var expected = Path.Combine(_workspaceRoot, "schemas", "EdFi", "ApiSchema.json");
        resolved.Should().Be(expected);
    }
}

[TestFixture]
public class Given_manifest_paths_that_are_symbolic_links
{
    private string _workspaceRoot = string.Empty;
    private string _outsideRoot = string.Empty;
    private IApiSchemaAssetManifestProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _outsideRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_workspaceRoot);
        Directory.CreateDirectory(_outsideRoot);

        var manifestJson = """
            {
              "version": 1,
              "projects": [
                {
                  "projectName": "EdFi",
                  "projectEndpointName": "ed-fi",
                  "isExtensionProject": false,
                  "schemaPath": "schemas/EdFi/ApiSchema.json",
                  "discoverySpecPath": "content/EdFi/discovery-spec.json",
                  "xsdDirectory": "content/EdFi/xsd"
                }
              ]
            }
            """;

        File.WriteAllText(Path.Combine(_workspaceRoot, "bootstrap-api-schema-manifest.json"), manifestJson);

        var appSettings = Options.Create(
            new AppSettings
            {
                ApiSchemaPath = _workspaceRoot,
                UseApiSchemaPath = true,
                AllowIdentityUpdateOverrides = "",
            }
        );
        var logger = A.Fake<ILogger<ApiSchemaAssetManifestProvider>>();
        _provider = new ApiSchemaAssetManifestProvider(appSettings, logger);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }

        if (Directory.Exists(_outsideRoot))
        {
            Directory.Delete(_outsideRoot, recursive: true);
        }
    }

    [Test]
    public void It_rejects_a_discovery_spec_symlink_that_resolves_outside_the_workspace()
    {
        var contentDirectory = Path.Combine(_workspaceRoot, "content", "EdFi");
        Directory.CreateDirectory(contentDirectory);

        var outsideDiscoverySpecPath = Path.Combine(_outsideRoot, "discovery-spec.json");
        File.WriteAllText(outsideDiscoverySpecPath, "{}");

        var discoverySpecLinkPath = Path.Combine(contentDirectory, "discovery-spec.json");
        CreateFileSymbolicLinkOrIgnore(discoverySpecLinkPath, outsideDiscoverySpecPath);

        var project = _provider.GetManifest().Projects[0];
        Action action = () => _provider.ResolveValidatedPath(project.DiscoverySpecPath!);

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*outside the configured workspace root*symbolic links*");
    }

    [Test]
    public void It_rejects_an_xsd_directory_symlink_that_resolves_outside_the_workspace()
    {
        var contentDirectory = Path.Combine(_workspaceRoot, "content", "EdFi");
        Directory.CreateDirectory(contentDirectory);

        var outsideXsdDirectory = Path.Combine(_outsideRoot, "xsd");
        Directory.CreateDirectory(outsideXsdDirectory);
        File.WriteAllText(Path.Combine(outsideXsdDirectory, "Ed-Fi-Core.xsd"), "<schema/>");

        var xsdDirectoryLinkPath = Path.Combine(contentDirectory, "xsd");
        CreateDirectorySymbolicLinkOrIgnore(xsdDirectoryLinkPath, outsideXsdDirectory);

        var project = _provider.GetManifest().Projects[0];
        Action action = () => _provider.EnumerateValidatedXsdFiles(project).ToList();

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*outside the configured workspace root*symbolic links*");
    }

    private static void CreateFileSymbolicLinkOrIgnore(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or NotSupportedException
                        or PlatformNotSupportedException
                        or UnauthorizedAccessException
            )
        {
            Assert.Ignore($"Symbolic link creation is not available: {ex.Message}");
        }
    }

    private static void CreateDirectorySymbolicLinkOrIgnore(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or NotSupportedException
                        or PlatformNotSupportedException
                        or UnauthorizedAccessException
            )
        {
            Assert.Ignore($"Symbolic link creation is not available: {ex.Message}");
        }
    }
}
