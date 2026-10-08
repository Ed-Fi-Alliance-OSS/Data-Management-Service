// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_Compact_Descriptor_TrackedChange_Storage(SqlDialect dialectKind)
{
    private ISqlDialect _dialect = default!;
    private string _ddl = default!;
    private TrackedChangeTableInfo _tracked = default!;
    private string _tableSql = default!;
    private string _tombstoneSql = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = SqlDialectFactory.Create(dialectKind);
        var fixture = Path.Combine(FixtureTestHelper.FindProjectRoot(), "Fixtures", "small", "nested");
        var schema = EffectiveSchemaFixtureLoader.LoadFromFixtureDirectory(fixture);
        var (model, ddl) = DdlPipelineHelpers.BuildDdlForDialect(schema, dialectKind, strict: false);
        _ddl = ddl;
        _tracked = model.TrackedChangeTablesInNameOrder.Single(table =>
            table.Kind is TrackedChangeTableKind.SharedDescriptor
        );
        var tableStart = ddl.IndexOf(_dialect.CreateTableHeader(_tracked.Table), StringComparison.Ordinal);
        tableStart.Should().BeGreaterOrEqualTo(0);
        var tableEnd = ddl.IndexOf("\n);", tableStart, StringComparison.Ordinal);
        tableEnd.Should().BeGreaterThan(tableStart);
        _tableSql = ddl[tableStart..(tableEnd + 3)];
        var writer = new SqlWriter(_dialect);
        TrackedChangeTriggerBodyEmitter.EmitDescriptorTombstoneInsert(
            writer,
            _dialect,
            _tracked,
            dialectKind is SqlDialect.Pgsql ? "OLD" : "del",
            fromDeletedSet: dialectKind is SqlDialect.Mssql
        );
        _tombstoneSql = writer.ToString();
    }

    [Test]
    public void It_should_store_a_non_null_smallint_type_key_and_a_bigint_owning_document_key()
    {
        _tableSql.Should().Contain(_dialect.CreateTableHeader(_tracked.Table));
        _tableSql.Should().Contain($"{_dialect.QuoteIdentifier("ResourceKeyId")} smallint NOT NULL");
        _tableSql.Should().Contain($"{_dialect.QuoteIdentifier("DocumentId")} bigint NOT NULL");
        _tableSql.Should().Contain($"{_dialect.QuoteIdentifier("ChangeVersion")} bigint NOT NULL");
        _tableSql.Should().NotContain("Discriminator");
        _tableSql.Should().NotContain("DescriptorId");
        _tableSql.Should().NotContain("FOREIGN KEY");
        _tableSql.Should().NotContain("REFERENCES");
        _ddl.Should().NotContain($"ALTER TABLE {_dialect.QualifyTable(_tracked.Table)}");
    }

    [Test]
    public void It_should_emit_the_type_routing_index_after_creating_the_history_table()
    {
        var index = _dialect.QuoteIdentifier("IX_Descriptor_ResourceKeyId_ChangeVersion");
        var createIndex = dialectKind is SqlDialect.Pgsql ? "CREATE INDEX IF NOT EXISTS" : "CREATE INDEX";
        _ddl.Should()
            .Contain(
                $"ON {_dialect.QualifyTable(_tracked.Table)} "
                    + $"({_dialect.QuoteIdentifier("ResourceKeyId")}, {_dialect.QuoteIdentifier("ChangeVersion")})"
            );
        _ddl.IndexOf($"{createIndex} {index}", StringComparison.Ordinal)
            .Should()
            .BeGreaterThan(
                _ddl.IndexOf(_dialect.CreateTableHeader(_tracked.Table), StringComparison.Ordinal)
            );
    }

    [Test]
    public void It_should_copy_the_deleted_type_and_components_without_reading_live_descriptor_rows()
    {
        var image = dialectKind is SqlDialect.Pgsql ? "OLD" : "del";
        _tombstoneSql
            .Should()
            .Contain(
                $"SELECT\n    {image}.{_dialect.QuoteIdentifier("ResourceKeyId")},\n"
                    + $"    {image}.{_dialect.QuoteIdentifier("Namespace")},\n"
                    + $"    {image}.{_dialect.QuoteIdentifier("CodeValue")},\n"
                    + $"    doc.{_dialect.QuoteIdentifier("DocumentUuid")},\n"
                    + $"    doc.{_dialect.QuoteIdentifier("ContentVersion")},\n"
                    + $"    {image}.{_dialect.QuoteIdentifier("DocumentId")}\n"
            );
        _tombstoneSql.Should().NotContain(_dialect.QualifyTable(DmsTableNames.Descriptor));
        _tombstoneSql.Should().NotContain("Discriminator");
        _tombstoneSql.Should().NotContain("NewNamespace");
        _tombstoneSql.Should().NotContain("NewCodeValue");
        _tombstoneSql.Should().NotContain("DescriptorId");
    }

    [Test]
    public void It_should_reject_an_inventory_without_the_descriptor_type_key()
    {
        var tracked = SharedDescriptorTrackedChangeFixture.Build();
        tracked = tracked with
        {
            SystemColumns = tracked
                .SystemColumns.Where(column => column.Role is not TrackedChangeSystemColumnRole.ResourceKeyId)
                .ToArray(),
        };
        var writer = new SqlWriter(_dialect);
        var emit = () =>
            TrackedChangeTriggerBodyEmitter.EmitDescriptorTombstoneInsert(
                writer,
                _dialect,
                tracked,
                "OLD",
                fromDeletedSet: false
            );

        emit.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*required system column*ResourceKeyId*");
    }
}

