// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel.Manifest;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// An independent oracle for the document/resource invariant on every abstract identity table of the
/// authoritative fixtures. It derives its expectations from the effective ApiSchema and the effective
/// schema's resource keys, never from the regenerated goldens, so a careless <c>UPDATE_GOLDENS</c> run
/// cannot hide a regression.
/// </summary>
[TestFixture("ds-5.2", SqlDialect.Pgsql)]
[TestFixture("ds-5.2", SqlDialect.Mssql)]
[TestFixture("ds-5.2-tpdm", SqlDialect.Pgsql)]
[TestFixture("ds-5.2-tpdm", SqlDialect.Mssql)]
[TestFixture("sample", SqlDialect.Pgsql)]
[TestFixture("sample", SqlDialect.Mssql)]
[Category("Authoritative")]
public class Given_Authoritative_Abstract_Identity_Document_Resource_Invariant(
    string fixtureName,
    SqlDialect dialect
)
{
    private const string DocumentIdColumn = "DocumentId";
    private const string ResourceKeyIdColumn = "ResourceKeyId";

    private AbstractIdentityDocumentResourceInvariantBuild _build = default!;
    private IReadOnlyList<AbstractIdentityTableInfo> _abstractTables = default!;

    [SetUp]
    public void Setup()
    {
        _build = AbstractIdentityDocumentResourceInvariantBuild.Get(fixtureName, dialect);
        _abstractTables = _build.ModelSet.AbstractIdentityTablesInNameOrder;
        _abstractTables.Should().NotBeEmpty("every authoritative fixture derives abstract identity tables");
    }

    [Test]
    public void It_pins_the_natural_key_to_the_identityJsonPaths_order()
    {
        foreach (var abstractTable in _abstractTables)
        {
            var naturalKey = GetUnique(abstractTable, "_NK");

            SourcePathsOf(abstractTable, naturalKey.Columns)
                .Should()
                .Equal(
                    IdentityJsonPathsOf(abstractTable.AbstractResourceKey.Resource),
                    $"{naturalKey.Name} must cover exactly the abstract identity fields, in identityJsonPaths order"
                );
        }
    }

    [Test]
    public void It_pins_the_reference_key_to_the_same_fields_plus_trailing_DocumentId()
    {
        foreach (var abstractTable in _abstractTables)
        {
            var naturalKey = GetUnique(abstractTable, "_NK");
            var referenceKey = GetUnique(abstractTable, "_RefKey");

            referenceKey
                .Columns.Select(column => column.Value)
                .Should()
                .Equal(
                    [.. naturalKey.Columns.Select(column => column.Value), DocumentIdColumn],
                    referenceKey.Name
                );
        }
    }

    [Test]
    public void It_has_one_document_foreign_key_on_DocumentId_and_ResourceKeyId()
    {
        foreach (var abstractTable in _abstractTables)
        {
            var table = abstractTable.TableModel;
            var documentForeignKey = table
                .Constraints.OfType<TableConstraint.ForeignKey>()
                .Where(foreignKey => IsDocumentTable(foreignKey.TargetTable))
                .Should()
                .ContainSingle(table.Table.Name)
                .Subject;

            documentForeignKey.Name.Should().Be($"FK_{table.Table.Name}_DocumentResourceKey");
            documentForeignKey
                .Columns.Select(column => column.Value)
                .Should()
                .Equal(DocumentIdColumn, ResourceKeyIdColumn);
            documentForeignKey
                .TargetColumns.Select(column => column.Value)
                .Should()
                .Equal(DocumentIdColumn, ResourceKeyIdColumn);
            documentForeignKey.OnDelete.Should().Be(ReferentialAction.Cascade);
            documentForeignKey.OnUpdate.Should().Be(ReferentialAction.NoAction);

            var resourceKeyId = table.Columns.Single(column =>
                column.ColumnName.Value == ResourceKeyIdColumn
            );
            resourceKeyId.ScalarType.Should().Be(new RelationalScalarType(ScalarKind.Int16));
            resourceKeyId.IsNullable.Should().BeFalse();
            resourceKeyId.SourceJsonPath.Should().BeNull();
        }
    }

    [Test]
    public void It_has_no_abstract_identity_foreign_key_named_Identity_Document()
    {
        foreach (var abstractTable in _abstractTables)
        {
            var tableName = abstractTable.TableModel.Table.Name;
            var oldForeignKey = new Regex($"FK_{Regex.Escape(tableName)}_Document(?![A-Za-z])");

            abstractTable
                .TableModel.Constraints.OfType<TableConstraint.ForeignKey>()
                .Select(foreignKey => foreignKey.Name)
                .Should()
                .NotContain(name => name.EndsWith("Identity_Document", StringComparison.Ordinal));
            _build.RelationalModelManifest.Should().NotMatchRegex(oldForeignKey.ToString());
            _build.RelationalModelManifest.Should().Contain($"FK_{tableName}_DocumentResourceKey");
            _build.Sql.Should().NotMatchRegex(oldForeignKey.ToString());
            _build.Sql.Should().Contain($"FK_{tableName}_DocumentResourceKey");
        }
    }

    [Test]
    public void It_takes_every_ResourceKeyId_literal_from_compile_time_member_metadata()
    {
        var abstractTableNames = _abstractTables.Select(table => table.TableModel.Table).ToHashSet();
        var triggers = _build
            .ModelSet.TriggersInCreateOrder.Where(trigger =>
                trigger.Parameters is TriggerKindParameters.AbstractIdentityMaintenance
            )
            .ToArray();

        triggers.Should().NotBeEmpty();

        foreach (var trigger in triggers)
        {
            var parameters = (TriggerKindParameters.AbstractIdentityMaintenance)trigger.Parameters;
            var member = ConcreteResourceOwning(trigger.Table);
            var discriminator = $"{member.ProjectName}:{member.ResourceName}";

            abstractTableNames.Should().Contain(parameters.TargetTable);
            parameters.ResourceKeyIdValue.Should().Be(ResourceKeyIdOf(member), trigger.Name.Value);
            parameters.DiscriminatorValue.Should().Be(discriminator);
            _build.Sql.Should().Contain(TriggerValueFragment(ResourceKeyIdOf(member), discriminator));
        }

        foreach (var view in _build.ModelSet.AbstractUnionViewsInNameOrder)
        {
            var resourceKeyIdIndex = view
                .OutputColumnsInSelectOrder.Select(column => column.ColumnName.Value)
                .ToList()
                .IndexOf(ResourceKeyIdColumn);

            resourceKeyIdIndex.Should().BeGreaterThan(0, view.ViewName.Name);

            foreach (var arm in view.UnionArmsInOrder)
            {
                var member = arm.ConcreteMemberResourceKey.Resource;
                var discriminator = $"{member.ProjectName}:{member.ResourceName}";

                arm.ProjectionExpressionsInSelectOrder[resourceKeyIdIndex]
                    .Should()
                    .Be(new AbstractUnionViewProjectionExpression.Int16Literal(ResourceKeyIdOf(member)));
                _build.Sql.Should().MatchRegex(ViewArmFragment(ResourceKeyIdOf(member), discriminator));
            }
        }
    }

    [Test]
    public void It_derives_no_helper_index_for_the_document_resource_foreign_key()
    {
        foreach (var abstractTable in _abstractTables)
        {
            var table = abstractTable.TableModel.Table;

            _build
                .ModelSet.IndexesInCreateOrder.Where(index => index.Table.Equals(table))
                .Select(index => index.KeyColumns.Select(column => column.Value).ToArray())
                .Should()
                .NotContain(
                    keyColumns => keyColumns.SequenceEqual(new[] { DocumentIdColumn, ResourceKeyIdColumn }),
                    table.Name
                );
            _build.Sql.Should().NotContain($"IX_{table.Name}_DocumentId_ResourceKeyId");
        }
    }

    [Test]
    public void It_emits_the_document_resource_candidate_key()
    {
        var expected =
            dialect == SqlDialect.Pgsql
                ? @"ALTER TABLE ""dms"".""Document""\s+ADD CONSTRAINT ""UX_Document_DocumentId_ResourceKeyId"" UNIQUE \(""DocumentId"", ""ResourceKeyId""\);"
                : @"ALTER TABLE \[dms\]\.\[Document\]\s+ADD CONSTRAINT \[UX_Document_DocumentId_ResourceKeyId\] UNIQUE \(\[DocumentId\], \[ResourceKeyId\]\);";

        _build.Sql.Should().MatchRegex(expected);
    }

    private static bool IsDocumentTable(DbTableName table) =>
        table.Schema.Value == "dms" && table.Name == "Document";

    private static TableConstraint.Unique GetUnique(AbstractIdentityTableInfo abstractTable, string suffix) =>
        abstractTable
            .TableModel.Constraints.OfType<TableConstraint.Unique>()
            .Single(unique => unique.Name == $"UX_{abstractTable.TableModel.Table.Name}{suffix}");

    private static IReadOnlyList<string?> SourcePathsOf(
        AbstractIdentityTableInfo abstractTable,
        IEnumerable<DbColumnName> columnNames
    ) =>
        [
            .. columnNames.Select(columnName =>
                abstractTable
                    .TableModel.Columns.Single(column => column.ColumnName == columnName)
                    .SourceJsonPath?.Canonical
            ),
        ];

    private IReadOnlyList<string?> IdentityJsonPathsOf(QualifiedResourceName abstractResource)
    {
        var projectSchema = _build
            .EffectiveSchemaSet.ProjectsInEndpointOrder.Single(project =>
                project.ProjectName == abstractResource.ProjectName
            )
            .ProjectSchema;

        return
        [
            .. projectSchema["abstractResources"]![abstractResource.ResourceName]!["identityJsonPaths"]!
                .AsArray()
                .Select(path => (string?)path!.GetValue<string>()),
        ];
    }

    private short ResourceKeyIdOf(QualifiedResourceName resource) =>
        _build
            .ModelSet.EffectiveSchema.ResourceKeysInIdOrder.Single(entry => entry.Resource == resource)
            .ResourceKeyId;

    private QualifiedResourceName ConcreteResourceOwning(DbTableName rootTable) =>
        _build
            .ModelSet.ConcreteResourcesInNameOrder.Single(resource =>
                resource.RelationalModel.Root.Table.Equals(rootTable)
            )
            .ResourceKey.Resource;

    private string TriggerValueFragment(short resourceKeyId, string discriminator) =>
        dialect == SqlDialect.Pgsql
            ? $", {resourceKeyId}, '{discriminator}')"
            : $", {resourceKeyId}, N'{discriminator}'";

    private string ViewArmFragment(short resourceKeyId, string discriminator) =>
        dialect == SqlDialect.Pgsql
            ? $@"{resourceKeyId}::smallint AS ""ResourceKeyId"", '{Regex.Escape(discriminator)}'::"
            : $@"CAST\({resourceKeyId} AS smallint\) AS \[ResourceKeyId\], CAST\(N'{Regex.Escape(discriminator)}' AS ";
}

