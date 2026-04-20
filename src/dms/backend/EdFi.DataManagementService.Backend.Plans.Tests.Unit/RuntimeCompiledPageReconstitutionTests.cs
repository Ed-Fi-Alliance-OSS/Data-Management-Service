// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_DocumentReconstituter_With_A_Runtime_Compiled_Page_For_Canonical_Collection_Extension_Scope(
    SqlDialect dialect
)
{
    private const string FixturePath =
        "Fixtures/runtime-plan-compilation/focused-stable-key/positive/extension-child-collections/fixture.manifest.json";

    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");

    private IReadOnlyList<JsonNode> _pageResult = null!;
    private JsonNode _compiledResult = null!;

    [SetUp]
    public void SetUp()
    {
        var modelSet = RuntimePlanFixtureModelSetBuilder.Build(FixturePath, dialect);
        var schoolModel = modelSet.ConcreteResourcesInNameOrder.Single(resource =>
            resource.ResourceKey.Resource == _schoolResource
        );
        var readPlan = new ReadPlanCompiler(dialect).Compile(schoolModel.RelationalModel);
        var hydratedPage = RuntimeCompiledPageReconstitutionTestData.CreateHydratedPage(readPlan);

        _pageResult = DocumentReconstituter.ReconstitutePage(readPlan, hydratedPage);
        _compiledResult = DocumentReconstituter.Reconstitute(
            1L,
            readPlan,
            hydratedPage.TableRowsInDependencyOrder,
            new Dictionary<long, string>()
        );
    }

    [Test]
    public void It_should_reconstitute_the_runtime_compiled_collection_extension_under_the_address_item()
    {
        var document = _pageResult.Should().ContainSingle().Subject;

        document["schoolId"]!.GetValue<long>().Should().Be(255901L);
        document["addresses"]!.AsArray().Should().HaveCount(2);
        document["addresses"]![0]!["city"]!.GetValue<string>().Should().Be("Austin");
        document["addresses"]![0]!["periods"]![0]!["periodName"]!.GetValue<string>().Should().Be("Morning");
        document["addresses"]![0]!["_ext"]!["sample"]!["zone"]!.GetValue<string>().Should().Be("North");
        document["addresses"]![1]!["city"]!.GetValue<string>().Should().Be("Dallas");
        document["addresses"]![1]!["_ext"]!["sample"]!["zone"]!.GetValue<string>().Should().Be("South");
    }

    [Test]
    public void It_should_not_emit_a_spurious_addresses_branch_beneath_address_items()
    {
        var document = _pageResult.Should().ContainSingle().Subject;

        document["addresses"]![0]!["addresses"].Should().BeNull();
        document["addresses"]![1]!["addresses"].Should().BeNull();
        _compiledResult["addresses"]![0]!["addresses"].Should().BeNull();
        _compiledResult["addresses"]![1]!["addresses"].Should().BeNull();
    }

    [Test]
    public void It_should_match_the_single_document_compiled_entry_point()
    {
        _compiledResult
            .ToJsonString()
            .Should()
            .Be(_pageResult.Should().ContainSingle().Subject.ToJsonString());
    }
}

