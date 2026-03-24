// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Naming;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Startup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

internal static class FocusedStableKeyFixtureEffectiveSchemaSetLoader
{
    private const string DdlTestsProjectFileName = "EdFi.DataManagementService.Backend.Ddl.Tests.Unit.csproj";
    private const string RelationalModelTestsProjectDirectoryName =
        "EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit";
    private const string InputsDirectoryName = "inputs";
    private const string InputsPropertyName = "inputs";

    public static EffectiveSchemaSet Load(string fixtureRelativePath)
    {
        var ddlTestsProjectRoot = GoldenFixtureTestHelpers.FindProjectRoot(
            TestContext.CurrentContext.TestDirectory,
            DdlTestsProjectFileName
        );
        var fixturePath = Path.GetFullPath(
            Path.Combine(
                ddlTestsProjectRoot,
                "..",
                RelationalModelTestsProjectDirectoryName,
                fixtureRelativePath
            )
        );
        var fixtureRoot = ParseJsonObject(fixturePath, "Fixture manifest");
        var inputEntries = RequireArray(fixtureRoot[InputsPropertyName], InputsPropertyName);

        JsonObject? coreNode = null;
        List<JsonObject> extensionNodes = [];

        foreach (var inputEntry in inputEntries)
        {
            var inputObject = RequireObject(inputEntry, "inputs entry");
            var fileName = RequireString(inputObject, "fileName");
            var isExtensionProject = inputObject["isExtensionProject"]?.GetValue<bool>() ?? false;
            var inputPath = Path.Combine(
                Path.GetDirectoryName(fixturePath)
                    ?? throw new InvalidOperationException(
                        $"Unable to resolve fixture directory for '{fixturePath}'."
                    ),
                InputsDirectoryName,
                fileName
            );
            var inputRoot = ParseJsonObject(inputPath, "Fixture input");
            AssignExtensionFlag(inputRoot, isExtensionProject);

            if (isExtensionProject)
            {
                extensionNodes.Add(inputRoot);
                continue;
            }

            if (coreNode is not null)
            {
                throw new InvalidOperationException(
                    $"Fixture '{fixturePath}' declares more than one core schema input."
                );
            }

            coreNode = inputRoot;
        }

        if (coreNode is null)
        {
            throw new InvalidOperationException(
                $"Fixture '{fixturePath}' does not declare a core schema input."
            );
        }

        var rawNodes = new ApiSchemaDocumentNodes(coreNode, [.. extensionNodes]);
        var normalizer = new ApiSchemaInputNormalizer(NullLogger<ApiSchemaInputNormalizer>.Instance);
        var normalizationResult = normalizer.Normalize(rawNodes);
        var normalizedNodes = normalizationResult is ApiSchemaNormalizationResult.SuccessResult success
            ? success.NormalizedNodes
            : throw new InvalidOperationException(
                $"ApiSchema normalization failed for focused stable-key fixture '{fixturePath}'."
            );

        var builder = new EffectiveSchemaSetBuilder(
            new EffectiveSchemaHashProvider(NullLogger<EffectiveSchemaHashProvider>.Instance),
            new ResourceKeySeedProvider(NullLogger<ResourceKeySeedProvider>.Instance)
        );

        return builder.Build(normalizedNodes);
    }

    private static void AssignExtensionFlag(JsonObject inputRoot, bool isExtensionProject)
    {
        var projectSchema =
            inputRoot["projectSchema"] as JsonObject
            ?? throw new InvalidOperationException("Fixture input is missing 'projectSchema'.");

        projectSchema["abstractResources"] ??= new JsonObject();
        projectSchema["resourceSchemas"] ??= new JsonObject();
        projectSchema["isExtensionProject"] = isExtensionProject;
    }

    private static JsonArray RequireArray(JsonNode? node, string path)
    {
        return node as JsonArray
            ?? throw new InvalidOperationException($"Expected '{path}' to be a JSON array.");
    }

    private static JsonObject RequireObject(JsonNode? node, string path)
    {
        return node as JsonObject
            ?? throw new InvalidOperationException($"Expected '{path}' to be a JSON object.");
    }

