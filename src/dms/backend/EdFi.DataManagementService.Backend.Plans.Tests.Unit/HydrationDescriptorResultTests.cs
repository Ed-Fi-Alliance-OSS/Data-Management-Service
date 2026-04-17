// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Plans.Tests.Unit.HydrationBatchBuilderTestHelper;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_HydrationReader_With_Descriptor_Result_Sets
{
    [Test]
    public async Task It_reads_descriptor_pairs_using_the_compiled_result_shape()
    {
        var descriptorPlan = new DescriptorProjectionPlan(
            SelectByKeysetSql: "SELECT \"Uri\", \"DescriptorId\" FROM dms.\"Descriptor\";",
            ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 1, UriOrdinal: 0),
            SourcesInOrder: []
        );

        using var reader = HydrationDescriptorResultTestHelper.CreateReader(
            HydrationDescriptorResultTestHelper.CreateDescriptorRowsTableWithUriFirst(
                ("uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade", 101L),
                ("uri://ed-fi.org/GradeLevelDescriptor#Eleventh Grade", 202L)
            )
        );

        var result = await HydrationReader.ReadDescriptorRowsAsync(
            reader,
            descriptorPlan,
            CancellationToken.None
        );

        result
            .Rows.Should()
            .Equal(
                new DescriptorUriRow(101L, "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"),
                new DescriptorUriRow(202L, "uri://ed-fi.org/GradeLevelDescriptor#Eleventh Grade")
            );
    }
}

[TestFixture]
public class Given_HydrationExecutor_With_Descriptor_Result_Sets
{
    [Test]
    public async Task It_returns_descriptor_rows_in_compiled_plan_order()
    {
        var descriptorPlans = new[]
        {
            new DescriptorProjectionPlan(
                SelectByKeysetSql: "SELECT descriptor rows FROM root_descriptor;",
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: []
            ),
            new DescriptorProjectionPlan(
                SelectByKeysetSql: "SELECT descriptor rows FROM child_descriptor;",
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: []
            ),
        };

        var command = new RecordingDbCommand(
            HydrationDescriptorResultTestHelper.CreateReader(
                CreateDocumentMetadataTable(
                    (
                        42L,
                        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                        44L,
                        45L,
                        new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 4, 2, 12, 1, 0, TimeSpan.Zero)
                    )
                ),
                CreateRootTableRows((42L, 255901)),
                CreateChildTableRows((100L, 42L, 0, "Springfield")),
                CreateDescriptorRowsTableWithDescriptorIdFirst(
                    (301L, "uri://ed-fi.org/SchoolTypeDescriptor#Charter")
                ),
                CreateDescriptorRowsTableWithDescriptorIdFirst(
                    (401L, "uri://ed-fi.org/AddressTypeDescriptor#Home")
                )
            )
        );

        var connection = new RecordingDbConnection(command);

        var result = await HydrationExecutor.ExecuteAsync(
            connection,
            BuildTestReadPlan(SqlDialect.Pgsql, descriptorPlans),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            CancellationToken.None
        );

