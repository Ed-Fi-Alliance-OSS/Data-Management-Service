// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Configuration;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.ApiSchema;

[TestFixture]
[NonParallelizable]
public class Given_bundled_ApiSchema_package_content
{
    private ApiSchemaDocumentNodes _nodes = null!;

    [SetUp]
    public void Setup()
    {
        var provider = new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            Options.Create(new AppSettings { AllowIdentityUpdateOverrides = "" }),
            new ApiSchemaValidator(NullLogger<ApiSchemaValidator>.Instance)
        );

        _nodes = provider.GetApiSchemaNodes();
    }

    [Test]
    public void It_loads_the_core_schema_from_the_application_output()
    {
        _nodes
            .CoreApiSchemaRootNode.SelectRequiredNodeFromPathAs<string>(
                "$.projectSchema.projectEndpointName",
                NullLogger.Instance
            )
            .Should()
            .Be("ed-fi");
    }
}

[TestFixture]
[NonParallelizable]
public class Given_bundled_ApiSchema_package_content_without_a_bootstrap_manifest
{
    private string _manifestPath = null!;
    private string _manifestBackupPath = null!;
    private string _legacyRootSchemaPath = null!;
    private string? _originalLegacyRootContent;
    private bool _legacyRootSchemaExisted;
    private IApiSchemaProvider _provider = null!;
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var outputApiSchemaDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ApiSchema");
        _manifestPath = Path.Combine(outputApiSchemaDirectory, "bootstrap-api-schema-manifest.json");
        File.Exists(_manifestPath)
            .Should()
            .BeTrue("bundled package content should materialize a bootstrap manifest in the app output");

        _manifestBackupPath = $"{_manifestPath}.{Guid.NewGuid():N}.bak";
        File.Move(_manifestPath, _manifestBackupPath);

        _legacyRootSchemaPath = Path.Combine(outputApiSchemaDirectory, "ApiSchema.json");
        _legacyRootSchemaExisted = File.Exists(_legacyRootSchemaPath);
        _originalLegacyRootContent = _legacyRootSchemaExisted
            ? File.ReadAllText(_legacyRootSchemaPath)
            : null;

        File.WriteAllText(
            _legacyRootSchemaPath,
            ApiSchemaProviderTestFixtures
                .CreateApiSchema("Legacy", "legacy", isExtensionProject: false)
                .ToJsonString()
        );

        _provider = new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            Options.Create(new AppSettings { AllowIdentityUpdateOverrides = "" }),
            new ApiSchemaValidator(NullLogger<ApiSchemaValidator>.Instance)
        );

        try
        {
            _provider.GetApiSchemaNodes();
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [TearDown]
    public void TearDown()
    {
        if (_legacyRootSchemaExisted)
        {
            File.WriteAllText(_legacyRootSchemaPath, _originalLegacyRootContent ?? string.Empty);
        }
        else if (File.Exists(_legacyRootSchemaPath))
        {
            File.Delete(_legacyRootSchemaPath);
        }

        if (File.Exists(_manifestBackupPath))
        {
            if (File.Exists(_manifestPath))
            {
                File.Delete(_manifestPath);
            }

            File.Move(_manifestBackupPath, _manifestPath);
        }
    }

    [Test]
    public void It_fails_startup()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_reports_the_missing_bundled_manifest()
    {
        _provider
            .ApiSchemaFailures.Should()
            .ContainSingle(f =>
                f.FailureType == "Configuration"
                && f.Message.Contains("bootstrap-api-schema-manifest.json", StringComparison.Ordinal)
                && f.Message.Contains("not found", StringComparison.Ordinal)
            );
    }
}

[TestFixture]
[NonParallelizable]
public class Given_bundled_ApiSchema_package_content_with_a_stale_root_schema_file
{
    private string _legacyRootSchemaPath = null!;
    private string? _originalLegacyRootContent;
    private bool _legacyRootSchemaExisted;
    private ApiSchemaDocumentNodes _nodes = null!;

