// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Canonicalized_Model_When_Emitting_A_Manifest
{
    private IReadOnlyList<string> _canonicalTables = default!;
    private IReadOnlyList<string> _canonicalDescriptorEdges = default!;
    private IReadOnlyList<string> _canonicalExtensionSites = default!;
    private IReadOnlyList<string> _manifestTables = default!;
    private IReadOnlyList<string> _manifestDescriptorEdges = default!;
    private IReadOnlyList<string> _manifestExtensionSites = default!;

    [SetUp]
    public void Setup()
    {
        var context = BuildContextWithUnsortedModel();

        var canonicalize = new CanonicalizeOrderingStep();
        canonicalize.Execute(context);

        var resourceModel =
            context.ResourceModel
            ?? throw new InvalidOperationException(
                "Expected ResourceModel to be set after canonicalization."
            );

        _canonicalTables = CaptureTableSnapshot(resourceModel);
        _canonicalDescriptorEdges = CaptureDescriptorEdgeSnapshot(resourceModel);
        _canonicalExtensionSites = CaptureExtensionSiteSnapshot(context.ExtensionSites);

        var manifest = RelationalModelManifestEmitter.Emit(resourceModel, context.ExtensionSites);

        var manifestRoot =
            JsonNode.Parse(manifest) as JsonObject
            ?? throw new InvalidOperationException("Expected manifest to be a JSON object.");

        _manifestTables = CaptureTableSnapshot(manifestRoot);
        _manifestDescriptorEdges = CaptureDescriptorEdgeSnapshot(manifestRoot);
        _manifestExtensionSites = CaptureExtensionSiteSnapshot(manifestRoot);
    }

    [Test]
    public void It_should_emit_manifest_in_canonical_order()
    {
        _manifestTables.Should().Equal(_canonicalTables);
        _manifestDescriptorEdges.Should().Equal(_canonicalDescriptorEdges);
        _manifestExtensionSites.Should().Equal(_canonicalExtensionSites);
    }

    private static RelationalModelBuilderContext BuildContextWithUnsortedModel()
    {
        var schema = new DbSchemaName("edfi");
        var rootTable = BuildRootTable(schema);
        var addressTable = BuildAddressTable(schema);

        var resourceModel = new RelationalResourceModel(
            new QualifiedResourceName("Ed-Fi", "School"),
            schema,
            ResourceStorageKind.RelationalTables,
            rootTable,
            new[] { addressTable, rootTable },
            new[] { addressTable, rootTable },
            Array.Empty<DocumentReferenceBinding>(),
            new[] { BuildAddressDescriptorEdge(schema), BuildRootDescriptorEdge(schema) }
        );

        var context = new RelationalModelBuilderContext { ResourceModel = resourceModel };

        context.ExtensionSites.AddRange(new[] { BuildAddressExtensionSite(), BuildRootExtensionSite() });

        return context;
    }

    private static DbTableModel BuildRootTable(DbSchemaName schema)
    {
        var tableName = new DbTableName(schema, "School");
        var jsonScope = JsonPathExpressionCompiler.Compile("$");
        var keyColumn = new DbKeyColumn(
            RelationalNameConventions.DocumentIdColumnName,
            ColumnKind.ParentKeyPart
        );

        var columns = new[]
        {
            new DbColumnModel(
                new DbColumnName("Name"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                IsNullable: false,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.name"),
                TargetResource: null
            ),
            new DbColumnModel(
                RelationalNameConventions.DocumentIdColumnName,
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                RelationalNameConventions.DescriptorIdColumnName("SchoolTypeDescriptor"),
                ColumnKind.DescriptorFk,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.schoolTypeDescriptor"),
                TargetResource: new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor")
            ),
        };

        var foreignKey = new TableConstraint.ForeignKey(
            "FK_School_Document",
            new[] { RelationalNameConventions.DocumentIdColumnName },
            new DbTableName(new DbSchemaName("dms"), "Document"),
            new[] { RelationalNameConventions.DocumentIdColumnName },
            OnDelete: ReferentialAction.Cascade
        );
        var unique = new TableConstraint.Unique("UK_School_Name", new[] { new DbColumnName("Name") });

        var constraints = new TableConstraint[] { foreignKey, unique };

        return new DbTableModel(tableName, jsonScope, new TableKey([keyColumn]), columns, constraints);
    }

    private static DbTableModel BuildAddressTable(DbSchemaName schema)
    {
        var tableName = new DbTableName(schema, "SchoolAddress");
        var jsonScope = JsonPathExpressionCompiler.Compile("$.addresses[*]");

        var documentIdColumn = RelationalNameConventions.RootDocumentIdColumnName("School");
        var keyColumns = new[]
        {
            new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart),
            new DbKeyColumn(RelationalNameConventions.OrdinalColumnName, ColumnKind.Ordinal),
        };

        var columns = new[]
        {
            new DbColumnModel(
                new DbColumnName("StreetNumberName"),
                ColumnKind.Scalar,
                new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                IsNullable: false,
                SourceJsonPath: JsonPathExpressionCompiler.Compile("$.addresses[*].streetNumberName"),
                TargetResource: null
            ),
            new DbColumnModel(
                RelationalNameConventions.OrdinalColumnName,
                ColumnKind.Ordinal,
                new RelationalScalarType(ScalarKind.Int32),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
            new DbColumnModel(
                documentIdColumn,
                ColumnKind.ParentKeyPart,
                new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                SourceJsonPath: null,
                TargetResource: null
            ),
        };

        var foreignKey = new TableConstraint.ForeignKey(
            "FK_SchoolAddress_School",
            new[] { documentIdColumn },
            new DbTableName(schema, "School"),
            new[] { RelationalNameConventions.DocumentIdColumnName },
            OnDelete: ReferentialAction.Cascade
        );
        var unique = new TableConstraint.Unique(
            "UK_SchoolAddress_Street",
            new[] { new DbColumnName("StreetNumberName") }
        );
        var constraints = new TableConstraint[] { foreignKey, unique };

        return new DbTableModel(tableName, jsonScope, new TableKey(keyColumns), columns, constraints);
    }

    private static DescriptorEdgeSource BuildRootDescriptorEdge(DbSchemaName schema)
    {
        return new DescriptorEdgeSource(
            IsIdentityComponent: false,
            DescriptorValuePath: JsonPathExpressionCompiler.Compile("$.schoolTypeDescriptor"),
            Table: new DbTableName(schema, "School"),
            FkColumn: RelationalNameConventions.DescriptorIdColumnName("SchoolTypeDescriptor"),
            DescriptorResource: new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor")
        );
    }

    private static DescriptorEdgeSource BuildAddressDescriptorEdge(DbSchemaName schema)
    {
        return new DescriptorEdgeSource(
            IsIdentityComponent: false,
            DescriptorValuePath: JsonPathExpressionCompiler.Compile("$.addresses[*].addressTypeDescriptor"),
            Table: new DbTableName(schema, "SchoolAddress"),
            FkColumn: RelationalNameConventions.DescriptorIdColumnName("AddressTypeDescriptor"),
            DescriptorResource: new QualifiedResourceName("Ed-Fi", "AddressTypeDescriptor")
        );
    }

    private static ExtensionSite BuildRootExtensionSite()
    {
        return new ExtensionSite(
            JsonPathExpressionCompiler.Compile("$"),
            JsonPathExpressionCompiler.Compile("$._ext"),
            new[] { "tpdm", "edfi" }
        );
    }

    private static ExtensionSite BuildAddressExtensionSite()
    {
        return new ExtensionSite(
            JsonPathExpressionCompiler.Compile("$.addresses[*]"),
            JsonPathExpressionCompiler.Compile("$.addresses[*]._ext"),
            new[] { "edfi", "tpdm" }
        );
    }

    private static IReadOnlyList<string> CaptureTableSnapshot(RelationalResourceModel model)
    {
        return model
            .TablesInReadDependencyOrder.Select(table =>
            {
                var columnNames = string.Join(",", table.Columns.Select(column => column.ColumnName.Value));
                var constraintNames = string.Join(",", table.Constraints.Select(GetConstraintName));

                return $"{table.Table.Schema.Value}.{table.Table.Name}|{columnNames}|{constraintNames}";
            })
            .ToArray();
    }

    private static IReadOnlyList<string> CaptureDescriptorEdgeSnapshot(RelationalResourceModel model)
    {
        return model
            .DescriptorEdgeSources.Select(edge =>
            {
                var tableName = $"{edge.Table.Schema.Value}.{edge.Table.Name}";

                return $"{tableName}|{edge.FkColumn.Value}|{edge.DescriptorValuePath.Canonical}";
            })
            .ToArray();
    }

    private static IReadOnlyList<string> CaptureExtensionSiteSnapshot(
        IReadOnlyList<ExtensionSite> extensionSites
    )
    {
        return extensionSites
            .Select(site =>
            {
                var projectKeys = string.Join("|", site.ProjectKeys);

                return $"{site.OwningScope.Canonical}|{site.ExtensionPath.Canonical}|{projectKeys}";
            })
            .ToArray();
    }

    private static IReadOnlyList<string> CaptureTableSnapshot(JsonObject manifestRoot)
    {
        var tables =
            manifestRoot["tables"] as JsonArray
            ?? throw new InvalidOperationException("Expected tables to be a JSON array.");

        return tables
            .Select(tableNode =>
            {
                if (tableNode is not JsonObject table)
                {
                    throw new InvalidOperationException("Expected table entry to be a JSON object.");
                }

                var schema =
                    table["schema"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Expected table schema.");
                var name =
                    table["name"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Expected table name.");

                var columns =
                    table["columns"] as JsonArray
                    ?? throw new InvalidOperationException("Expected table columns array.");
                var columnNames = string.Join(
                    ",",
                    columns.Select(column => column?["name"]?.GetValue<string>())
                );

                var constraints =
                    table["constraints"] as JsonArray
                    ?? throw new InvalidOperationException("Expected table constraints array.");
                var constraintNames = string.Join(
                    ",",
                    constraints.Select(constraint => constraint?["name"]?.GetValue<string>())
                );

                return $"{schema}.{name}|{columnNames}|{constraintNames}";
            })
            .ToArray();
    }

    private static IReadOnlyList<string> CaptureDescriptorEdgeSnapshot(JsonObject manifestRoot)
    {
        var edges =
            manifestRoot["descriptor_edge_sources"] as JsonArray
            ?? throw new InvalidOperationException("Expected descriptor edges to be a JSON array.");

        return edges
            .Select(edgeNode =>
            {
                if (edgeNode is not JsonObject edge)
                {
                    throw new InvalidOperationException("Expected descriptor edge to be a JSON object.");
                }

                var table =
                    edge["table"] as JsonObject
                    ?? throw new InvalidOperationException("Expected descriptor edge table.");
                var schema =
                    table["schema"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Expected descriptor edge table schema.");
                var name =
                    table["name"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Expected descriptor edge table name.");

                var fkColumn =
                    edge["fk_column"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Expected descriptor edge fk_column.");
                var descriptorPath =
                    edge["descriptor_value_path"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Expected descriptor edge descriptor_value_path.");

                return $"{schema}.{name}|{fkColumn}|{descriptorPath}";
            })
            .ToArray();
    }

    private static IReadOnlyList<string> CaptureExtensionSiteSnapshot(JsonObject manifestRoot)
    {
        var sites =
            manifestRoot["extension_sites"] as JsonArray
            ?? throw new InvalidOperationException("Expected extension sites to be a JSON array.");

        return sites
            .Select(siteNode =>
            {
                if (siteNode is not JsonObject site)
                {
                    throw new InvalidOperationException("Expected extension site to be a JSON object.");
                }

                var owningScope =
                    site["owning_scope"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Expected extension site owning_scope.");
                var extensionPath =
                    site["extension_path"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("Expected extension site extension_path.");

                var projectKeys =
                    site["project_keys"] as JsonArray
                    ?? throw new InvalidOperationException("Expected extension site project_keys.");
                var projectKeyList = string.Join(
                    "|",
                    projectKeys.Select(projectKey => projectKey?.GetValue<string>())
                );

                return $"{owningScope}|{extensionPath}|{projectKeyList}";
            })
            .ToArray();
    }

    private static string GetConstraintName(TableConstraint constraint)
    {
        return constraint switch
        {
            TableConstraint.Unique unique => unique.Name,
            TableConstraint.ForeignKey foreignKey => foreignKey.Name,
            _ => string.Empty,
        };
    }
}