        result.DescriptorRowsInPlanOrder.Should().HaveCount(2);
        result
            .DescriptorRowsInPlanOrder[0]
            .Rows.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new DescriptorUriRow(301L, "uri://ed-fi.org/SchoolTypeDescriptor#Charter"));
        result
            .DescriptorRowsInPlanOrder[1]
            .Rows.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new DescriptorUriRow(401L, "uri://ed-fi.org/AddressTypeDescriptor#Home"));
    }

    [Test]
    public async Task It_executes_descriptor_projection_as_part_of_the_single_hydration_batch()
    {
        var descriptorPlans = new[]
        {
            new DescriptorProjectionPlan(
                SelectByKeysetSql: "SELECT descriptor rows FROM root_descriptor;",
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: []
            ),
            new DescriptorProjectionPlan(
                SelectByKeysetSql: "SELECT descriptor rows FROM child_descriptor;",
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: []
            ),
        };

        var command = new RecordingDbCommand(
            HydrationDescriptorResultTestHelper.CreateReader(
                CreateDocumentMetadataTable(
                    (
                        42L,
                        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                        44L,
                        45L,
                        new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 4, 2, 12, 1, 0, TimeSpan.Zero)
                    )
                ),
                CreateRootTableRows((42L, 255901)),
                CreateChildTableRows((100L, 42L, 0, "Springfield")),
                CreateDescriptorRowsTableWithDescriptorIdFirst(
                    (301L, "uri://ed-fi.org/SchoolTypeDescriptor#Charter")
                ),
                CreateDescriptorRowsTableWithDescriptorIdFirst(
                    (401L, "uri://ed-fi.org/AddressTypeDescriptor#Home")
                )
            )
        );

        var connection = new RecordingDbConnection(command);

        await HydrationExecutor.ExecuteAsync(
            connection,
            BuildTestReadPlan(SqlDialect.Pgsql, descriptorPlans),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            CancellationToken.None
        );

        command.ExecuteReaderCount.Should().Be(1);

        var commandText = command.CommandText;
        var rootTableIndex = commandText.IndexOf("SELECT root columns FROM root;", StringComparison.Ordinal);
        var childTableIndex = commandText.IndexOf(
            "SELECT child columns FROM child;",
            StringComparison.Ordinal
        );
        var rootDescriptorIndex = commandText.IndexOf(
            "SELECT descriptor rows FROM root_descriptor;",
            StringComparison.Ordinal
        );
        var childDescriptorIndex = commandText.IndexOf(
            "SELECT descriptor rows FROM child_descriptor;",
            StringComparison.Ordinal
        );

        rootTableIndex.Should().BePositive();
        childTableIndex.Should().BeGreaterThan(rootTableIndex);
        rootDescriptorIndex.Should().BeGreaterThan(childTableIndex);
        childDescriptorIndex.Should().BeGreaterThan(rootDescriptorIndex);
    }

    [Test]
    public async Task It_can_skip_descriptor_result_sets_when_projection_is_disabled()
    {
        var descriptorPlans = new[]
        {
            new DescriptorProjectionPlan(
                SelectByKeysetSql: "SELECT descriptor rows FROM root_descriptor;",
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: []
            ),
            new DescriptorProjectionPlan(
                SelectByKeysetSql: "SELECT descriptor rows FROM child_descriptor;",
                ResultShape: new DescriptorProjectionResultShape(DescriptorIdOrdinal: 0, UriOrdinal: 1),
                SourcesInOrder: []
            ),
        };

        var command = new RecordingDbCommand(
            HydrationDescriptorResultTestHelper.CreateReader(
                CreateDocumentMetadataTable(
                    (
                        42L,
                        Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                        44L,
                        45L,
                        new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                        new DateTimeOffset(2026, 4, 2, 12, 1, 0, TimeSpan.Zero)
                    )
                ),
                CreateRootTableRows((42L, 255901)),
                CreateChildTableRows((100L, 42L, 0, "Springfield"))
            )
        );

        var connection = new RecordingDbConnection(command);

        var result = await HydrationExecutor.ExecuteAsync(
            connection,
            BuildTestReadPlan(SqlDialect.Pgsql, descriptorPlans),
            new PageKeysetSpec.Single(42L),
            SqlDialect.Pgsql,
            new HydrationExecutionOptions(IncludeDescriptorProjection: false),
            CancellationToken.None
        );

        result.DescriptorRowsInPlanOrder.Should().HaveCount(2);
        result.DescriptorRowsInPlanOrder.Should().OnlyContain(rows => rows.Rows.Count == 0);
        command.CommandText.Should().NotContain("SELECT descriptor rows FROM root_descriptor;");
        command.CommandText.Should().NotContain("SELECT descriptor rows FROM child_descriptor;");
    }

    private static DataTable CreateDocumentMetadataTable(
        params (
            long DocumentId,
            Guid DocumentUuid,
            long ContentVersion,
            long IdentityVersion,
            DateTimeOffset ContentLastModifiedAt,
            DateTimeOffset IdentityLastModifiedAt
        )[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("DocumentUuid", typeof(Guid));
        table.Columns.Add("ContentVersion", typeof(long));
        table.Columns.Add("IdentityVersion", typeof(long));
        table.Columns.Add("ContentLastModifiedAt", typeof(DateTimeOffset));
        table.Columns.Add("IdentityLastModifiedAt", typeof(DateTimeOffset));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.DocumentId,
                row.DocumentUuid,
                row.ContentVersion,
                row.IdentityVersion,
                row.ContentLastModifiedAt,
                row.IdentityLastModifiedAt
            );
        }

        return table;
    }

    private static DataTable CreateRootTableRows(params (long DocumentId, int SchoolId)[] rows)
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("SchoolId", typeof(int));

        foreach (var row in rows)
        {
            table.Rows.Add(row.DocumentId, row.SchoolId);
        }

        return table;
    }

    private static DataTable CreateChildTableRows(
        params (long CollectionItemId, long SchoolDocumentId, int Ordinal, string City)[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("CollectionItemId", typeof(long));
        table.Columns.Add("School_DocumentId", typeof(long));
        table.Columns.Add("Ordinal", typeof(int));
        table.Columns.Add("City", typeof(string));

        foreach (var row in rows)
        {
            table.Rows.Add(row.CollectionItemId, row.SchoolDocumentId, row.Ordinal, row.City);
        }

        return table;
    }

    private static DataTable CreateDescriptorRowsTableWithDescriptorIdFirst(
        params (long DescriptorId, string Uri)[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("DescriptorId", typeof(long));
        table.Columns.Add("Uri", typeof(string));

        foreach (var row in rows)
        {
            table.Rows.Add(row.DescriptorId, row.Uri);
        }

        return table;
    }
}

internal sealed class RecordingDbConnection(RecordingDbCommand command) : DbConnection
{
    private ConnectionState _state = ConnectionState.Open;

    public RecordingDbCommand Command { get; } = command ?? throw new ArgumentNullException(nameof(command));

    [AllowNull]
    public override string ConnectionString { get; set; } = "Host=localhost;Database=test";

    public override string Database => "test";

    public override string DataSource => "recording";

    public override string ServerVersion => "1.0";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open() => _state = ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException();

    protected override DbCommand CreateDbCommand()
    {
        Command.Connection = this;
        return Command;
    }
}

internal sealed class RecordingDbCommand(DbDataReader reader) : DbCommand
{
    private readonly DbDataReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly RecordingDbParameterCollection _parameters = [];

    public int ExecuteReaderCount { get; private set; }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction { get; set; }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    public new RecordingDbConnection? Connection
    {
        get => DbConnection as RecordingDbConnection;
        set => DbConnection = value;
    }

    public override void Cancel() { }

    public override int ExecuteNonQuery() => throw new NotSupportedException();

    public override object? ExecuteScalar() => throw new NotSupportedException();

    public override void Prepare() { }

    protected override DbParameter CreateDbParameter() => new RecordingDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        ExecuteReaderCount++;
        return _reader;
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecuteReaderCount++;
        return Task.FromResult(_reader);
    }
}

