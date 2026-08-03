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
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_CdcSourceInventoryBuilder(SqlDialect dialect)
{
    private IReadOnlyList<CdcSourceTableInventory> _inventory = null!;

    [SetUp]
    public void SetUp()
    {
        _inventory = CdcSourceInventoryBuilder.BuildExpectedSourceInventory(
            SqlDialectFactory.Create(dialect)
        );
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

[TestFixture]
public class Given_CdcSourceInventoryValidator
{
    private IReadOnlyList<CdcSourceTableInventory> _expected = null!;

    [SetUp]
    public void SetUp()
    {
        _expected = CdcSourceInventoryBuilder.BuildExpectedSourceInventory(
            SqlDialectFactory.Create(SqlDialect.Pgsql)
        );
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
