// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.RelationalModel.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for an authoritative api schema derived relational model set.
/// </summary>
[TestFixture]
public class Given_An_Authoritative_ApiSchema_For_Derived_Relational_Model_Set
{
    private string _diffOutput = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectRoot = FindProjectRoot(TestContext.CurrentContext.TestDirectory);
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "authoritative", "ds-5.2");
        var inputPath = Path.Combine(fixtureRoot, "inputs", "ds-5.2-api-schema-authoritative.json");
        var expectedPath = Path.Combine(
            fixtureRoot,
            "expected",
            "authoritative-relational-model.derived.manifest.json"
        );
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "authoritative",
            "ds-5.2",
            "authoritative-relational-model.derived.manifest.json"
        );

        File.Exists(inputPath).Should().BeTrue($"fixture missing at {inputPath}");

        var projectSchema = LoadProjectSchema(inputPath);
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(projectSchema, false);
        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(new[] { project });

        var endpointMappings = BuildResourceEndpointMappings(projectSchema);

        var extensionSiteCapture = new ExtensionSiteCapturePass();
        IRelationalModelSetPass[] passes =
        [
            .. RelationalModelSetPasses.CreateDefault(),
            extensionSiteCapture,
        ];
        var builder = new DerivedRelationalModelSetBuilder(passes);
        var derivedSet = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());

        var manifest = BuildProjectManifest(derivedSet, extensionSiteCapture, endpointMappings);

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, manifest);

        if (ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue(
                $"authoritative derived manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate."
            );

        _diffOutput = RunGitDiff(expectedPath, actualPath);
    }

    /// <summary>
    /// It should match the authoritative derived manifest.
    /// </summary>
    [Test]
    public void It_should_match_the_authoritative_manifest()
    {
        if (!string.IsNullOrWhiteSpace(_diffOutput))
        {
            Assert.Fail(_diffOutput);
        }
    }

    /// <summary>
    /// Load project schema.
    /// </summary>
    private static JsonObject LoadProjectSchema(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path));

        if (root is not JsonObject rootObject)
        {
            throw new InvalidOperationException($"ApiSchema parsed null or non-object: {path}");
        }

        return RequireObject(rootObject["projectSchema"], "projectSchema");
    }

    /// <summary>
    /// Build resource endpoint mappings.
    /// </summary>
    private static ResourceEndpointMappings BuildResourceEndpointMappings(JsonObject projectSchema)
    {
        ArgumentNullException.ThrowIfNull(projectSchema);

        var projectName = RequireString(projectSchema, "projectName");
        var resourceSchemas = RequireObject(
            projectSchema["resourceSchemas"],
            "projectSchema.resourceSchemas"
        );

        Dictionary<QualifiedResourceName, string> endpointNamesByResource = new();
        Dictionary<string, QualifiedResourceName> resourcesByEndpointName = new(StringComparer.Ordinal);

        foreach (var resourceSchemaEntry in resourceSchemas)
        {
            if (resourceSchemaEntry.Value is null)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema.resourceSchemas entries to be non-null, invalid ApiSchema."
                );
            }

            if (resourceSchemaEntry.Value is not JsonObject resourceSchema)
            {
                throw new InvalidOperationException(
                    "Expected projectSchema.resourceSchemas entries to be objects, invalid ApiSchema."
                );
            }

            if (string.IsNullOrWhiteSpace(resourceSchemaEntry.Key))
            {
                throw new InvalidOperationException(
                    "Expected resource schema entry key to be non-empty, invalid ApiSchema."
                );
            }

            var resourceName = GetResourceName(resourceSchemaEntry.Key, resourceSchema);
            var resource = new QualifiedResourceName(projectName, resourceName);

            if (!endpointNamesByResource.TryAdd(resource, resourceSchemaEntry.Key))
            {
                throw new InvalidOperationException(
                    $"Duplicate resource detected for endpoint '{resourceSchemaEntry.Key}' ({FormatResource(resource)})."
                );
            }

            if (!resourcesByEndpointName.TryAdd(resourceSchemaEntry.Key, resource))
            {
                throw new InvalidOperationException(
                    $"Duplicate resource endpoint name detected: '{resourceSchemaEntry.Key}'."
                );
            }
        }

        return new ResourceEndpointMappings(endpointNamesByResource, resourcesByEndpointName);
    }

    /// <summary>
    /// Build project manifest.
    /// </summary>
    private static string BuildProjectManifest(
        DerivedRelationalModelSet modelSet,
        ExtensionSiteCapturePass extensionSiteCapture,
        ResourceEndpointMappings endpointMappings
    )
    {
        ArgumentNullException.ThrowIfNull(modelSet);
        ArgumentNullException.ThrowIfNull(extensionSiteCapture);
        ArgumentNullException.ThrowIfNull(endpointMappings);

        if (modelSet.ProjectSchemasInEndpointOrder.Count != 1)
        {
            throw new InvalidOperationException(
                "Expected a single project schema for the authoritative ds-5.2 derived manifest."
            );
        }

        var project = modelSet.ProjectSchemasInEndpointOrder[0];
        var resourcesByName = modelSet.ConcreteResourcesInNameOrder.ToDictionary(resource =>
            resource.ResourceKey.Resource
        );

        if (resourcesByName.Count != endpointMappings.ResourcesByEndpointName.Count)
        {
            throw new InvalidOperationException(
                "Derived resource count does not match the project schema resource count."
            );
        }

        var orderedEndpointNames = endpointMappings
            .ResourcesByEndpointName.Keys.OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("project_name", project.ProjectName);
            writer.WriteString("project_endpoint_name", project.ProjectEndpointName);
            writer.WriteString("project_version", project.ProjectVersion);
            writer.WritePropertyName("resources");
            writer.WriteStartArray();

            foreach (var endpointName in orderedEndpointNames)
            {
                var resource = endpointMappings.ResourcesByEndpointName[endpointName];

                if (!resourcesByName.TryGetValue(resource, out var model))
                {
                    throw new InvalidOperationException(
                        $"Derived resource not found for endpoint '{endpointName}' ({FormatResource(resource)})."
                    );
                }

                var extensionSites = extensionSiteCapture.GetExtensionSites(resource);
                var manifest = RelationalModelManifestEmitter.Emit(model.RelationalModel, extensionSites);

                using var manifestDocument = JsonDocument.Parse(manifest);
                writer.WriteStartObject();
                writer.WriteString("resource_endpoint_name", endpointName);
                writer.WritePropertyName("manifest");
                manifestDocument.RootElement.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);

        return json + "\n";
    }

    /// <summary>
    /// Run git diff.
    /// </summary>
    private static string RunGitDiff(string expectedPath, string actualPath)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add("--no-index");
        startInfo.ArgumentList.Add("--ignore-space-at-eol");
        startInfo.ArgumentList.Add("--ignore-cr-at-eol");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(expectedPath);
        startInfo.ArgumentList.Add(actualPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode == 0)
        {
            return string.Empty;
        }

        if (process.ExitCode == 1)
        {
            return output;
        }

        return string.IsNullOrWhiteSpace(error) ? output : $"{error}\n{output}".Trim();
    }

    /// <summary>
    /// Should update goldens.
    /// </summary>
    private static bool ShouldUpdateGoldens()
    {
        var update = Environment.GetEnvironmentVariable("UPDATE_GOLDENS");

        return string.Equals(update, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(update, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Find project root.
    /// </summary>
    private static string FindProjectRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj"
            );
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj in parent directories."
        );
    }

    /// <summary>
    /// Test type extension site capture pass.
    /// </summary>
    private sealed class ExtensionSiteCapturePass : IRelationalModelSetPass
    {
        private readonly Dictionary<QualifiedResourceName, IReadOnlyList<ExtensionSite>> _sitesByResource =
            new();

        /// <summary>
        /// Execute.
        /// </summary>
        public void Execute(RelationalModelSetBuilderContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            foreach (var resource in context.ConcreteResourcesInNameOrder)
            {
                _sitesByResource[resource.ResourceKey.Resource] = context.GetExtensionSitesForResource(
                    resource.ResourceKey.Resource
                );
            }
        }

        /// <summary>
        /// Get extension sites.
        /// </summary>
        public IReadOnlyList<ExtensionSite> GetExtensionSites(QualifiedResourceName resource)
        {
            return _sitesByResource.TryGetValue(resource, out var sites)
                ? sites
                : Array.Empty<ExtensionSite>();
        }
    }

    /// <summary>
    /// Test type resource endpoint mappings.
    /// </summary>
    private sealed record ResourceEndpointMappings(
        IReadOnlyDictionary<QualifiedResourceName, string> EndpointNamesByResource,
        IReadOnlyDictionary<string, QualifiedResourceName> ResourcesByEndpointName
    );
}