    [SetUp]
    public void Setup()
    {
        var outputApiSchemaDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ApiSchema");
        File.Exists(Path.Combine(outputApiSchemaDirectory, "bootstrap-api-schema-manifest.json"))
            .Should()
            .BeTrue("bundled package content should materialize a bootstrap manifest in the app output");

        _legacyRootSchemaPath = Path.Combine(outputApiSchemaDirectory, "ApiSchema.json");
        _legacyRootSchemaExisted = File.Exists(_legacyRootSchemaPath);
        _originalLegacyRootContent = _legacyRootSchemaExisted
            ? File.ReadAllText(_legacyRootSchemaPath)
            : null;

        File.WriteAllText(
            _legacyRootSchemaPath,
            ApiSchemaProviderTestFixtures
                .CreateApiSchema("Legacy", "legacy", isExtensionProject: false)
                .ToJsonString()
        );

        var provider = new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            Options.Create(new AppSettings { AllowIdentityUpdateOverrides = "" }),
            new ApiSchemaValidator(NullLogger<ApiSchemaValidator>.Instance)
        );

        _nodes = provider.GetApiSchemaNodes();
    }

    [TearDown]
    public void TearDown()
    {
        if (_legacyRootSchemaExisted)
        {
            File.WriteAllText(_legacyRootSchemaPath, _originalLegacyRootContent ?? string.Empty);
        }
        else if (File.Exists(_legacyRootSchemaPath))
        {
            File.Delete(_legacyRootSchemaPath);
        }
    }

    [Test]
    public void It_uses_the_bundled_manifest_instead_of_recursive_root_file_loading()
    {
        ApiSchemaProviderTestFixtures.GetCoreEndpointName(_nodes).Should().Be("ed-fi");
    }
}

public abstract class ApiSchemaProviderWorkspaceTestBase
{
    protected string WorkspaceRoot = null!;

