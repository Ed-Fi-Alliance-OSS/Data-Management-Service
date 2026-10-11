// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.RelationalModel.Schema;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_ReadPlan_With_Compact_Descriptor_Projections(SqlDialect dialect)
{
    private const long DocumentId = 5000000042L;
    private const long ReferencedDocumentId = 6000000043L;
    private const int DirectDescriptorId = 42;
    private const int CopiedDescriptorId = 43;
    private const string DirectUri = "uri://Example.org/Kind #MiXeD#Value";
    private const string CopiedUri = "uri://Sample.org/Kind#Other#Value";
    private ResourceReadPlan _plan = null!;
    private HydratedPage _page = null!;
    private JsonNode _document = null!;

    [SetUp]
    public async Task CompileAndHydrate()
    {
        const string fixturePath =
            "Fixtures/runtime-plan-compilation/focused-stable-key/positive/extension-child-collections/fixture.manifest.json";
        var model = RuntimePlanFixtureModelSetBuilder
            .Build(fixturePath, dialect)
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.ResourceKey.Resource == new QualifiedResourceName("Ed-Fi", "School")
            )
            .RelationalModel;
        var scopes = new HashSet<string>(StringComparer.Ordinal)
        {
            "$",
            "$.addresses[*]",
            "$.addresses[*].periods[*]",
            "$._ext.sample.addresses[*]._ext.sample",
            "$._ext.sample",
        };
        var tables = model
            .TablesInDependencyOrder.Where(table => scopes.Contains(table.JsonScope.Canonical))
            .Select(AddReferences)
            .ToArray();
        model = model with
        {
            Root = tables[0],
            TablesInDependencyOrder = tables,
            DescriptorEdgeSources =
            [
                .. tables.SelectMany(table =>
                    table
                        .Columns.Where(column => column.Kind is ColumnKind.DescriptorFk)
                        .Select(column => new DescriptorEdgeSource(
                            false,
                            column.SourceJsonPath!.Value,
                            table.Table,
                            column.ColumnName,
                            column.TargetResource!.Value
                        ))
                ),
            ],
            DocumentReferenceBindings =
            [
                .. tables.Select(table => new DocumentReferenceBinding(
                    false,
                    Path($"{table.JsonScope.Canonical}.schoolReference"),
                    table.Table,
                    new("ReferencedSchool_DocumentId"),
                    new("Ed-Fi", "School"),
                    [
                        new(
                            Path("$.kindDescriptor"),
                            Path($"{table.JsonScope.Canonical}.schoolReference.kindDescriptor"),
                            new("CopiedKind_DescriptorId")
                        ),
                        new(
                            Path("$.schoolId"),
                            Path($"{table.JsonScope.Canonical}.schoolReference.schoolId"),
                            new("ReferencedSchoolId")
                        ),
                    ]
                )),
            ],
        };
        _plan = new ReadPlanCompiler(dialect).Compile(model);
        var projection = _plan.DescriptorProjectionPlansInOrder.Single();
        var descriptorResults = new DataTable();
        descriptorResults.Columns.Add("DescriptorId", typeof(int));
        descriptorResults.Columns.Add("Uri", typeof(string));
        descriptorResults.Rows.Add(DirectDescriptorId, DirectUri);
        descriptorResults.Rows.Add(CopiedDescriptorId, CopiedUri);
        using var reader = descriptorResults.CreateDataReader();
        var descriptorRows = await HydrationReader.ReadDescriptorRowsAsync(
            reader,
            projection,
            CancellationToken.None
        );
        _page = new(
            null,
            [
                new(
                    DocumentId,
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    9876543210L,
                    DateTimeOffset.UnixEpoch,
                    7
                ),
            ],
            [
                .. _plan.TablePlansInDependencyOrder.Select(plan => new HydratedTableRows(
                    plan.TableModel,
                    [CreateRow(plan.TableModel)]
                )),
            ],
            [descriptorRows]
        );
        _document = DocumentReconstituter.ReconstitutePage(_plan, _page).Single();
    }

    [Test]
    public void It_reconstructs_whole_uris_and_joins_compact_keys_in_both_page_and_single_document_sql()
    {
        var projection = _plan.DescriptorProjectionPlansInOrder.Single();
        projection.ResultShape.Should().Be(new DescriptorProjectionResultShape(0, 1));
        foreach (var sql in new[] { projection.SelectByKeysetSql, projection.SelectBySingleDocumentSql! })
        {
            sql.Should()
                .Contain(
                    dialect is SqlDialect.Pgsql
                        ? "d.\"Namespace\" || '#' || d.\"CodeValue\" AS \"Uri\""
                        : "d.[Namespace] + N'#' + d.[CodeValue] AS [Uri]"
                );
            sql.Should()
                .Contain(
                    dialect is SqlDialect.Pgsql
                        ? "d.\"DescriptorId\" = p.\"DescriptorId\""
                        : "d.[DescriptorId] = p.[DescriptorId]"
                );
            sql.Should()
                .NotContain("d.\"DocumentId\"")
                .And.NotContain("d.[DocumentId]")
                .And.NotContain("d.\"Uri\"")
                .And.NotContain("d.[Uri]")
                .And.NotContain("Discriminator")
                .And.NotContain("LOWER");
        }
        foreach (var source in projection.SourcesInOrder)
        {
            var table = _plan.TablePlansInDependencyOrder.Single(table =>
                table.TableModel.Table == source.Table
            );
            table
                .TableModel.Columns[source.DescriptorIdColumnOrdinal]
                .ScalarType!.Kind.Should()
                .Be(ScalarKind.Int32);
        }
    }

    [Test]
    public void It_materializes_original_case_direct_and_copied_identities_in_every_scope()
    {
        JsonNode[] scopes =
        [
            _document,
            _document["addresses"]![0]!,
            _document["addresses"]![0]!["periods"]![0]!,
            _document["addresses"]![0]!["_ext"]!["sample"]!,
            _document["_ext"]!["sample"]!,
        ];
        foreach (var scope in scopes)
        {
            scope["kindDescriptor"]!.GetValue<string>().Should().Be(DirectUri);
            scope["schoolReference"]!["kindDescriptor"]!.GetValue<string>().Should().Be(CopiedUri);
            scope["schoolReference"]!["schoolId"]!.GetValue<int>().Should().Be(12345);
            scope["DirectKind_DescriptorId"].Should().BeNull();
        }
        var single = DocumentReconstituter.Reconstitute(
            DocumentId,
            _plan,
            _page.TableRowsInDependencyOrder,
            new Dictionary<int, string> { [DirectDescriptorId] = DirectUri, [CopiedDescriptorId] = CopiedUri }
        );
        single.ToJsonString().Should().Be(_document.ToJsonString());
    }

    [TestCase(42L)]
    [TestCase(5000000042L)]
    public void It_rejects_wide_hydrated_descriptor_values_without_narrowing(long invalidDescriptorId)
    {
        var rootRows = _page.TableRowsInDependencyOrder[0];
        var row = (object?[])rootRows.Rows.Single().Clone();
        var ordinal = rootRows
            .TableModel.Columns.Select((column, index) => (column, index))
            .Single(entry => entry.column.ColumnName.Value == "DirectKind_DescriptorId")
            .index;
        row[ordinal] = invalidDescriptorId;
        var page = _page with
        {
            TableRowsInDependencyOrder =
            [
                new(rootRows.TableModel, [row]),
                .. _page.TableRowsInDependencyOrder.Skip(1),
            ],
        };
        Action act = () => DocumentReconstituter.ReconstitutePage(_plan, page);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*DirectKind_DescriptorId*Int32 descriptor ID*");
    }

    [Test]
    public void It_rejects_wide_descriptor_storage_metadata_before_emitting_sql()
    {
        var model = _plan.Model;
        var root = model.Root with
        {
            Columns =
            [
                .. model.Root.Columns.Select(column =>
                    column.ColumnName.Value == "DirectKind_DescriptorId"
                        ? column with
                        {
                            ScalarType = new(ScalarKind.Int64),
                        }
                        : column
                ),
            ],
        };
        model = model with
        {
            Root = root,
            TablesInDependencyOrder = [root, .. model.TablesInDependencyOrder.Skip(1)],
        };
        Action act = () => new ReadPlanCompiler(dialect).Compile(model);
        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*DirectKind_DescriptorId*Int32 descriptor storage*");
    }

    private static DbTableModel AddReferences(DbTableModel table) =>
        table with
        {
            Columns =
            [
                .. table.Columns,
                new(
                    new("DirectKind_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    new(ScalarKind.Int32),
                    false,
                    Path($"{table.JsonScope.Canonical}.kindDescriptor"),
                    new("Ed-Fi", "KindDescriptor")
                ),
                new(
                    new("CopiedKind_DescriptorId"),
                    ColumnKind.DescriptorFk,
                    new(ScalarKind.Int32),
                    false,
                    Path($"{table.JsonScope.Canonical}.schoolReference.kindDescriptor"),
                    new("Sample", "KindDescriptor")
                ),
                new(
                    new("ReferencedSchool_DocumentId"),
                    ColumnKind.DocumentFk,
                    new(ScalarKind.Int64),
                    false,
                    Path($"{table.JsonScope.Canonical}.schoolReference"),
                    new("Ed-Fi", "School")
                ),
                new(
                    new("ReferencedSchoolId"),
                    ColumnKind.Scalar,
                    new(ScalarKind.Int32),
                    false,
                    Path($"{table.JsonScope.Canonical}.schoolReference.schoolId"),
                    null
                ),
            ],
        };

    private static object?[] CreateRow(DbTableModel table) =>
        [
            .. table.Columns.Select(column =>
                column.ColumnName.Value switch
                {
                    "DocumentId" or "School_DocumentId" => (object)DocumentId,
                    "CollectionItemId" => table.JsonScope.Canonical == "$.addresses[*]"
                        ? 7000000010L
                        : 7000000100L,
                    "ParentCollectionItemId" or "BaseCollectionItemId" => 7000000010L,
                    "Ordinal" => 0,
                    "DirectKind_DescriptorId" => DirectDescriptorId,
                    "CopiedKind_DescriptorId" => CopiedDescriptorId,
                    "ReferencedSchool_DocumentId" => ReferencedDocumentId,
                    "ReferencedSchoolId" => 12345,
                    _ => null,
                }
            ),
        ];

    private static JsonPathExpression Path(string path) => JsonPathExpressionCompiler.Compile(path);
}