    private static string RequireString(JsonObject node, string propertyName)
    {
        return node[propertyName]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Expected '{propertyName}' to be a string property.");
    }

    private static JsonObject ParseJsonObject(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{description} not found: {path}", path);
        }

        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidOperationException($"{description} '{path}' parsed null or non-object.");
    }
}

internal static class StableCollectionIdentityDdlTestHelper
{
    public static DbIndexInfo RequireForeignKeySupportIndex(
        DerivedRelationalModelSet modelSet,
        string schema,
        string tableName,
        params string[] columns
    )
    {
        return modelSet.IndexesInCreateOrder.Single(index =>
            index.Kind == DbIndexKind.ForeignKeySupport
            && index.Table.Schema.Value.Equals(schema, StringComparison.Ordinal)
            && index.Table.Name.Equals(tableName, StringComparison.Ordinal)
            && index.KeyColumns.Select(static column => column.Value).SequenceEqual(columns)
        );
    }

    public static TableConstraint.ForeignKey RequireForeignKeyConstraint(
        DerivedRelationalModelSet modelSet,
        string schema,
        string tableName,
        params string[] columns
    )
    {
        return modelSet
            .ConcreteResourcesInNameOrder.SelectMany(resource =>
                resource.RelationalModel.TablesInDependencyOrder
            )
            .Concat(modelSet.AbstractIdentityTablesInNameOrder.Select(tableInfo => tableInfo.TableModel))
            .Single(table =>
                table.Table.Schema.Value.Equals(schema, StringComparison.Ordinal)
                && table.Table.Name.Equals(tableName, StringComparison.Ordinal)
            )
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(foreignKey =>
                foreignKey.Columns.Select(static column => column.Value).SequenceEqual(columns)
            );
    }

    public static DbTableModel RequireTableByScope(
        DerivedRelationalModelSet modelSet,
        string scope,
        string schema,
        string tableName
    )
    {
        return modelSet
            .ConcreteResourcesInNameOrder.SelectMany(resource =>
                resource.RelationalModel.TablesInDependencyOrder
            )
            .DistinctBy(table =>
                (Scope: table.JsonScope.Canonical, Schema: table.Table.Schema.Value, table.Table.Name)
            )
            .Single(table =>
                table.JsonScope.Canonical == scope
                && table.Table.Schema.Value.Equals(schema, StringComparison.Ordinal)
                && table.Table.Name.Equals(tableName, StringComparison.Ordinal)
            );
    }

    public static string ExtractCreateTableBlock(string ddl, DbTableName table, SqlDialect dialect)
    {
        var header = dialect switch
        {
            SqlDialect.Pgsql => $"CREATE TABLE IF NOT EXISTS \"{table.Schema.Value}\".\"{table.Name}\"",
            SqlDialect.Mssql => $"CREATE TABLE [{table.Schema.Value}].[{table.Name}]",
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported dialect."),
        };
        var startIndex = ddl.IndexOf(header, StringComparison.Ordinal);

        if (startIndex < 0)
        {
            throw new InvalidOperationException($"Could not locate CREATE TABLE block for '{table}'.");
        }

        var endIndex = ddl.IndexOf(");", startIndex, StringComparison.Ordinal);

        if (endIndex < 0)
        {
            throw new InvalidOperationException(
                $"Could not find the end of CREATE TABLE block for '{table}'."
            );
        }

        return ddl[startIndex..(endIndex + 2)];
    }
}