    [SetUp]
    public void BaseSetUp()
    {
        WorkspaceRoot = Path.Combine(Path.GetTempPath(), $"ApiSchemaProviderTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(WorkspaceRoot);
    }

    [TearDown]
    public void BaseTearDown()
    {
        if (Directory.Exists(WorkspaceRoot))
        {
            Directory.Delete(WorkspaceRoot, recursive: true);
        }
    }

    protected IApiSchemaProvider CreateFileModeProvider()
    {
        var validator = A.Fake<IApiSchemaValidator>();
        A.CallTo(() => validator.Validate(A<JsonNode>._)).Returns(new List<SchemaValidationFailure>());

        return new ApiSchemaProvider(
            NullLogger<ApiSchemaProvider>.Instance,
            Options.Create(
                new AppSettings
                {
                    AllowIdentityUpdateOverrides = "",
                    UseApiSchemaPath = true,
                    ApiSchemaPath = WorkspaceRoot,
                }
            ),
            validator
        );
    }

    protected void WriteSchemaFile(string relativePath, JsonNode schema)
    {
        var filePath = Path.Combine(WorkspaceRoot, relativePath);
        var directory = Path.GetDirectoryName(filePath);
        if (directory is null)
        {
            throw new InvalidOperationException($"Unable to determine directory for '{filePath}'.");
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(filePath, schema.ToJsonString());
    }

    protected void WriteManifest(
        params (string ProjectName, string EndpointName, bool IsExtension, string SchemaPath)[] projects
    )
    {
        var projectNodes = projects
            .Select(project => new JsonObject
            {
                ["projectName"] = project.ProjectName,
                ["projectEndpointName"] = project.EndpointName,
                ["isExtensionProject"] = project.IsExtension,
                ["schemaPath"] = project.SchemaPath,
            })
            .ToArray<JsonNode?>();

        var manifest = new JsonObject { ["version"] = 1, ["projects"] = new JsonArray(projectNodes) };

        File.WriteAllText(
            Path.Combine(WorkspaceRoot, "bootstrap-api-schema-manifest.json"),
            manifest.ToJsonString()
        );
    }
}

[TestFixture]
public class Given_ApiSchemaProvider_workspace_path_resolver
{
    private string _workspaceRoot = null!;
    private string _outsideRoot = null!;
    private ApiSchemaWorkspacePathResolver _resolver = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            $"ApiSchemaWorkspacePathResolverTests_{Guid.NewGuid()}"
        );
        _outsideRoot = Path.Combine(
            Path.GetTempPath(),
            $"ApiSchemaWorkspacePathResolverOutside_{Guid.NewGuid()}"
        );
        Directory.CreateDirectory(_workspaceRoot);
        Directory.CreateDirectory(_outsideRoot);
        _resolver = new ApiSchemaWorkspacePathResolver(_workspaceRoot);
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
    public void It_resolves_a_valid_manifest_relative_path()
    {
        var resolvedPath = _resolver.ResolveManifestRelativePath("schemas/EdFi/ApiSchema.json");

        resolvedPath.Should().Be(Path.Combine(_workspaceRoot, "schemas", "EdFi", "ApiSchema.json"));
    }

    [Test]
    public void It_rejects_a_rooted_path()
    {
        var rootedPath = Path.Combine(Path.GetPathRoot(_workspaceRoot)!, "outside", "ApiSchema.json");

        Action action = () => _resolver.ResolveManifestRelativePath(rootedPath);

        action.Should().Throw<InvalidOperationException>().WithMessage("*absolute (rooted) path*");
    }

    [Test]
    public void It_rejects_parent_directory_traversal()
    {
        Action action = () => _resolver.ResolveManifestRelativePath("schemas/../outside/ApiSchema.json");

        action.Should().Throw<InvalidOperationException>().WithMessage("*parent-directory traversal*");
    }

    [Test]
    public void It_rejects_a_symbolic_link_that_resolves_outside_the_workspace()
    {
        File.WriteAllText(Path.Combine(_outsideRoot, "ApiSchema.json"), "{}");
        var linkPath = Path.Combine(_workspaceRoot, "linked");
        CreateDirectorySymbolicLinkOrIgnore(linkPath, _outsideRoot);

        Action action = () => _resolver.ResolveManifestRelativePath("linked/ApiSchema.json");

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*outside the configured workspace root*symbolic links*");
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

[TestFixture]
public class Given_manifest_backed_workspace_with_a_stale_root_schema_file
    : ApiSchemaProviderWorkspaceTestBase
{
    private ApiSchemaDocumentNodes _nodes = null!;

    [SetUp]
    public void Setup()
    {
        WriteSchemaFile(
            "ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Legacy", "legacy", isExtensionProject: false)
        );
        WriteSchemaFile(
            "Packages/Current/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Ed-Fi", "ed-fi", isExtensionProject: false)
        );
        WriteManifest(("Ed-Fi", "ed-fi", false, "Packages/Current/ApiSchema.json"));

        _nodes = CreateFileModeProvider().GetApiSchemaNodes();
    }

    [Test]
    public void It_loads_only_the_manifest_selected_core_schema()
    {
        ApiSchemaProviderTestFixtures.GetCoreEndpointName(_nodes).Should().Be("ed-fi");
    }
}

[TestFixture]
public class Given_manifest_backed_workspace_with_duplicate_extension_schema_names
    : ApiSchemaProviderWorkspaceTestBase
{
    private ApiSchemaDocumentNodes _nodes = null!;

    [SetUp]
    public void Setup()
    {
        WriteSchemaFile(
            "Packages/Core/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Ed-Fi", "ed-fi", isExtensionProject: false)
        );
        WriteSchemaFile(
            "Packages/Sample/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Sample", "sample", isExtensionProject: true)
        );
        WriteSchemaFile(
            "Packages/Tpdm/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("TPDM", "tpdm", isExtensionProject: true)
        );
        WriteSchemaFile(
            "Packages/Stale/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Stale", "stale", isExtensionProject: true)
        );
        WriteManifest(
            ("Ed-Fi", "ed-fi", false, "Packages/Core/ApiSchema.json"),
            ("TPDM", "tpdm", true, "Packages/Tpdm/ApiSchema.json"),
            ("Sample", "sample", true, "Packages/Sample/ApiSchema.json")
        );

        _nodes = CreateFileModeProvider().GetApiSchemaNodes();
    }

    [Test]
    public void It_loads_only_the_manifest_selected_extension_schemas()
    {
        _nodes
            .ExtensionApiSchemaRootNodes.Select(ApiSchemaProviderTestFixtures.GetEndpointName)
            .Should()
            .Equal("tpdm", "sample");
    }
}