/// <summary>
/// Builds the derived model, emitted DDL and relational-model manifest for one authoritative fixture and
/// dialect, once per test run.
/// </summary>
internal sealed record AbstractIdentityDocumentResourceInvariantBuild(
    EffectiveSchemaSet EffectiveSchemaSet,
    DerivedRelationalModelSet ModelSet,
    string Sql,
    string RelationalModelManifest
)
{
    private static readonly ConcurrentDictionary<
        (string FixtureName, SqlDialect Dialect),
        Lazy<AbstractIdentityDocumentResourceInvariantBuild>
    > _builds = new();

    internal static AbstractIdentityDocumentResourceInvariantBuild Get(string fixtureName, SqlDialect dialect)
    {
        return _builds
            .GetOrAdd(
                (fixtureName, dialect),
                key => new Lazy<AbstractIdentityDocumentResourceInvariantBuild>(() =>
                {
                    var fixtureDirectory = Path.GetFullPath(
                        Path.Combine(
                            FixtureTestHelper.FindProjectRoot(),
                            "..",
                            "Fixtures",
                            "authoritative",
                            key.FixtureName
                        )
                    );
                    var config = FixtureConfigReader.Read(fixtureDirectory);
                    var effectiveSchemaSet = EffectiveSchemaFixtureLoader.LoadEffectiveSchemaSet(
                        fixtureDirectory,
                        config.ApiSchemaFiles
                    );
                    var (modelSet, sql) = DdlPipelineHelpers.BuildDdlForDialect(
                        effectiveSchemaSet,
                        key.Dialect
                    );
                    var manifest = DerivedModelSetManifestEmitter.Emit(
                        modelSet,
                        new HashSet<QualifiedResourceName>(
                            modelSet.ConcreteResourcesInNameOrder.Select(resource =>
                                resource.ResourceKey.Resource
                            )
                        )
                    );

                    return new AbstractIdentityDocumentResourceInvariantBuild(
                        effectiveSchemaSet,
                        modelSet,
                        sql,
                        manifest
                    );
                })
            )
            .Value;
    }
}