[TestFixture]
public class Given_DdlPipelineHelpers_With_Focused_Stable_Key_Fixture
{
    private const string FixturePath =
        "Fixtures/focused-stable-key/positive/extension-child-collections/fixture.manifest.json";
    private static readonly StableKeyForeignKeyIndexExpectation[] _stableKeyForeignKeyIndexExpectations =
    [
        new("edfi", "SchoolAddressPeriod", ["ParentCollectionItemId", "School_DocumentId"]),
        new(
            "sample",
            "SchoolExtensionAddressSponsorReference",
            ["BaseCollectionItemId", "School_DocumentId"]
        ),
        new(
            "sample",
            "SchoolExtensionAddressSponsorReference",
            ["Program_DocumentId", "Program_ProgramName"]
        ),
        new("sample", "SchoolExtensionInterventionVisit", ["ParentCollectionItemId", "School_DocumentId"]),
    ];
    private static readonly string[] _expectedStableKeyFkSupportIndexSignatures =
    [
        "edfi.SchoolAddressPeriod|ParentCollectionItemId|School_DocumentId",
        "sample.SchoolExtensionAddressSponsorReference|BaseCollectionItemId|School_DocumentId",
        "sample.SchoolExtensionAddressSponsorReference|Program_DocumentId|Program_ProgramName",
        "sample.SchoolExtensionInterventionVisit|ParentCollectionItemId|School_DocumentId",
    ];

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_apply_collection_item_sequence_defaults_only_to_collection_identity_columns(
        SqlDialect dialect
    )
    {
        var effectiveSchemaSet = FocusedStableKeyFixtureEffectiveSchemaSetLoader.Load(FixturePath);
        var (modelSet, combinedSql) = DdlPipelineHelpers.BuildDdlForDialect(effectiveSchemaSet, dialect);
        var sqlDialect = SqlDialectFactory.Create(dialect);
        var defaultExpression = sqlDialect.RenderSequenceDefaultExpression(
            DmsTableNames.DmsSchema,
            DmsTableNames.CollectionItemIdSequence
        );
        var collectionItemColumnDefinition = sqlDialect.RenderColumnDefinition(
            RelationalNameConventions.CollectionItemIdColumnName,
            "bigint",
            false,
            defaultExpression
        );
        var parentCollectionItemColumnDefinition = sqlDialect.RenderColumnDefinition(
            RelationalNameConventions.ParentCollectionItemIdColumnName,
            "bigint",
            false
        );
        var baseCollectionItemColumnDefinition = sqlDialect.RenderColumnDefinition(
            RelationalNameConventions.BaseCollectionItemIdColumnName,
            "bigint",
            false
        );
        var parentCollectionItemColumnDefinitionWithDefault = sqlDialect.RenderColumnDefinition(
            RelationalNameConventions.ParentCollectionItemIdColumnName,
            "bigint",
            false,
            defaultExpression
        );
        var baseCollectionItemColumnDefinitionWithDefault = sqlDialect.RenderColumnDefinition(
            RelationalNameConventions.BaseCollectionItemIdColumnName,
            "bigint",
            false,
            defaultExpression
        );

        var rootTableDdl = TableDdlForScope(modelSet, combinedSql, "$", "edfi", "School", dialect);
        var addressTableDdl = TableDdlForScope(
            modelSet,
            combinedSql,
            "$.addresses[*]",
            "edfi",
            "SchoolAddress",
            dialect
        );
        var addressPeriodTableDdl = TableDdlForScope(
            modelSet,
            combinedSql,
            "$.addresses[*].periods[*]",
            "edfi",
            "SchoolAddressPeriod",
            dialect
        );
        var alignedExtensionScopeTableDdl = TableDdlForScope(
            modelSet,
            combinedSql,
            "$._ext.sample.addresses[*]._ext.sample",
            "sample",
            "SchoolExtensionAddress",
            dialect
        );
        var interventionTableDdl = TableDdlForScope(
            modelSet,
            combinedSql,
            "$._ext.sample.interventions[*]",
            "sample",
            "SchoolExtensionIntervention",
            dialect
        );
        var visitTableDdl = TableDdlForScope(
            modelSet,
            combinedSql,
            "$._ext.sample.interventions[*].visits[*]",
            "sample",
            "SchoolExtensionInterventionVisit",
            dialect
        );
        var sponsorReferenceTableDdl = TableDdlForScope(
            modelSet,
            combinedSql,
            "$._ext.sample.addresses[*]._ext.sample.sponsorReferences[*]",
            "sample",
            "SchoolExtensionAddressSponsorReference",
            dialect
        );

        addressTableDdl.Should().Contain(collectionItemColumnDefinition);
        addressPeriodTableDdl.Should().Contain(collectionItemColumnDefinition);
        interventionTableDdl.Should().Contain(collectionItemColumnDefinition);
        visitTableDdl.Should().Contain(collectionItemColumnDefinition);
        sponsorReferenceTableDdl.Should().Contain(collectionItemColumnDefinition);

        addressPeriodTableDdl.Should().Contain(parentCollectionItemColumnDefinition);
        visitTableDdl.Should().Contain(parentCollectionItemColumnDefinition);
        alignedExtensionScopeTableDdl.Should().Contain(baseCollectionItemColumnDefinition);
        sponsorReferenceTableDdl.Should().Contain(baseCollectionItemColumnDefinition);

        rootTableDdl.Should().NotContain(defaultExpression);
        alignedExtensionScopeTableDdl.Should().NotContain(defaultExpression);
        addressPeriodTableDdl.Should().NotContain(parentCollectionItemColumnDefinitionWithDefault);
        visitTableDdl.Should().NotContain(parentCollectionItemColumnDefinitionWithDefault);
        alignedExtensionScopeTableDdl.Should().NotContain(baseCollectionItemColumnDefinitionWithDefault);
        sponsorReferenceTableDdl.Should().NotContain(baseCollectionItemColumnDefinitionWithDefault);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_emit_stable_key_FK_support_indexes_once_in_canonical_order_after_foreign_keys(
        SqlDialect dialect
    )
    {
        var effectiveSchemaSet = FocusedStableKeyFixtureEffectiveSchemaSetLoader.Load(FixturePath);
        var (modelSet, combinedSql) = DdlPipelineHelpers.BuildDdlForDialect(effectiveSchemaSet, dialect);

        var stableKeyFkSupportIndexes = modelSet
            .IndexesInCreateOrder.Where(index =>
                index.Kind == DbIndexKind.ForeignKeySupport
                && index.Table.Name
                    is "SchoolAddressPeriod"
                        or "SchoolExtensionAddress"
                        or "SchoolExtensionAddressSponsorReference"
                        or "SchoolExtensionIntervention"
                        or "SchoolExtensionInterventionVisit"
            )
            .ToArray();

        stableKeyFkSupportIndexes
            .Select(static index =>
                $"{index.Table.Schema.Value}.{index.Table.Name}|"
                + $"{string.Join("|", index.KeyColumns.Select(static column => column.Value))}"
            )
            .Should()
            .Equal(_expectedStableKeyFkSupportIndexSignatures);

        stableKeyFkSupportIndexes
            .GroupBy(
                static index =>
                    $"{index.Table.Schema.Value}.{index.Table.Name}|"
                    + $"{string.Join("|", index.KeyColumns.Select(static column => column.Value))}",
                StringComparer.Ordinal
            )
            .Should()
            .OnlyContain(static group => group.Count() == 1);

        var indexPositions = new List<int>(_stableKeyForeignKeyIndexExpectations.Length);

        foreach (var expectation in _stableKeyForeignKeyIndexExpectations)
        {
            var foreignKey = StableCollectionIdentityDdlTestHelper.RequireForeignKeyConstraint(
                modelSet,
                expectation.Schema,
                expectation.TableName,
                expectation.Columns
            );
            var index = StableCollectionIdentityDdlTestHelper.RequireForeignKeySupportIndex(
                modelSet,
                expectation.Schema,
                expectation.TableName,
                expectation.Columns
            );
            var foreignKeyPosition = combinedSql.IndexOf(foreignKey.Name, StringComparison.Ordinal);
            var indexPosition = combinedSql.IndexOf(index.Name.Value, StringComparison.Ordinal);

            foreignKeyPosition.Should().BeGreaterThanOrEqualTo(0);
            indexPosition.Should().BeGreaterThan(foreignKeyPosition);
            indexPositions.Add(indexPosition);
        }

        indexPositions.Should().BeInAscendingOrder();
    }

    private static string TableDdlForScope(
        DerivedRelationalModelSet modelSet,
        string ddl,
        string scope,
        string schema,
        string tableName,
        SqlDialect dialect
    )
    {
        var table = StableCollectionIdentityDdlTestHelper.RequireTableByScope(
            modelSet,
            scope,
            schema,
            tableName
        );
        return StableCollectionIdentityDdlTestHelper.ExtractCreateTableBlock(ddl, table.Table, dialect);
    }
}

internal readonly record struct StableKeyForeignKeyIndexExpectation(
    string Schema,
    string TableName,
    string[] Columns
);