[TestFixture]
public class Given_manifest_declares_a_missing_schema_file_with_a_legacy_fallback_schema
    : ApiSchemaProviderWorkspaceTestBase
{
    private IApiSchemaProvider _provider = null!;
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        WriteSchemaFile(
            "ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Legacy", "legacy", isExtensionProject: false)
        );
        WriteManifest(("Ed-Fi", "ed-fi", false, "Packages/Missing/ApiSchema.json"));

        _provider = CreateFileModeProvider();

        try
        {
            _provider.GetApiSchemaNodes();
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_fails_startup()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_reports_the_missing_manifest_declared_schema_path()
    {
        _provider
            .ApiSchemaFailures.Should()
            .ContainSingle(f =>
                f.FailureType == "FileSystem"
                && f.Message.Contains("Packages/Missing/ApiSchema.json", StringComparison.Ordinal)
            );
    }
}

[TestFixture]
public class Given_manifest_backed_workspace_with_two_schema_documents_marked_as_core
    : ApiSchemaProviderWorkspaceTestBase
{
    private IApiSchemaProvider _provider = null!;
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        WriteSchemaFile(
            "Packages/Core/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Ed-Fi", "ed-fi", isExtensionProject: false)
        );
        WriteSchemaFile(
            "Packages/Sample/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Sample", "sample", isExtensionProject: false)
        );
        WriteManifest(
            ("Ed-Fi", "ed-fi", false, "Packages/Core/ApiSchema.json"),
            ("Sample", "sample", true, "Packages/Sample/ApiSchema.json")
        );

        _provider = CreateFileModeProvider();

        try
        {
            _provider.GetApiSchemaNodes();
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_fails_startup()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_records_a_configuration_failure_for_multiple_core_schemas()
    {
        _provider
            .ApiSchemaFailures.Should()
            .ContainSingle(f =>
                f.FailureType == "Configuration"
                && f.Message.Contains("exactly one core API schema", StringComparison.Ordinal)
                && f.Message.Contains("found 2", StringComparison.Ordinal)
            );
    }
}

[TestFixture]
public class Given_manifest_backed_workspace_with_no_schema_document_marked_as_core
    : ApiSchemaProviderWorkspaceTestBase
{
    private IApiSchemaProvider _provider = null!;
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        WriteSchemaFile(
            "Packages/Core/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Ed-Fi", "ed-fi", isExtensionProject: true)
        );
        WriteSchemaFile(
            "Packages/Sample/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Sample", "sample", isExtensionProject: true)
        );
        WriteManifest(
            ("Ed-Fi", "ed-fi", false, "Packages/Core/ApiSchema.json"),
            ("Sample", "sample", true, "Packages/Sample/ApiSchema.json")
        );

        _provider = CreateFileModeProvider();

        try
        {
            _provider.GetApiSchemaNodes();
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_fails_startup()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_records_a_configuration_failure_for_zero_core_schemas()
    {
        _provider
            .ApiSchemaFailures.Should()
            .ContainSingle(f =>
                f.FailureType == "Configuration"
                && f.Message.Contains("exactly one core API schema", StringComparison.Ordinal)
                && f.Message.Contains("found 0", StringComparison.Ordinal)
            );
    }
}