internal sealed class RecordingDbParameterCollection : DbParameterCollection
{
    public List<DbParameter> Items { get; } = [];

    public override int Count => Items.Count;

    public override object SyncRoot => ((ICollection)Items).SyncRoot!;

    public override int Add(object value)
    {
        Items.Add((DbParameter)value);
        return Items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    public override void Clear() => Items.Clear();

    public override bool Contains(object value) => Items.Contains((DbParameter)value);

    public override bool Contains(string value) =>
        Items.Exists(parameter => parameter.ParameterName == value);

    public override void CopyTo(Array array, int index) => ((ICollection)Items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => Items.GetEnumerator();

    protected override DbParameter GetParameter(int index) => Items[index];

    protected override DbParameter GetParameter(string parameterName) =>
        Items.Single(parameter => parameter.ParameterName == parameterName);

    public override int IndexOf(object value) => Items.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName) =>
        Items.FindIndex(parameter => parameter.ParameterName == parameterName);

    public override void Insert(int index, object value) => Items.Insert(index, (DbParameter)value);

    public override void Remove(object value) => Items.Remove((DbParameter)value);

    public override void RemoveAt(int index) => Items.RemoveAt(index);

    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);

        if (index >= 0)
        {
            Items.RemoveAt(index);
        }
    }

    protected override void SetParameter(int index, DbParameter value) => Items[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);

        if (index >= 0)
        {
            Items[index] = value;
            return;
        }

        Items.Add(value);
    }
}

internal sealed class RecordingDbParameter : DbParameter
{
    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override object? Value { get; set; }

    public override bool SourceColumnNullMapping { get; set; }

    public override int Size { get; set; }

    public override void ResetDbType() { }
}

internal static class HydrationDescriptorResultTestHelper
{
    public static DataTableReader CreateReader(params DataTable[] tables)
    {
        var dataSet = new DataSet();

        foreach (var table in tables)
        {
            dataSet.Tables.Add(table);
        }

        return dataSet.CreateDataReader();
    }

    public static DataTable CreateDescriptorRowsTableWithUriFirst(
        params (string Uri, long DescriptorId)[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("Uri", typeof(string));
        table.Columns.Add("DescriptorId", typeof(long));

        foreach (var row in rows)
        {
            table.Rows.Add(row.Uri, row.DescriptorId);
        }

        return table;
    }
}
