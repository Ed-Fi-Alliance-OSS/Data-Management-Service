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
using static EdFi.DataManagementService.Backend.RelationalModel.Schema.RelationalModelSetSchemaHelpers;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test fixture for an authoritative core and extension effective schema set.
/// </summary>
[TestFixture]
public class Given_An_Authoritative_Core_And_Extension_EffectiveSchemaSet
{
    private string _diffOutput = default!;
    private static readonly QualifiedResourceName[] _detailedResources =
    [
        new QualifiedResourceName("Ed-Fi", "AssessmentAdministration"),
        new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation"),
    ];

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectRoot = FindProjectRoot(TestContext.CurrentContext.TestDirectory);
        var fixtureRoot = Path.Combine(projectRoot, "Fixtures", "authoritative");
        var coreInputPath = Path.Combine(
            fixtureRoot,
            "ds-5.2",
            "inputs",
            "ds-5.2-api-schema-authoritative.json"
        );
        var extensionInputPath = Path.Combine(
            fixtureRoot,
            "sample",
            "inputs",
            "sample-api-schema-authoritative.json"
        );
        var expectedPath = Path.Combine(
            fixtureRoot,
            "sample",
            "expected",
            "authoritative-derived-relational-model-set.json"
        );
        var actualPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "authoritative",
            "sample",
            "authoritative-derived-relational-model-set.json"
        );

        File.Exists(coreInputPath).Should().BeTrue($"fixture missing at {coreInputPath}");
        File.Exists(extensionInputPath).Should().BeTrue($"fixture missing at {extensionInputPath}");

        var coreSchema = LoadProjectSchema(coreInputPath);
        var extensionSchema = LoadProjectSchema(extensionInputPath);

        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(coreSchema, false);
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionSchema,
            true
        );

        var effectiveSchemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(
            new[] { coreProject, extensionProject }
        );

        var extensionSiteCapture = new ExtensionSiteCapturePass();
        IRelationalModelSetPass[] passes =
        [
            .. RelationalModelSetPasses.CreateDefault(),
            extensionSiteCapture,
        ];
        var builder = new DerivedRelationalModelSetBuilder(passes);
        var derivedSet = builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        var manifest = BuildDerivedSetManifest(derivedSet, extensionSiteCapture);

        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllText(actualPath, manifest);

        if (ShouldUpdateGoldens())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            File.WriteAllText(expectedPath, manifest);
        }

        File.Exists(expectedPath)
            .Should()
            .BeTrue($"authoritative manifest missing at {expectedPath}. Set UPDATE_GOLDENS=1 to generate.");

        _diffOutput = RunGitDiff(expectedPath, actualPath);
    }

    /// <summary>
    /// It should match the authoritative manifest.
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
    /// Build derived set manifest.
    /// </summary>
    private static string BuildDerivedSetManifest(
        DerivedRelationalModelSet modelSet,
        ExtensionSiteCapturePass extensionSiteCapture
    )
    {
        var detailedResources = new HashSet<QualifiedResourceName>(_detailedResources);
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("dialect", modelSet.Dialect.ToString());

            WriteProjects(writer, modelSet.ProjectSchemasInEndpointOrder);
            WriteResourcesSummary(writer, modelSet.ConcreteResourcesInNameOrder);
            WriteAbstractIdentityTables(writer, modelSet.AbstractIdentityTablesInNameOrder);

            writer.WritePropertyName("resource_details");
            writer.WriteStartArray();

            foreach (var resource in modelSet.ConcreteResourcesInNameOrder)
            {
                if (!detailedResources.Contains(resource.ResourceKey.Resource))
                {
                    continue;
                }

                var extensionSites = extensionSiteCapture.GetExtensionSites(resource.ResourceKey.Resource);
                WriteResourceDetails(writer, resource, extensionSites);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);

        return json + "\n";
    }

    /// <summary>
    /// Write projects.
    /// </summary>
    private static void WriteProjects(Utf8JsonWriter writer, IReadOnlyList<ProjectSchemaInfo> projects)
    {
        writer.WritePropertyName("projects");
        writer.WriteStartArray();

        foreach (var project in projects)
        {
            writer.WriteStartObject();
            writer.WriteString("project_endpoint_name", project.ProjectEndpointName);
            writer.WriteString("project_name", project.ProjectName);
            writer.WriteString("project_version", project.ProjectVersion);
            writer.WriteBoolean("is_extension", project.IsExtensionProject);
            writer.WriteString("physical_schema", project.PhysicalSchema.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Write resources summary.
    /// </summary>
    private static void WriteResourcesSummary(
        Utf8JsonWriter writer,
        IReadOnlyList<ConcreteResourceModel> resources
    )
    {
        writer.WritePropertyName("resources");
        writer.WriteStartArray();

        foreach (var resource in resources)
        {
            writer.WriteStartObject();
            writer.WriteString("project_name", resource.ResourceKey.Resource.ProjectName);
            writer.WriteString("resource_name", resource.ResourceKey.Resource.ResourceName);
            writer.WriteString("storage_kind", resource.StorageKind.ToString());
            writer.WriteString("physical_schema", resource.RelationalModel.PhysicalSchema.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Write abstract identity tables.
    /// </summary>
    private static void WriteAbstractIdentityTables(
        Utf8JsonWriter writer,
        IReadOnlyList<AbstractIdentityTableInfo> abstractIdentityTables
    )
    {
        writer.WritePropertyName("abstract_identity_tables");
        writer.WriteStartArray();

        foreach (var tableInfo in abstractIdentityTables)
        {
            writer.WriteStartObject();
            WriteResource(writer, tableInfo.AbstractResourceKey.Resource);
            writer.WritePropertyName("table");
            WriteTable(writer, tableInfo.TableModel);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Write resource details.
    /// </summary>
    private static void WriteResourceDetails(
        Utf8JsonWriter writer,
        ConcreteResourceModel resource,
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        var model = resource.RelationalModel;

        writer.WriteStartObject();
        WriteResource(writer, model.Resource);
        writer.WriteString("physical_schema", model.PhysicalSchema.Value);
        writer.WriteString("storage_kind", model.StorageKind.ToString());

        writer.WritePropertyName("tables");
        writer.WriteStartArray();
        if (model.StorageKind != ResourceStorageKind.SharedDescriptorTable)
        {
            foreach (var table in model.TablesInDependencyOrder)
            {
                WriteTable(writer, table);
            }
        }
        writer.WriteEndArray();

        writer.WritePropertyName("document_reference_bindings");
        writer.WriteStartArray();
        foreach (var binding in model.DocumentReferenceBindings)
        {
            WriteDocumentReferenceBinding(writer, binding);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("descriptor_edge_sources");
        writer.WriteStartArray();
        foreach (var edge in model.DescriptorEdgeSources)
        {
            WriteDescriptorEdge(writer, edge);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("extension_sites");
        writer.WriteStartArray();
        foreach (var site in extensionSites)
        {
            WriteExtensionSite(writer, site);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
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
    /// Write resource.
    /// </summary>
    private static void WriteResource(Utf8JsonWriter writer, QualifiedResourceName resource)
    {
        writer.WritePropertyName("resource");
        writer.WriteStartObject();
        writer.WriteString("project_name", resource.ProjectName);
        writer.WriteString("resource_name", resource.ResourceName);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Write table.
    /// </summary>
    private static void WriteTable(Utf8JsonWriter writer, DbTableModel table)
    {
        writer.WriteStartObject();
        writer.WriteString("schema", table.Table.Schema.Value);
        writer.WriteString("name", table.Table.Name);
        writer.WriteString("scope", table.JsonScope.Canonical);

        writer.WritePropertyName("key_columns");
        writer.WriteStartArray();
        foreach (var keyColumn in table.Key.Columns)
        {
            WriteKeyColumn(writer, keyColumn);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("columns");
        writer.WriteStartArray();
        foreach (var column in table.Columns)
        {
            WriteColumn(writer, column);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("constraints");
        writer.WriteStartArray();
        foreach (var constraint in table.Constraints)
        {
            WriteConstraint(writer, constraint);
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Write key column.
    /// </summary>
    private static void WriteKeyColumn(Utf8JsonWriter writer, DbKeyColumn keyColumn)
    {
        writer.WriteStartObject();
        writer.WriteString("name", keyColumn.ColumnName.Value);
        writer.WriteString("kind", keyColumn.Kind.ToString());
        writer.WriteEndObject();
    }

    /// <summary>
    /// Write column.
    /// </summary>
    private static void WriteColumn(Utf8JsonWriter writer, DbColumnModel column)
    {
        writer.WriteStartObject();
        writer.WriteString("name", column.ColumnName.Value);
        writer.WriteString("kind", column.Kind.ToString());
        writer.WritePropertyName("type");
        WriteScalarType(writer, column.ScalarType);
        writer.WriteBoolean("is_nullable", column.IsNullable);
        writer.WritePropertyName("source_path");
        if (column.SourceJsonPath is { } sourcePath)
        {
            writer.WriteStringValue(sourcePath.Canonical);
        }
        else
        {
            writer.WriteNullValue();
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Write scalar type.
    /// </summary>
    private static void WriteScalarType(Utf8JsonWriter writer, RelationalScalarType? scalarType)
    {
        if (scalarType is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("kind", scalarType.Kind.ToString());

        if (scalarType.MaxLength is not null)
        {
            writer.WriteNumber("max_length", scalarType.MaxLength.Value);
        }

        if (scalarType.Decimal is not null)
        {
            writer.WriteNumber("precision", scalarType.Decimal.Value.Precision);
            writer.WriteNumber("scale", scalarType.Decimal.Value.Scale);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Write constraint.
    /// </summary>
    private static void WriteConstraint(Utf8JsonWriter writer, TableConstraint constraint)
    {
        writer.WriteStartObject();

        switch (constraint)
        {
            case TableConstraint.Unique unique:
                writer.WriteString("kind", "Unique");
                writer.WriteString("name", unique.Name);
                writer.WritePropertyName("columns");
                WriteColumnNameList(writer, unique.Columns);
                break;
            case TableConstraint.ForeignKey foreignKey:
                writer.WriteString("kind", "ForeignKey");
                writer.WriteString("name", foreignKey.Name);
                writer.WritePropertyName("columns");
                WriteColumnNameList(writer, foreignKey.Columns);
                writer.WritePropertyName("target_table");
                WriteTableReference(writer, foreignKey.TargetTable);
                writer.WritePropertyName("target_columns");
                WriteColumnNameList(writer, foreignKey.TargetColumns);
                writer.WriteString("on_delete", foreignKey.OnDelete.ToString());
                writer.WriteString("on_update", foreignKey.OnUpdate.ToString());
                break;
            case TableConstraint.AllOrNoneNullability allOrNone:
                writer.WriteString("kind", "AllOrNoneNullability");
                writer.WriteString("name", allOrNone.Name);
                writer.WriteString("fk_column", allOrNone.FkColumn.Value);
                writer.WritePropertyName("dependent_columns");
                WriteColumnNameList(writer, allOrNone.DependentColumns);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(constraint),
                    constraint,
                    "Unknown table constraint type."
                );
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Write column name list.
    /// </summary>
    private static void WriteColumnNameList(Utf8JsonWriter writer, IReadOnlyList<DbColumnName> columns)
    {
        writer.WriteStartArray();
        foreach (var column in columns)
        {
            writer.WriteStringValue(column.Value);
        }
        writer.WriteEndArray();
    }

    /// <summary>
    /// Write document reference binding.
    /// </summary>
    private static void WriteDocumentReferenceBinding(Utf8JsonWriter writer, DocumentReferenceBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("is_identity_component", binding.IsIdentityComponent);
        writer.WriteString("reference_object_path", binding.ReferenceObjectPath.Canonical);
        writer.WritePropertyName("table");
        WriteTableReference(writer, binding.Table);
        writer.WriteString("fk_column", binding.FkColumn.Value);
        writer.WritePropertyName("target_resource");
        WriteResourceReference(writer, binding.TargetResource);
        writer.WritePropertyName("identity_bindings");
        writer.WriteStartArray();
        foreach (var identityBinding in binding.IdentityBindings)
        {
            WriteReferenceIdentityBinding(writer, identityBinding);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Write reference identity binding.
    /// </summary>
    private static void WriteReferenceIdentityBinding(
        Utf8JsonWriter writer,
        ReferenceIdentityBinding identityBinding
    )
    {
        writer.WriteStartObject();
        writer.WriteString("reference_json_path", identityBinding.ReferenceJsonPath.Canonical);
        writer.WriteString("column", identityBinding.Column.Value);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Write descriptor edge.
    /// </summary>
    private static void WriteDescriptorEdge(Utf8JsonWriter writer, DescriptorEdgeSource edge)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("is_identity_component", edge.IsIdentityComponent);
        writer.WriteString("descriptor_value_path", edge.DescriptorValuePath.Canonical);
        writer.WritePropertyName("table");
        WriteTableReference(writer, edge.Table);
        writer.WriteString("fk_column", edge.FkColumn.Value);
        writer.WritePropertyName("descriptor_resource");
        WriteResourceReference(writer, edge.DescriptorResource);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Write extension site.
    /// </summary>
    private static void WriteExtensionSite(Utf8JsonWriter writer, ExtensionSite site)
    {
        writer.WriteStartObject();
        writer.WriteString("owning_scope", site.OwningScope.Canonical);
        writer.WriteString("extension_path", site.ExtensionPath.Canonical);
        writer.WritePropertyName("project_keys");
        writer.WriteStartArray();
        foreach (var projectKey in site.ProjectKeys)
        {
            writer.WriteStringValue(projectKey);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Write table reference.
    /// </summary>
    private static void WriteTableReference(Utf8JsonWriter writer, DbTableName tableName)
    {
        writer.WriteStartObject();
        writer.WriteString("schema", tableName.Schema.Value);
        writer.WriteString("name", tableName.Name);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Write resource reference.
    /// </summary>
    private static void WriteResourceReference(Utf8JsonWriter writer, QualifiedResourceName resource)
    {
        writer.WriteStartObject();
        writer.WriteString("project_name", resource.ProjectName);
        writer.WriteString("resource_name", resource.ResourceName);
        writer.WriteEndObject();
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
}