[TestFixture]
public class Given_manifest_backed_workspace_with_missing_isExtensionProject_field
    : ApiSchemaProviderWorkspaceTestBase
{
    private IApiSchemaProvider _provider = null!;
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        WriteSchemaFile(
            "Packages/Core/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Ed-Fi", "ed-fi", isExtensionProject: false)
        );

        var manifest = new JsonObject
        {
            ["version"] = 1,
            ["projects"] = new JsonArray(
                new JsonObject
                {
                    ["projectName"] = "Ed-Fi",
                    ["projectEndpointName"] = "ed-fi",
                    ["schemaPath"] = "Packages/Core/ApiSchema.json",
                }
            ),
        };
        File.WriteAllText(
            Path.Combine(WorkspaceRoot, "bootstrap-api-schema-manifest.json"),
            manifest.ToJsonString()
        );

        _provider = CreateFileModeProvider();

        try
        {
            _provider.GetApiSchemaNodes();
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_fails_startup()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_records_a_configuration_failure_for_the_manifest_field()
    {
        _provider
            .ApiSchemaFailures.Should()
            .ContainSingle(f =>
                f.FailureType == "Configuration"
                && f.Message.Contains("isExtensionProject", StringComparison.Ordinal)
                && f.Message.Contains("non-null boolean", StringComparison.Ordinal)
            );
    }
}

[TestFixture]
public class Given_workspace_without_a_bootstrap_manifest : ApiSchemaProviderWorkspaceTestBase
{
    private IApiSchemaProvider _provider = null!;
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        WriteSchemaFile(
            "ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Legacy", "legacy", isExtensionProject: false)
        );
        WriteSchemaFile(
            "Extensions/ApiSchema.Sample.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema("Sample", "sample", isExtensionProject: true)
        );

        _provider = CreateFileModeProvider();

        try
        {
            _provider.GetApiSchemaNodes();
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_fails_startup()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
    }

    [Test]
    public void It_reports_the_missing_bootstrap_manifest()
    {
        _provider
            .ApiSchemaFailures.Should()
            .ContainSingle(f =>
                f.FailureType == "Configuration"
                && f.Message.Contains("bootstrap-api-schema-manifest.json", StringComparison.Ordinal)
                && f.Message.Contains("not found", StringComparison.Ordinal)
            );
    }
}

internal static class ApiSchemaProviderTestFixtures
{
    public static JsonNode CreateApiSchema(
        string projectName,
        string projectEndpointName,
        bool isExtensionProject
    )
    {
        return new JsonObject
        {
            ["apiSchemaVersion"] = "1.0.0",
            ["projectSchema"] = new JsonObject
            {
                ["abstractResources"] = new JsonObject(),
                ["caseInsensitiveEndpointNameMapping"] = new JsonObject(),
                ["description"] = $"{projectName} description",
                ["educationOrganizationHierarchy"] = new JsonObject(),
                ["educationOrganizationTypes"] = new JsonArray(),
                ["domains"] = new JsonArray(),
                ["isExtensionProject"] = isExtensionProject,
                ["projectName"] = projectName,
                ["projectVersion"] = "1.0.0",
                ["projectEndpointName"] = projectEndpointName,
                ["resourceNameMapping"] = new JsonObject(),
                ["resourceSchemas"] = new JsonObject(),
            },
        };
    }

    public static string GetCoreEndpointName(ApiSchemaDocumentNodes nodes)
    {
        return GetEndpointName(nodes.CoreApiSchemaRootNode);
    }

    public static string GetEndpointName(JsonNode node)
    {
        return node.SelectRequiredNodeFromPathAs<string>(
            "$.projectSchema.projectEndpointName",
            NullLogger.Instance
        );
    }
}
