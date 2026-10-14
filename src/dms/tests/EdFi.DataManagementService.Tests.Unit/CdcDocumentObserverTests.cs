// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FakeItEasy;
using FluentAssertions;
using Npgsql;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture(CdcProvider.Postgresql, "Student")]
[TestFixture(CdcProvider.Postgresql, "SchoolTypeDescriptor")]
[TestFixture(CdcProvider.SqlServer, "Student")]
[TestFixture(CdcProvider.SqlServer, "SchoolTypeDescriptor")]
public class Given_CdcDocumentObserver_Metadata(CdcProvider provider, string resourceName)
{
    private readonly Guid _uuid = Guid.Parse("9622f938-2c1a-4f99-9bc4-10970b1c2649");
    private readonly DateTime _timestamp = new(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private DbCommand _command = null!;
    private CdcDocumentObserver _observer = null!;
    private DataTable _rows = null!;
    private IReadOnlyList<CdcSourceDocument> _result = null!;

    [SetUp]
    public async Task Setup()
    {
        _rows = new();
        foreach (
            Type type in new[]
            {
                typeof(long),
                typeof(Guid),
                typeof(short),
                typeof(long),
                typeof(DateTime),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(string),
            }
        )
        {
            _rows.Columns.Add($"column{_rows.Columns.Count}", type);
        }
        _rows.Rows.Add(
            5000000001L,
            _uuid,
            (short)73,
            901L,
            _timestamp,
            "schema-hash",
            "Ed-Fi",
            resourceName,
            "5.2.0"
        );
        _command = A.Fake<DbCommand>();
        using var parameters = new NpgsqlCommand();
        A.CallTo(_command)
            .Where(call => call.Method.Name == "get_DbParameterCollection")
            .WithReturnType<DbParameterCollection>()
            .Returns(parameters.Parameters);
        A.CallTo(_command)
            .Where(call => call.Method.Name == "CreateDbParameter")
            .WithReturnType<DbParameter>()
            .ReturnsLazily(() => new NpgsqlParameter());
        A.CallTo(_command)
            .Where(call => call.Method.Name == "ExecuteDbDataReaderAsync")
            .WithReturnType<Task<DbDataReader>>()
            .ReturnsLazily(() => Task.FromResult<DbDataReader>(_rows.CreateDataReader()));
        var connection = A.Fake<DbConnection>();
        A.CallTo(connection)
            .Where(call => call.Method.Name == "CreateDbCommand")
            .WithReturnType<DbCommand>()
            .Returns(_command);
        _observer = new(provider, () => connection);
        _result = await _observer.ReadSourceAsync(_uuid, CancellationToken.None);
    }

    [TearDown]
    public void Teardown()
    {
        _command.Dispose();
        _rows.Dispose();
    }

    [Test]
    public void It_reads_owner_timestamp_and_document_metadata_in_one_resource_aware_command()
    {
        string sql = _command.CommandText.Replace('[', '"').Replace(']', '"');
        sql.Should().Contain("d.\"ContentVersion\"").And.NotContain("d.\"ContentLastModifiedAt\"");
        sql.Should()
            .Contain("student.\"ContentLastModifiedAt\"")
            .And.Contain("descriptor.\"ContentLastModifiedAt\"");
        sql.Should()
            .Contain("LEFT JOIN \"edfi\".\"Student\" student ON student.\"DocumentId\" = d.\"DocumentId\"");
        sql.Should()
            .Contain(
                "LEFT JOIN \"dms\".\"Descriptor\" descriptor ON descriptor.\"DocumentId\" = d.\"DocumentId\""
            );
        sql.Should().NotContain("\"DescriptorId\"");
        sql.Should()
            .Contain("rk.\"ResourceName\" = 'Student'")
            .And.Contain("rk.\"ResourceName\" = 'SchoolTypeDescriptor'");
        _command.Parameters.Cast<DbParameter>().Should().ContainSingle().Which.Value.Should().Be(_uuid);
        A.CallTo(_command)
            .Where(call => call.Method.Name == "ExecuteDbDataReaderAsync")
            .MustHaveHappenedOnceExactly();
        _result
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new CdcSourceDocument(
                    5000000001L,
                    _uuid,
                    73,
                    901,
                    new DateTimeOffset(_timestamp),
                    "schema-hash",
                    "Ed-Fi",
                    resourceName,
                    "5.2.0"
                )
            );
    }

    [Test]
    public async Task It_does_not_report_a_surviving_document_with_missing_owner_as_deleted()
    {
        _rows.Rows[0][4] = DBNull.Value;
        Func<Task> act = () => _observer.ReadSourceAsync(_uuid, CancellationToken.None);
        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Unexpected database timestamp type.");
    }

    [Test]
    public async Task It_reports_an_absent_document_as_missing()
    {
        _rows.Rows.Clear();
        (await _observer.ReadSourceAsync(_uuid, CancellationToken.None)).Should().BeEmpty();
    }
}
