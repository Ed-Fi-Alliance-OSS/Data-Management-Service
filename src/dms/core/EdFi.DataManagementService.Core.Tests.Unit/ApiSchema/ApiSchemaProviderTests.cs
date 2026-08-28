// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
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

        // The resolver canonicalizes symbolic links by design. macOS exposes TMPDIR under /var,
        // itself a symlink to /private/var, so the fixture root has to be canonical too or every
        // expectation compares a pre-resolution path against a post-resolution one.
        _workspaceRoot = _resolver.CanonicalWorkspaceRoot;
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
public class Given_manifest_backed_workspace_with_changeQueries_openapi_base_documents
    : ApiSchemaProviderWorkspaceTestBase
{
    private ApiSchemaDocumentNodes _nodes = null!;

    [SetUp]
    public void Setup()
    {
        WriteSchemaFile(
            "Packages/Core/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchemaWithChangeQueriesOpenApiBaseDocument(
                "Ed-Fi",
                "ed-fi",
                isExtensionProject: false,
                operationId: "getAvailableChangeVersions"
            )
        );
        WriteSchemaFile(
            "Packages/Sample/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchemaWithChangeQueriesOpenApiBaseDocument(
                "Sample",
                "sample",
                isExtensionProject: true,
                operationId: "extensionOnly"
            )
        );
        WriteManifest(
            ("Ed-Fi", "ed-fi", false, "Packages/Core/ApiSchema.json"),
            ("Sample", "sample", true, "Packages/Sample/ApiSchema.json")
        );

        _nodes = CreateFileModeProvider().GetApiSchemaNodes();
    }

    [Test]
    public void It_preserves_the_core_changeQueries_openApi_base_document_on_raw_loaded_nodes()
    {
        _nodes
            .CoreApiSchemaRootNode["projectSchema"]
            ?["openApiBaseDocuments"]?["changeQueries"]?["paths"]?["/availableChangeVersions"]?["get"]?[
                "operationId"
            ]?.GetValue<string>()
            .Should()
            .Be("getAvailableChangeVersions");
    }

    [Test]
    public void It_preserves_extension_changeQueries_openApi_base_documents_on_raw_loaded_nodes()
    {
        _nodes
            .ExtensionApiSchemaRootNodes[0]["projectSchema"]
            ?["openApiBaseDocuments"]?["changeQueries"]?["paths"]?["/availableChangeVersions"]?["get"]?[
                "operationId"
            ]?.GetValue<string>()
            .Should()
            .Be("extensionOnly");
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
    public void It_records_a_configuration_failure_for_the_schema_identity_mismatch()
    {
        _provider
            .ApiSchemaFailures.Should()
            .ContainSingle(f =>
                f.FailureType == "Configuration"
                && f.Message.Contains("Sample", StringComparison.Ordinal)
                && f.Message.Contains("isExtensionProject", StringComparison.Ordinal)
                && f.Message.Contains("true", StringComparison.Ordinal)
                && f.Message.Contains("false", StringComparison.Ordinal)
                && f.Message.Contains("Packages/Sample/ApiSchema.json", StringComparison.Ordinal)
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
    public void It_records_a_configuration_failure_for_the_schema_identity_mismatch()
    {
        _provider
            .ApiSchemaFailures.Should()
            .ContainSingle(f =>
                f.FailureType == "Configuration"
                && f.Message.Contains("Ed-Fi", StringComparison.Ordinal)
                && f.Message.Contains("isExtensionProject", StringComparison.Ordinal)
                && f.Message.Contains("false", StringComparison.Ordinal)
                && f.Message.Contains("true", StringComparison.Ordinal)
                && f.Message.Contains("Packages/Core/ApiSchema.json", StringComparison.Ordinal)
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
public class Given_manifest_backed_workspace_with_a_null_project_entry : ApiSchemaProviderWorkspaceTestBase
{
    private IApiSchemaProvider _provider = null!;
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var manifest = new JsonObject { ["version"] = 1, ["projects"] = new JsonArray((JsonNode?)null) };
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
    public void It_records_a_configuration_failure_for_the_null_project()
    {
        _provider
            .ApiSchemaFailures.Should()
            .ContainSingle(f =>
                f.FailureType == "Configuration"
                && f.Message.Contains("null project entry", StringComparison.Ordinal)
                && f.Message.Contains("index 0", StringComparison.Ordinal)
            );
    }
}

[TestFixture]
public class Given_manifest_backed_workspace_with_blank_identity_fields : ApiSchemaProviderWorkspaceTestBase
{
    [TestCase("projectName")]
    [TestCase("projectEndpointName")]
    [TestCase("schemaPath")]
    public void It_records_a_configuration_failure_for_the_blank_manifest_field(string fieldName)
    {
        var project = new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["isExtensionProject"] = false,
            ["schemaPath"] = "Packages/Core/ApiSchema.json",
        };
        project[fieldName] = "   ";

        var manifest = new JsonObject { ["version"] = 1, ["projects"] = new JsonArray(project) };
        File.WriteAllText(
            Path.Combine(WorkspaceRoot, "bootstrap-api-schema-manifest.json"),
            manifest.ToJsonString()
        );

        var provider = CreateFileModeProvider();
        Exception? exception = null;

        try
        {
            provider.GetApiSchemaNodes();
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        exception.Should().BeOfType<InvalidOperationException>();
        provider
            .ApiSchemaFailures.Should()
            .ContainSingle(f =>
                f.FailureType == "Configuration"
                && f.Message.Contains($"non-empty {fieldName}", StringComparison.Ordinal)
            );
    }
}

public abstract class ManifestSchemaIdentityMismatchTestBase : ApiSchemaProviderWorkspaceTestBase
{
    private IApiSchemaProvider _provider = null!;
    private Exception? _exception;

    protected virtual string ManifestProjectName => "Ed-Fi";
    protected virtual string ManifestProjectEndpointName => "ed-fi";
    protected virtual bool ManifestIsExtensionProject => false;
    protected virtual string SchemaProjectName => "Ed-Fi";
    protected virtual string SchemaProjectEndpointName => "ed-fi";
    protected virtual bool SchemaIsExtensionProject => false;
    protected abstract string FieldName { get; }
    protected abstract string DeclaredValue { get; }
    protected abstract string SchemaValue { get; }

    [SetUp]
    public void Setup()
    {
        WriteSchemaFile(
            "Packages/Core/ApiSchema.json",
            ApiSchemaProviderTestFixtures.CreateApiSchema(
                SchemaProjectName,
                SchemaProjectEndpointName,
                SchemaIsExtensionProject
            )
        );
        WriteManifest(
            (
                ManifestProjectName,
                ManifestProjectEndpointName,
                ManifestIsExtensionProject,
                "Packages/Core/ApiSchema.json"
            )
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
    public void It_records_a_configuration_failure_that_identifies_the_mismatch()
    {
        _provider
            .ApiSchemaFailures.Should()
            .ContainSingle(f =>
                f.FailureType == "Configuration"
                && f.Message.Contains("Ed-Fi", StringComparison.Ordinal)
                && f.Message.Contains(FieldName, StringComparison.Ordinal)
                && f.Message.Contains(DeclaredValue, StringComparison.Ordinal)
                && f.Message.Contains(SchemaValue, StringComparison.Ordinal)
                && f.Message.Contains("Packages/Core/ApiSchema.json", StringComparison.Ordinal)
            );
    }
}

[TestFixture]
public class Given_manifest_project_name_does_not_match_schema : ManifestSchemaIdentityMismatchTestBase
{
    protected override string SchemaProjectName => "Actual";
    protected override string FieldName => "projectName";
    protected override string DeclaredValue => "Ed-Fi";
    protected override string SchemaValue => "Actual";
}

[TestFixture]
public class Given_manifest_project_endpoint_name_does_not_match_schema
    : ManifestSchemaIdentityMismatchTestBase
{
    protected override string SchemaProjectEndpointName => "actual";
    protected override string FieldName => "projectEndpointName";
    protected override string DeclaredValue => "ed-fi";
    protected override string SchemaValue => "actual";
}

[TestFixture]
public class Given_manifest_is_extension_project_does_not_match_schema
    : ManifestSchemaIdentityMismatchTestBase
{
    protected override bool SchemaIsExtensionProject => true;
    protected override string FieldName => "isExtensionProject";
    protected override string DeclaredValue => "false";
    protected override string SchemaValue => "true";
}

[TestFixture]
public class Given_ApiSchemaAssetManifestReader_with_malformed_manifest_content
    : ApiSchemaProviderWorkspaceTestBase
{
    [Test]
    public void It_rejects_malformed_json_with_a_parse_failure_type()
    {
        Action action = () => ApiSchemaAssetManifestReader.ReadFromJson("{ nope", WorkspaceRoot);

        action
            .Should()
            .Throw<ApiSchemaAssetManifestException>()
            .Where(ex => ex.FailureType == "ParseError")
            .WithMessage("*malformed JSON*");
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
        JsonObject projectSchema = new()
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
        };

        // A core project's resource and descriptor base documents are what the metadata documents are
        // assembled from, so a core project without them is not a valid schema.
        if (!isExtensionProject)
        {
            projectSchema["openApiBaseDocuments"] = new JsonObject
            {
                ["resources"] = BaseDocument(isExtensionProject),
                ["descriptors"] = BaseDocument(isExtensionProject),
            };
        }

        return new JsonObject { ["apiSchemaVersion"] = "1.0.0", ["projectSchema"] = projectSchema };
    }

    public static JsonNode CreateApiSchemaWithChangeQueriesOpenApiBaseDocument(
        string projectName,
        string projectEndpointName,
        bool isExtensionProject,
        string operationId
    )
    {
        var schema = CreateApiSchema(projectName, projectEndpointName, isExtensionProject);
        schema["projectSchema"]!.AsObject()["openApiBaseDocuments"] = new JsonObject
        {
            ["resources"] = BaseDocument(isExtensionProject),
            ["descriptors"] = BaseDocument(isExtensionProject),
            ["changeQueries"] = new JsonObject
            {
                ["openapi"] = "3.0.1",
                ["paths"] = new JsonObject
                {
                    ["/availableChangeVersions"] = new JsonObject
                    {
                        ["get"] = new JsonObject { ["operationId"] = operationId },
                    },
                },
            },
        };

        return schema;
    }

    /// <summary>
    /// Only a core project's resource and descriptor base documents are assembled, so only a core project
    /// is required to declare the cursor-paging parameter components. An extension keeps the minimal shape.
    /// </summary>
    private static JsonObject BaseDocument(bool isExtensionProject)
    {
        JsonObject baseDocument = new() { ["openapi"] = "3.0.1", ["paths"] = new JsonObject() };

        if (!isExtensionProject)
        {
            baseDocument["components"] = new JsonObject
            {
                ["parameters"] = ApiSchemaBuilder.CursorPagingParameterComponents(),
                ["schemas"] = new JsonObject(),
            };
        }

        return baseDocument;
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

/// <summary>
/// Reads Namespace securable elements out of a pinned <c>EdFi.DataStandard*.ApiSchema</c> package
/// exactly as NuGet restored it. The packaged payload is the artifact the DMS runtime provisions
/// from by default, so the resource-root scope rule has to hold in the package itself rather than
/// only in the checked-in authoritative fixtures, and it has to hold with nothing standing between
/// the package and the assertion, because DMS deliberately applies no load-time correction.
/// </summary>
internal static class PackagedApiSchemaContract
{
    /// <summary>
    /// Parses <c>projectSchema.resourceSchemas</c> from the packaged <c>ApiSchema.json</c> of the
    /// package whose restored root the build recorded under
    /// <paramref name="packageRootMetadataKey"/>.
    /// </summary>
    public static JsonObject LoadPackagedResourceSchemas(string packageRootMetadataKey)
    {
        string? packageRoot = typeof(PackagedApiSchemaContract)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute =>
                string.Equals(attribute.Key, packageRootMetadataKey, StringComparison.Ordinal)
            )
            ?.Value;

        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            throw new InvalidOperationException(
                $"The build recorded no restored package root for '{packageRootMetadataKey}'. The test "
                    + "project must reference that package with GeneratePathProperty enabled."
            );
        }

        string apiSchemaPath = Path.Combine(
            packageRoot,
            "contentFiles",
            "any",
            "any",
            "ApiSchema",
            "ApiSchema.json"
        );

        if (!File.Exists(apiSchemaPath))
        {
            throw new FileNotFoundException(
                $"Packaged ApiSchema not found for '{packageRootMetadataKey}': {apiSchemaPath}",
                apiSchemaPath
            );
        }

        if (
            JsonNode.Parse(File.ReadAllText(apiSchemaPath))?["projectSchema"]?["resourceSchemas"]
            is not JsonObject resourceSchemas
        )
        {
            throw new InvalidOperationException(
                $"Packaged ApiSchema is missing projectSchema.resourceSchemas: {apiSchemaPath}"
            );
        }

        return resourceSchemas;
    }

    /// <summary>
    /// Extracts every <c>securableElements.Namespace</c> entry with its declaring resource endpoint
    /// name, e.g. <c>("studentAssessments", "$.assessmentReference.namespace")</c>.
    /// </summary>
    public static IReadOnlyList<(string Resource, string Path)> NamespaceSecurablePaths(
        JsonObject resourceSchemas
    )
    {
        List<(string Resource, string Path)> paths = [];

        foreach ((string resourceName, JsonNode? resourceSchema) in resourceSchemas)
        {
            if (resourceSchema?["securableElements"]?["Namespace"] is not JsonArray namespacePaths)
            {
                continue;
            }

            foreach (JsonNode? pathNode in namespacePaths)
            {
                string path =
                    pathNode?.GetValue<string>()
                    ?? throw new InvalidOperationException(
                        $"Resource '{resourceName}' has a null securableElements.Namespace entry."
                    );
                paths.Add((resourceName, path));
            }
        }

        return paths;
    }

    public static IReadOnlyList<(string Resource, string Path)> CollectionScopedPaths(
        IReadOnlyList<(string Resource, string Path)> namespacePaths
    ) => [.. namespacePaths.Where(entry => entry.Path.Contains("[*]", StringComparison.Ordinal))];

    public static IReadOnlyList<(string Resource, string Path)> ExtensionScopedPaths(
        IReadOnlyList<(string Resource, string Path)> namespacePaths
    ) => [.. namespacePaths.Where(entry => entry.Path.Contains("._ext.", StringComparison.Ordinal))];

    public static Dictionary<string, string[]> GroupByResource(
        IReadOnlyList<(string Resource, string Path)> namespacePaths
    ) =>
        namespacePaths
            .GroupBy(entry => entry.Resource, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Path).ToArray(),
                StringComparer.Ordinal
            );

    /// <summary>
    /// Recursively collects every string value beneath a resource-schema section, so a retention
    /// check can assert a JSONPath is still carried by that section regardless of which property
    /// holds it.
    /// </summary>
    public static HashSet<string> CollectStringValues(JsonNode? node)
    {
        HashSet<string> values = new(StringComparer.Ordinal);
        Collect(node, values);
        return values;

        static void Collect(JsonNode? current, HashSet<string> accumulator)
        {
            switch (current)
            {
                case JsonObject jsonObject:
                    foreach ((_, JsonNode? child) in jsonObject)
                    {
                        Collect(child, accumulator);
                    }
                    break;
                case JsonArray jsonArray:
                    foreach (JsonNode? child in jsonArray)
                    {
                        Collect(child, accumulator);
                    }
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue):
                    accumulator.Add(stringValue);
                    break;
            }
        }
    }
}

/// <summary>
/// Namespace-based authorization resolves a securable element only against the resource's own root
/// table, so a <c>securableElements.Namespace</c> path that descends into a collection
/// (<c>[*]</c>) or into a resource-extension container (<c>._ext.</c>) can never take part in an
/// authorization plan; it only yields an authorization index no plan will use. Both Data Standard
/// families this repository pins are held to that rule, so the assertions are shared and only the
/// paths each model declares differ.
/// </summary>
/// <summary>
/// A resource endpoint name paired with the exact <c>securableElements.Namespace</c> paths that
/// resource must still declare, in declaration order.
/// </summary>
/// <param name="Resource">The resource endpoint name, e.g. <c>studentAssessments</c>.</param>
/// <param name="Paths">The root-scope Namespace securable paths the resource must declare.</param>
public readonly record struct RetainedNamespaceSecurables(string Resource, string[] Paths);

public abstract class PackagedApiSchemaNamespaceSecurableContract
{
    /// <summary>
    /// Key of the assembly metadata entry carrying the restored package root to read.
    /// </summary>
    protected abstract string PackageRootMetadataKey { get; }

    /// <summary>
    /// The collection-scoped Namespace paths this Data Standard historically declared and that must
    /// never reappear as securable elements. Each one remains valid reference metadata elsewhere in
    /// its resource schema.
    /// </summary>
    protected abstract IReadOnlyList<(string Resource, string Path)> RemovedCollectionPaths { get; }

    /// <summary>
    /// Root-scope Namespace securable elements that must survive intact, covering both the resources
    /// the fix touched and unaffected resources that prove it did not over-remove.
    /// </summary>
    protected abstract IReadOnlyList<RetainedNamespaceSecurables> RetainedRootScopePaths { get; }

    /// <summary>
    /// Resources whose only Namespace securable elements were collection-scoped, leaving them
    /// declaring none at all.
    /// </summary>
    protected abstract IReadOnlyList<string> ResourcesWithoutNamespaceSecurables { get; }

    private JsonObject _resourceSchemas = default!;
    private IReadOnlyList<(string Resource, string Path)> _namespacePaths = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _resourceSchemas = PackagedApiSchemaContract.LoadPackagedResourceSchemas(PackageRootMetadataKey);
        _namespacePaths = PackagedApiSchemaContract.NamespaceSecurablePaths(_resourceSchemas);
    }

    [Test]
    public void It_declares_no_collection_scoped_namespace_securable_elements()
    {
        PackagedApiSchemaContract
            .CollectionScopedPaths(_namespacePaths)
            .Should()
            .BeEmpty("Namespace authorization applies only to resource-root fields");
    }

    [Test]
    public void It_declares_no_extension_scoped_namespace_securable_elements()
    {
        PackagedApiSchemaContract
            .ExtensionScopedPaths(_namespacePaths)
            .Should()
            .BeEmpty("fields beneath an _ext container must not be Namespace securable elements");
    }

    [Test]
    public void It_declares_none_of_the_paths_the_generator_fix_removed()
    {
        _namespacePaths.Should().NotContain(RemovedCollectionPaths);
    }

    [Test]
    public void It_retains_the_root_scope_namespace_securable_elements()
    {
        Dictionary<string, string[]> namespacePathsByResource = PackagedApiSchemaContract.GroupByResource(
            _namespacePaths
        );

        foreach ((string resource, string[] paths) in RetainedRootScopePaths)
        {
            namespacePathsByResource
                .Should()
                .ContainKey(
                    resource,
                    "'{0}' must keep its root-scope Namespace securable elements",
                    resource
                );
            namespacePathsByResource[resource].Should().Equal(paths);
        }

        foreach (string resource in ResourcesWithoutNamespaceSecurables)
        {
            namespacePathsByResource
                .Should()
                .NotContainKey(resource, "'{0}' has no root-scope Namespace securable element", resource);
        }
    }

    [Test]
    public void It_retains_reference_metadata_for_the_removed_paths()
    {
        foreach ((string resource, string path) in RemovedCollectionPaths)
        {
            JsonNode? resourceSchema = _resourceSchemas[resource];
            resourceSchema.Should().NotBeNull($"resource '{resource}' must exist in the package");

            PackagedApiSchemaContract
                .CollectStringValues(resourceSchema!["documentPathsMapping"])
                .Should()
                .Contain(path, $"'{resource}' must keep the reference metadata for '{path}'");
        }
    }
}

/// <summary>
/// Test fixture for the Namespace securable-element scope contract of the pinned Data Standard 5.2
/// core ApiSchema package.
/// </summary>
[TestFixture]
public class Given_the_packaged_DataStandard52_core_ApiSchema : PackagedApiSchemaNamespaceSecurableContract
{
    /// <inheritdoc />
    protected override string PackageRootMetadataKey => "DataStandard52ApiSchemaPackageRoot";

    /// <inheritdoc />
    protected override IReadOnlyList<(string Resource, string Path)> RemovedCollectionPaths =>
        [
            (
                "assessmentAdministrations",
                "$.assessmentBatteryParts[*].assessmentBatteryPartReference.namespace"
            ),
            ("assessmentBatteryParts", "$.objectiveAssessments[*].objectiveAssessmentReference.namespace"),
            ("graduationPlans", "$.requiredAssessments[*].assessmentReference.namespace"),
            ("objectiveAssessments", "$.assessmentItems[*].assessmentItemReference.namespace"),
            ("studentAssessments", "$.items[*].assessmentItemReference.namespace"),
            ("studentAssessments", "$.studentObjectiveAssessments[*].objectiveAssessmentReference.namespace"),
        ];

    /// <inheritdoc />
    protected override IReadOnlyList<RetainedNamespaceSecurables> RetainedRootScopePaths =>
        [
            new("assessmentAdministrations", ["$.assessmentReference.namespace"]),
            new("assessmentBatteryParts", ["$.assessmentReference.namespace"]),
            new(
                "objectiveAssessments",
                ["$.assessmentReference.namespace", "$.parentObjectiveAssessmentReference.namespace"]
            ),
            new("studentAssessments", ["$.assessmentReference.namespace"]),
            new("assessments", ["$.namespace"]),
            new("assessmentItems", ["$.assessmentReference.namespace"]),
            new("educationContents", ["$.namespace"]),
        ];

    /// <inheritdoc />
    protected override IReadOnlyList<string> ResourcesWithoutNamespaceSecurables => ["graduationPlans"];
}

/// <summary>
/// Test fixture for the Namespace securable-element scope contract of the pinned Data Standard 6.1
/// core ApiSchema package. The 6.1 model folds TPDM into core and models parent objective
/// assessments and required certifications as collections, so it declared three collection-scoped
/// paths that 5.2 does not.
/// </summary>
[TestFixture]
public class Given_the_packaged_DataStandard61_core_ApiSchema : PackagedApiSchemaNamespaceSecurableContract
{
    /// <inheritdoc />
    protected override string PackageRootMetadataKey => "DataStandard61ApiSchemaPackageRoot";

    /// <inheritdoc />
    protected override IReadOnlyList<(string Resource, string Path)> RemovedCollectionPaths =>
        [
            (
                "assessmentAdministrations",
                "$.assessmentBatteryParts[*].assessmentBatteryPartReference.namespace"
            ),
            ("assessmentBatteryParts", "$.objectiveAssessments[*].objectiveAssessmentReference.namespace"),
            ("certifications", "$.certificationExams[*].certificationExamReference.namespace"),
            ("graduationPlans", "$.requiredAssessments[*].assessmentReference.namespace"),
            ("graduationPlans", "$.requiredCertifications[*].certificationReference.namespace"),
            ("objectiveAssessments", "$.assessmentItems[*].assessmentItemReference.namespace"),
            (
                "objectiveAssessments",
                "$.parentObjectiveAssessments[*].parentObjectiveAssessmentReference.namespace"
            ),
            ("studentAssessments", "$.items[*].assessmentItemReference.namespace"),
            ("studentAssessments", "$.studentObjectiveAssessments[*].objectiveAssessmentReference.namespace"),
        ];

    /// <inheritdoc />
    protected override IReadOnlyList<RetainedNamespaceSecurables> RetainedRootScopePaths =>
        [
            new("assessmentAdministrations", ["$.assessmentReference.namespace"]),
            new("assessmentBatteryParts", ["$.assessmentReference.namespace"]),
            new("certifications", ["$.namespace"]),
            new("objectiveAssessments", ["$.assessmentReference.namespace"]),
            new("studentAssessments", ["$.assessmentReference.namespace"]),
            new("assessments", ["$.namespace"]),
            new("assessmentItems", ["$.assessmentReference.namespace"]),
            new("educationContents", ["$.namespace"]),
        ];

    /// <inheritdoc />
    protected override IReadOnlyList<string> ResourcesWithoutNamespaceSecurables => ["graduationPlans"];
}
