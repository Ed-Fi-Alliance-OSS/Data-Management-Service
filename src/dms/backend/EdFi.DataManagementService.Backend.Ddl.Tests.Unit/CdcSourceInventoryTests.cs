// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_CdcSourceInventoryContract
{
    [Test]
    public void It_should_name_the_fixed_cdc_source_table_order()
    {
        CdcSourceInventoryContract
            .RequiredSourceTableKinds.Should()
            .Equal(
                CdcSourceTableKind.DocumentCache,
                CdcSourceTableKind.Document,
                CdcSourceTableKind.CdcHeartbeat
            );
        CdcSourceInventoryContract
            .RequiredSourceTableOrdinal((CdcSourceTableKind)999)
            .Should()
            .Be(int.MaxValue);
    }

    [Test]
    public void It_should_accept_valid_required_source_inventory_in_emitted_column_order()
    {
        var inventory = CdcSourceInventoryTestEmission.EmitCoreCdcSourceInventory(SqlDialect.Pgsql);

        var validated = CdcSourceInventoryContract.ValidateRequiredSourceInventory(
            inventory,
            nameof(inventory)
        );

        validated.Should().BeSameAs(inventory);
    }

    [Test]
    public void It_should_reject_required_source_columns_when_contiguous_ordinals_are_out_of_list_order()
    {
        var inventory = ReplaceTable(
            CdcSourceInventoryTestEmission.EmitCoreCdcSourceInventory(SqlDialect.Pgsql),
            CdcSourceTableKind.DocumentCache,
            table => ReplaceColumns(table, [table.Columns[1], table.Columns[0], .. table.Columns.Skip(2)])
        );

        Action action = () =>
            CdcSourceInventoryContract.ValidateRequiredSourceInventory(inventory, nameof(inventory));

        action.Should().Throw<ArgumentException>().WithMessage("*contiguous ordinal order starting at 1*");
    }

    [Test]
    public void It_should_reject_required_source_columns_when_ordinals_are_not_contiguous()
    {
        var inventory = ReplaceTable(
            CdcSourceInventoryTestEmission.EmitCoreCdcSourceInventory(SqlDialect.Pgsql),
            CdcSourceTableKind.DocumentCache,
            table =>
                ReplaceColumns(
                    table,
                    table
                        .Columns.Select(
                            (column, index) =>
                                index == 2 ? CopyColumn(column, ordinal: table.Columns.Count + 1) : column
                        )
                        .ToArray()
                )
        );

        Action action = () =>
            CdcSourceInventoryContract.ValidateRequiredSourceInventory(inventory, nameof(inventory));

        action.Should().Throw<ArgumentException>().WithMessage("*contiguous ordinal order starting at 1*");
    }

    [Test]
    public void It_should_reject_source_columns_with_duplicate_zero_or_negative_ordinals()
    {
        var table = CdcSourceInventoryTestEmission
            .EmitCoreCdcSourceInventory(SqlDialect.Pgsql)
            .Single(table => table.TableKind == CdcSourceTableKind.DocumentCache);

        Action duplicateOrdinal = () =>
            ReplaceColumns(table, [table.Columns[0], CopyColumn(table.Columns[1], ordinal: 1)]);
        Action zeroOrdinal = () => CopyColumn(table.Columns[0], ordinal: 0);
        Action negativeOrdinal = () => CopyColumn(table.Columns[0], ordinal: -1);

        duplicateOrdinal.Should().Throw<ArgumentException>().WithMessage("*ordinals must be unique*");
        zeroOrdinal.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*positive*");
        negativeOrdinal.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*positive*");
    }

    [Test]
    public void It_should_reject_duplicate_required_source_column_names()
    {
        IReadOnlyList<CdcSourceTableInventory> inventory = CdcSourceInventoryTestEmission
            .EmitCoreCdcSourceInventory(SqlDialect.Pgsql)
            .Select(table =>
                table.TableKind == CdcSourceTableKind.CdcHeartbeat
                    ? new CdcSourceTableInventory(
                        table.TableKind,
                        table.TableName,
                        table.EmittedQuotedTableName,
                        [
                            .. table.Columns,
                            new CdcSourceColumnInventory(
                                new DbColumnName("HeartbeatId"),
                                @"""HeartbeatId""",
                                4,
                                "integer",
                                IsNullable: false
                            ),
                        ]
                    )
                    : table
            )
            .ToArray();

        Action action = () =>
            CdcSourceInventoryContract.ValidateRequiredSourceInventory(inventory, nameof(inventory));

        action.Should().Throw<ArgumentException>().WithMessage("*duplicate contract columns*");
    }

    [Test]
    public void It_should_reject_a_default_physical_column_name()
    {
        Action action = () => BuildColumn(default);

        action.Should().Throw<ArgumentException>().WithParameterName("ColumnName");
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("Document\nUuid")]
    public void It_should_reject_blank_or_control_character_physical_column_names(string columnName)
    {
        Action action = () => BuildColumn(new DbColumnName(columnName));

        action.Should().Throw<ArgumentException>().WithParameterName("ColumnName");
    }

    [Test]
    public void It_should_snapshot_source_columns()
    {
        var expectedColumn = BuildColumn(new DbColumnName("DocumentUuid"));
        var callerOwnedColumns = new List<CdcSourceColumnInventory> { expectedColumn };
        var inventory = new CdcSourceTableInventory(
            CdcSourceTableKind.Document,
            DmsTableNames.Document,
            @"""dms"".""Document""",
            callerOwnedColumns
        );

        callerOwnedColumns[0] = BuildColumn(new DbColumnName("ChangedColumn"));
        callerOwnedColumns.Add(BuildColumn(new DbColumnName("AddedColumn"), ordinal: 2));

        inventory.Columns.Should().Equal(expectedColumn);
    }

    private static IReadOnlyList<CdcSourceTableInventory> ReplaceTable(
        IReadOnlyList<CdcSourceTableInventory> inventory,
        CdcSourceTableKind tableKind,
        Func<CdcSourceTableInventory, CdcSourceTableInventory> replace
    ) => inventory.Select(table => table.TableKind == tableKind ? replace(table) : table).ToArray();

    private static CdcSourceTableInventory ReplaceColumns(
        CdcSourceTableInventory table,
        IReadOnlyList<CdcSourceColumnInventory> columns
    ) => new(table.TableKind, table.TableName, table.EmittedQuotedTableName, columns);

    private static CdcSourceColumnInventory CopyColumn(CdcSourceColumnInventory column, int ordinal) =>
        new(
            column.ColumnName,
            column.EmittedQuotedColumnName,
            ordinal,
            column.ProviderDataType,
            column.IsNullable
        );

    private static CdcSourceColumnInventory BuildColumn(DbColumnName columnName, int ordinal = 1) =>
        new(columnName, @"""DocumentUuid""", ordinal, "uuid", IsNullable: false);
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_CoreDdlMetadata_For_CdcSourceInventory(SqlDialect dialect)
{
    private IReadOnlyList<CdcSourceTableInventory> _inventory = null!;

    [SetUp]
    public void SetUp()
    {
        _inventory = CdcSourceInventoryTestEmission.EmitCoreCdcSourceInventory(dialect);
    }

    [Test]
    public void It_should_build_exactly_the_fixed_cdc_source_tables()
    {
        _inventory
            .Select(table => table.TableKind)
            .Should()
            .Equal(CdcSourceInventoryContract.RequiredSourceTableKinds);

        _inventory.Select(table => table.TableName).Should().NotContain(DmsTableNames.DocumentProjectionWork);
    }

    [Test]
    public void It_should_use_emitted_quoted_physical_table_identifiers()
    {
        var document = _inventory.Single(table => table.TableKind == CdcSourceTableKind.Document);
        var documentCache = _inventory.Single(table => table.TableKind == CdcSourceTableKind.DocumentCache);
        var cdcHeartbeat = _inventory.Single(table => table.TableKind == CdcSourceTableKind.CdcHeartbeat);

        if (dialect == SqlDialect.Pgsql)
        {
            document.EmittedQuotedTableName.Should().Be(@"""dms"".""Document""");
            documentCache.EmittedQuotedTableName.Should().Be(@"""dms"".""DocumentCache""");
            cdcHeartbeat.EmittedQuotedTableName.Should().Be(@"""dms"".""CdcHeartbeat""");
        }
        else
        {
            document.EmittedQuotedTableName.Should().Be("[dms].[Document]");
            documentCache.EmittedQuotedTableName.Should().Be("[dms].[DocumentCache]");
            cdcHeartbeat.EmittedQuotedTableName.Should().Be("[dms].[CdcHeartbeat]");
        }
    }

    [Test]
    public void It_should_include_all_document_columns_in_table_ordinal_order()
    {
        var document = _inventory.Single(table => table.TableKind == CdcSourceTableKind.Document);

        document
            .Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal(
                "DocumentId",
                "DocumentUuid",
                "ResourceKeyId",
                "CreatedByOwnershipTokenId",
                "ContentVersion",
                "IdentityVersion",
                "ContentLastModifiedAt",
                "IdentityLastModifiedAt",
                "CreatedAt"
            );
        document.Columns.Select(column => column.Ordinal).Should().Equal(Enumerable.Range(1, 9));
        document
            .Columns.Single(column => column.ColumnName.Value == "CreatedByOwnershipTokenId")
            .IsNullable.Should()
            .BeTrue();
    }

    [Test]
    public void It_should_include_document_cache_provider_types_from_core_metadata()
    {
        var documentCache = _inventory.Single(table => table.TableKind == CdcSourceTableKind.DocumentCache);

        var documentJson = documentCache.Columns.Single(column => column.ColumnName.Value == "DocumentJson");
        var streamEtag = documentCache.Columns.Single(column => column.ColumnName.Value == "StreamEtag");

        documentJson.ProviderDataType.Should().Be(dialect == SqlDialect.Pgsql ? "jsonb" : "nvarchar(max)");
        streamEtag.ProviderDataType.Should().Be("varchar(64)");
    }

    [Test]
    public void It_should_include_the_opt_in_heartbeat_inventory_without_the_work_table()
    {
        var cdcHeartbeat = _inventory.Single(table => table.TableKind == CdcSourceTableKind.CdcHeartbeat);

        cdcHeartbeat
            .Columns.Select(column => column.ColumnName.Value)
            .Should()
            .Equal("HeartbeatId", "HeartbeatSequence", "HeartbeatAt");
        cdcHeartbeat
            .Columns.Single(column => column.ColumnName.Value == "HeartbeatAt")
            .ProviderDataType.Should()
            .Be(dialect == SqlDialect.Pgsql ? "timestamp with time zone" : "datetime2(7)");
    }
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_DdlPipelineEmission_For_CdcSourceInventory(SqlDialect dialect)
{
    private DdlPipelineEmission _emission = null!;

    [SetUp]
    public void SetUp()
    {
        var effectiveSchemaSet = SmallFixtureEffectiveSchemaSetLoader.Load("minimal");
        _emission = DdlPipelineHelpers.BuildDdlEmissionForDialect(effectiveSchemaSet, dialect, strict: false);
    }

    [Test]
    public void It_should_expose_the_core_ddl_cdc_source_inventory()
    {
        _emission
            .CdcSourceInventory.Should()
            .BeEquivalentTo(
                CdcSourceInventoryTestEmission.EmitCoreCdcSourceInventory(dialect),
                options => options.WithStrictOrdering()
            );
    }

    [Test]
    public void It_should_not_add_opt_in_cdc_objects_to_ordinary_ddl()
    {
        _emission.CombinedSql.Should().NotContain("CdcHeartbeat");
        _emission.CombinedSql.Should().Contain("DocumentCache");
        _emission.CombinedSql.Should().Contain("DocumentProjectionWork");
    }
}

[TestFixture]
public class Given_CdcSourceInventoryValidator
{
    private IReadOnlyList<CdcSourceTableInventory> _expected = null!;

    [SetUp]
    public void SetUp()
    {
        _expected = CdcSourceInventoryTestEmission.EmitCoreCdcSourceInventory(SqlDialect.Pgsql);
    }

    [Test]
    public void It_should_accept_an_exact_live_inventory_match()
    {
        var diagnostics = CdcSourceInventoryValidator.ValidateLiveSourceInventory(_expected, _expected);

        diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_should_fail_closed_when_a_required_source_table_is_missing()
    {
        var observed = _expected.Where(table => table.TableKind != CdcSourceTableKind.CdcHeartbeat).ToArray();

        var diagnostics = CdcSourceInventoryValidator.ValidateLiveSourceInventory(_expected, observed);

        diagnostics
            .Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_SOURCE_TABLE_MISSING")
            .Which.Should()
            .Match<CdcProviderDiagnostic>(diagnostic =>
                diagnostic.Category == CdcProviderDiagnosticCategory.MissingRequiredSourceObject
                && diagnostic.ArtifactKind == CdcProviderArtifactKind.SourceTable
                && diagnostic.SafeName.Value == "dms.CdcHeartbeat"
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.FailClosed
            );
    }

    [Test]
    public void It_should_fail_closed_when_a_source_column_type_does_not_match()
    {
        var observed = ReplaceTable(
            _expected,
            CdcSourceTableKind.DocumentCache,
            table =>
                ReplaceColumns(
                    table,
                    table
                        .Columns.Select(column =>
                            column.ColumnName.Value == "DocumentJson"
                                ? CopyColumn(column, providerDataType: "text")
                                : column
                        )
                        .ToArray()
                )
        );

        var diagnostics = CdcSourceInventoryValidator.ValidateLiveSourceInventory(_expected, observed);

        diagnostics
            .Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_SOURCE_COLUMN_TYPE_MISMATCH")
            .Which.SafeName.Value.Should()
            .Be("dms.DocumentCache.DocumentJson");
    }

    [Test]
    public void It_should_fail_closed_when_column_order_does_not_match()
    {
        var observed = ReplaceTable(
            _expected,
            CdcSourceTableKind.DocumentCache,
            table =>
                ReplaceColumns(
                    table,
                    [
                        CopyColumn(table.Columns[1], ordinal: 1),
                        CopyColumn(table.Columns[0], ordinal: 2),
                        .. table.Columns.Skip(2),
                    ]
                )
        );

        var diagnostics = CdcSourceInventoryValidator.ValidateLiveSourceInventory(_expected, observed);

        diagnostics.Should().Contain(diagnostic => diagnostic.Code == "CDC_SOURCE_COLUMN_ORDER_MISMATCH");
    }

    [Test]
    public void It_should_report_missing_columns_when_observed_catalog_ordinals_are_sparse()
    {
        IReadOnlyList<CdcProviderDiagnostic> diagnostics = [];
        var observed = ReplaceTable(
            _expected,
            CdcSourceTableKind.DocumentCache,
            table =>
                ReplaceColumns(
                    table,
                    table.Columns.Where(column => column.ColumnName.Value != "ResourceName").ToArray()
                )
        );

        Action action = () =>
            diagnostics = CdcSourceInventoryValidator.ValidateLiveSourceInventory(_expected, observed);

        action.Should().NotThrow();
        diagnostics
            .Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SOURCE_COLUMN_MISSING"
                && diagnostic.SafeName.Value == "dms.DocumentCache.ResourceName"
                && diagnostic.ExpectedValue == "present"
                && diagnostic.ObservedValue == "missing"
            );
    }

    [Test]
    public void It_should_fail_closed_when_the_work_table_is_added_to_observed_inventory()
    {
        var observed = _expected
            .Append(
                new CdcSourceTableInventory(
                    (CdcSourceTableKind)999,
                    DmsTableNames.DocumentProjectionWork,
                    @"""dms"".""DocumentProjectionWork""",
                    [
                        new CdcSourceColumnInventory(
                            new DbColumnName("DocumentId"),
                            @"""DocumentId""",
                            1,
                            "bigint",
                            IsNullable: false
                        ),
                    ]
                )
            )
            .ToArray();

        var diagnostics = CdcSourceInventoryValidator.ValidateLiveSourceInventory(_expected, observed);

        diagnostics
            .Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_WORK_TABLE_SOURCE_INVENTORY_FORBIDDEN")
            .Which.Category.Should()
            .Be(CdcProviderDiagnosticCategory.WorkTableCaptureViolation);
    }

    private static IReadOnlyList<CdcSourceTableInventory> ReplaceTable(
        IReadOnlyList<CdcSourceTableInventory> inventory,
        CdcSourceTableKind tableKind,
        Func<CdcSourceTableInventory, CdcSourceTableInventory> replace
    ) => inventory.Select(table => table.TableKind == tableKind ? replace(table) : table).ToArray();

    private static CdcSourceTableInventory ReplaceColumns(
        CdcSourceTableInventory table,
        IReadOnlyList<CdcSourceColumnInventory> columns
    ) => new(table.TableKind, table.TableName, table.EmittedQuotedTableName, columns);

    private static CdcSourceColumnInventory CopyColumn(
        CdcSourceColumnInventory column,
        int? ordinal = null,
        string? providerDataType = null
    ) =>
        new(
            column.ColumnName,
            column.EmittedQuotedColumnName,
            ordinal ?? column.Ordinal,
            providerDataType ?? column.ProviderDataType,
            column.IsNullable
        );
}

internal static class CdcSourceInventoryTestEmission
{
    internal static IReadOnlyList<CdcSourceTableInventory> EmitCoreCdcSourceInventory(SqlDialect dialect) =>
        new CoreDdlEmitter(SqlDialectFactory.Create(dialect)).EmitWithMetadata().CdcSourceInventory;
}