file static class RuntimeCompiledPageReconstitutionTestData
{
    public static HydratedPage CreateHydratedPage(ResourceReadPlan readPlan)
    {
        var tablesByScope = readPlan.TablePlansInDependencyOrder.ToDictionary(
            tablePlan => tablePlan.TableModel.JsonScope.Canonical,
            tablePlan => tablePlan.TableModel
        );

        var rootTable = RequireTable(tablesByScope, "$");
        var addressesTable = RequireTable(tablesByScope, "$.addresses[*]");
        var periodsTable = RequireTable(tablesByScope, "$.addresses[*].periods[*]");
        var alignedExtensionTable = RequireTable(tablesByScope, "$._ext.sample.addresses[*]._ext.sample");

        var rowsByScope = new Dictionary<string, IReadOnlyList<object?[]>>
        {
            ["$"] =
            [
                CreateRow(
                    rootTable,
                    columnValues: new Dictionary<string, object?> { ["DocumentId"] = 1L },
                    sourcePathValues: new Dictionary<string, object?> { ["$.schoolId"] = 255901L }
                ),
            ],
            ["$.addresses[*]"] =
            [
                CreateRow(
                    addressesTable,
                    columnValues: new Dictionary<string, object?>
                    {
                        ["CollectionItemId"] = 10L,
                        ["School_DocumentId"] = 1L,
                        ["Ordinal"] = 0,
                    },
                    sourcePathValues: new Dictionary<string, object?> { ["$.addresses[*].city"] = "Austin" }
                ),
                CreateRow(
                    addressesTable,
                    columnValues: new Dictionary<string, object?>
                    {
                        ["CollectionItemId"] = 20L,
                        ["School_DocumentId"] = 1L,
                        ["Ordinal"] = 1,
                    },
                    sourcePathValues: new Dictionary<string, object?> { ["$.addresses[*].city"] = "Dallas" }
                ),
            ],
            ["$.addresses[*].periods[*]"] =
            [
                CreateRow(
                    periodsTable,
                    columnValues: new Dictionary<string, object?>
                    {
                        ["CollectionItemId"] = 100L,
                        ["School_DocumentId"] = 1L,
                        ["ParentCollectionItemId"] = 10L,
                        ["Ordinal"] = 0,
                    },
                    sourcePathValues: new Dictionary<string, object?>
                    {
                        ["$.addresses[*].periods[*].periodName"] = "Morning",
                    }
                ),
            ],
            ["$._ext.sample.addresses[*]._ext.sample"] =
            [
                CreateRow(
                    alignedExtensionTable,
                    columnValues: new Dictionary<string, object?>
                    {
                        ["BaseCollectionItemId"] = 10L,
                        ["School_DocumentId"] = 1L,
                    },
                    sourcePathValues: new Dictionary<string, object?>
                    {
                        ["$._ext.sample.addresses[*]._ext.sample.zone"] = "North",
                    }
                ),
                CreateRow(
                    alignedExtensionTable,
                    columnValues: new Dictionary<string, object?>
                    {
                        ["BaseCollectionItemId"] = 20L,
                        ["School_DocumentId"] = 1L,
                    },
                    sourcePathValues: new Dictionary<string, object?>
                    {
                        ["$._ext.sample.addresses[*]._ext.sample.zone"] = "South",
                    }
                ),
            ],
        };

        return new HydratedPage(
            TotalCount: null,
            DocumentMetadata:
            [
                new DocumentMetadataRow(
                    DocumentId: 1L,
                    DocumentUuid: Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    ContentVersion: 1L,
                    IdentityVersion: 1L,
                    ContentLastModifiedAt: new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero),
                    IdentityLastModifiedAt: new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero)
                ),
            ],
            TableRowsInDependencyOrder:
            [
                .. readPlan.TablePlansInDependencyOrder.Select(tablePlan => new HydratedTableRows(
                    tablePlan.TableModel,
                    rowsByScope.GetValueOrDefault(tablePlan.TableModel.JsonScope.Canonical, [])
                )),
            ],
            DescriptorRowsInPlanOrder: []
        );
    }

    private static DbTableModel RequireTable(
        IReadOnlyDictionary<string, DbTableModel> tablesByScope,
        string scope
    )
    {
        tablesByScope.Should().ContainKey(scope);
        return tablesByScope[scope];
    }

    private static object?[] CreateRow(
        DbTableModel tableModel,
        IReadOnlyDictionary<string, object?>? columnValues = null,
        IReadOnlyDictionary<string, object?>? sourcePathValues = null
    )
    {
        var row = new object?[tableModel.Columns.Count];

        if (columnValues is not null)
        {
            foreach (var (columnName, value) in columnValues)
            {
                SetColumnValueByNameOrThrow(tableModel, row, columnName, value);
            }
        }

        if (sourcePathValues is not null)
        {
            foreach (var (sourcePath, value) in sourcePathValues)
            {
                SetColumnValueBySourcePathOrThrow(tableModel, row, sourcePath, value);
            }
        }

        return row;
    }

    private static void SetColumnValueByNameOrThrow(
        DbTableModel tableModel,
        object?[] row,
        string columnName,
        object? value
    )
    {
        var ordinal = tableModel
            .Columns.Select((column, index) => (column, index))
            .SingleOrDefault(entry =>
                string.Equals(entry.column.ColumnName.Value, columnName, StringComparison.Ordinal)
            );

        if (ordinal.column is null)
        {
            throw new InvalidOperationException(
                $"Table '{tableModel.Table}' does not contain column '{columnName}'."
            );
        }

        row[ordinal.index] = value;
    }

    private static void SetColumnValueBySourcePathOrThrow(
        DbTableModel tableModel,
        object?[] row,
        string sourcePath,
        object? value
    )
    {
        var ordinal = tableModel
            .Columns.Select((column, index) => (column, index))
            .SingleOrDefault(entry =>
                string.Equals(entry.column.SourceJsonPath?.Canonical, sourcePath, StringComparison.Ordinal)
            );

        if (ordinal.column is null)
        {
            throw new InvalidOperationException(
                $"Table '{tableModel.Table}' does not contain a stored column for source path '{sourcePath}'."
            );
        }

        row[ordinal.index] = value;
    }
}