[TestFixture(SqlDialect.Pgsql, false)]
[TestFixture(SqlDialect.Pgsql, true)]
[TestFixture(SqlDialect.Mssql, false)]
[TestFixture(SqlDialect.Mssql, true)]
public class Given_TrackedChange_Descriptor_Snapshots(SqlDialect dialectKind, bool unifiedReferenceIdentity)
{
    private ISqlDialect _dialect = default!;
    private TrackedChangeTableInfo _tracked = default!;
    private string _tombstone = default!;
    private string _keyChange = default!;

    [SetUp]
    public void Setup()
    {
        _dialect = SqlDialectFactory.Create(dialectKind);
        var source = CompactDescriptorReferentialIdentityTestModel
            .Build(dialectKind, unifiedReferenceIdentity)
            .ConcreteResourcesInNameOrder.Single()
            .RelationalModel.Root;
        var descriptorColumns = source
            .Columns.Where(column => column.Kind is ColumnKind.DescriptorFk)
            .ToArray();
        _tracked = TrackedChangeEmitterFixture.BuildTrackedTable() with
        {
            SourceTable = source.Table,
            ValueColumnsInTableOrder = descriptorColumns
                .SelectMany(column =>
                    new[]
                    {
                        new TrackedChangeColumnInfo(
                            new($"Old{column.ColumnName.Value}_Namespace"),
                            new($"New{column.ColumnName.Value}_Namespace"),
                            column.SourceJsonPath!.Value.Canonical,
                            null,
                            column.IsNullable,
                            true,
                            new(ScalarKind.String, 255),
                            TrackedChangeColumnRole.DescriptorNamespace,
                            TrackedChangeColumnOrigin.Identity,
                            DescriptorJoinName: column.ColumnName.Value
                        ),
                        new TrackedChangeColumnInfo(
                            new($"Old{column.ColumnName.Value}_CodeValue"),
                            new($"New{column.ColumnName.Value}_CodeValue"),
                            column.SourceJsonPath!.Value.Canonical,
                            null,
                            column.IsNullable,
                            true,
                            new(ScalarKind.String, 50),
                            TrackedChangeColumnRole.DescriptorCodeValue,
                            TrackedChangeColumnOrigin.Identity,
                            DescriptorJoinName: column.ColumnName.Value
                        ),
                    }
                )
                .ToArray(),
            DescriptorJoins = descriptorColumns
                .Select(column => new TrackedChangeDescriptorJoinInfo(
                    column.ColumnName.Value,
                    column.ColumnName,
                    column.TargetResource!.Value
                ))
                .ToArray(),
            PersonJoins = [],
        };
        var plan = TrackedChangeTriggerBodyEmitter.BuildPlan(_tracked, source);
        var tombstoneWriter = new SqlWriter(_dialect);
        var keyChangeWriter = new SqlWriter(_dialect);
        var documentId = new DbColumnName("DocumentId");
        if (dialectKind is SqlDialect.Pgsql)
        {
            TrackedChangeTriggerBodyEmitter.EmitPgsqlTombstoneInsert(
                tombstoneWriter,
                _dialect,
                plan,
                documentId
            );
            TrackedChangeTriggerBodyEmitter.EmitPgsqlKeyChangeInsert(
                keyChangeWriter,
                _dialect,
                plan,
                documentId
            );
        }
        else
        {
            TrackedChangeTriggerBodyEmitter.EmitMssqlTombstoneInsert(
                tombstoneWriter,
                _dialect,
                plan,
                documentId
            );
            TrackedChangeTriggerBodyEmitter.EmitMssqlKeyChangeInsert(
                keyChangeWriter,
                _dialect,
                plan,
                documentId
            );
        }
        _tombstone = tombstoneWriter.ToString();
        _keyChange = keyChangeWriter.ToString();
    }

    [Test]
    public void It_should_join_each_descriptor_snapshot_on_its_compact_key_in_both_row_images()
    {
        var oldImage = dialectKind is SqlDialect.Pgsql ? "OLD" : "del";
        var newImage = dialectKind is SqlDialect.Pgsql ? "NEW" : "i";
        _tracked.DescriptorJoins.Should().HaveCount(2);
        for (var index = 0; index < _tracked.DescriptorJoins.Count; index++)
        {
            var column = _dialect.QuoteIdentifier(_tracked.DescriptorJoins[index].SourceColumn.Value);
            var descriptorId = _dialect.QuoteIdentifier("DescriptorId");
            var oldJoin = $"oldDj{index}.{descriptorId} = {oldImage}.{column}";
            _tombstone.Should().Contain(oldJoin);
            _keyChange.Should().Contain(oldJoin);
            _keyChange.Should().Contain($"newDj{index}.{descriptorId} = {newImage}.{column}");
            _keyChange.Should().Contain($"oldDj{index}.{_dialect.QuoteIdentifier("Namespace")}");
            _keyChange.Should().Contain($"newDj{index}.{_dialect.QuoteIdentifier("CodeValue")}");
            _tombstone.Should().NotContain($"oldDj{index}.{_dialect.QuoteIdentifier("DocumentId")}");
            _keyChange.Should().NotContain($"newDj{index}.{_dialect.QuoteIdentifier("DocumentId")}");
        }
    }
}
