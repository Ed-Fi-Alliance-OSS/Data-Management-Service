// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_MssqlRelationalCommandExecutor
{
    [Test]
    public async Task It_opens_a_connection_for_the_selected_dms_instance_and_executes_the_relational_command()
    {
        const string connectionString =
            "Server=localhost;Database=test;User Id=sa;Password=TestPassword1!;TrustServerCertificate=true";

        var requestConnectionProvider = A.Fake<IRequestConnectionProvider>();
        var documentReferentialId = new ReferentialId(Guid.NewGuid());
        var descriptorReferentialId = new ReferentialId(Guid.NewGuid());
        var connection = new RecordingDbConnection(
            new RecordingDbCommand(
                CreateReader(
                    CreateLookupTable(
                        (
                            documentReferentialId.Value,
                            101L,
                            (short)11,
                            (short)11,
                            false,
                            "$$.schoolId=255901"
                        ),
                        (
                            descriptorReferentialId.Value,
                            202L,
                            (short)12,
                            (short)12,
                            true,
                            "$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative"
                        )
                    )
                )
            )
        );

        A.CallTo(() => requestConnectionProvider.GetRequestConnection())
            .Returns(new RequestConnection(new DmsInstanceId(7), connectionString));

        var sut = new MssqlRelationalCommandExecutor(
            requestConnectionProvider,
            selectedConnectionString =>
            {
                selectedConnectionString.Should().Be(connectionString);
                return connection;
            },
            NullLogger<MssqlRelationalCommandExecutor>.Instance
        );

        var result = await sut.ExecuteReaderAsync(
            new RelationalCommand(
                "select * from dms.lookup where referential_id in (@p0, @p1)",
                [
                    new RelationalParameter("@p0", documentReferentialId.Value),
                    new RelationalParameter("@p1", descriptorReferentialId.Value),
                ]
            ),
            ReferenceLookupResultReader.ReadAsync
        );

        A.CallTo(() => requestConnectionProvider.GetRequestConnection()).MustHaveHappenedOnceExactly();
        connection.OpenAsyncCallCount.Should().Be(1);
        connection.LastOpenAsyncCancellationToken.Should().Be(CancellationToken.None);
        connection.CreateCommandCallCount.Should().Be(1);
        connection
            .Command.CommandText.Should()
            .Be("select * from dms.lookup where referential_id in (@p0, @p1)");
        connection.Command.Parameters.Should().HaveCount(2);
        connection.Command.Parameters[0].ParameterName.Should().Be("@p0");
        connection.Command.Parameters[0].Value.Should().Be(documentReferentialId.Value);
        connection.Command.Parameters[1].ParameterName.Should().Be("@p1");
        connection.Command.Parameters[1].Value.Should().Be(descriptorReferentialId.Value);
        connection.Command.ExecuteReaderCallCount.Should().Be(1);
        connection.Command.DisposeCallCount.Should().Be(1);
        connection.DisposeCallCount.Should().Be(1);
        result
            .Should()
            .BeEquivalentTo([
                new ReferenceLookupResult(documentReferentialId, 101L, 11, 11, false, "$$.schoolId=255901"),
                new ReferenceLookupResult(
                    descriptorReferentialId,
                    202L,
                    12,
                    12,
                    true,
                    "$$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative"
                ),
            ]);
    }

    [Test]
    public async Task It_applies_parameter_configuration_and_converts_null_values_to_dbnull()
    {
        var connection = new RecordingDbConnection(new RecordingDbCommand(CreateReader(CreateLookupTable())));
        var sut = new MssqlRelationalCommandExecutor(
            _ => Task.FromResult<DbConnection>(connection),
            NullLogger<MssqlRelationalCommandExecutor>.Instance
        );

        var result = await sut.ExecuteReaderAsync(
            new RelationalCommand(
                "select 1",
                [
                    new RelationalParameter(
                        "@nullable",
                        null,
                        parameter =>
                        {
                            parameter.DbType = DbType.Guid;
                            parameter.Direction = ParameterDirection.InputOutput;
                        }
                    ),
                ]
            ),
            static (_, _) => Task.FromResult("done")
        );

        result.Should().Be("done");
        connection.Command.Parameters.Should().ContainSingle();
        connection.Command.Parameters[0].ParameterName.Should().Be("@nullable");
        connection.Command.Parameters[0].Value.Should().Be(DBNull.Value);
        connection.Command.Parameters[0].DbType.Should().Be(DbType.Guid);
        connection.Command.Parameters[0].Direction.Should().Be(ParameterDirection.InputOutput);
    }

    [Test]
    public async Task It_preserves_the_missing_connection_string_failure_from_the_request_connection_provider()
    {
        var requestConnectionProvider = A.Fake<IRequestConnectionProvider>();
        A.CallTo(() => requestConnectionProvider.GetRequestConnection())
            .Throws(
                new InvalidOperationException(
                    "Selected DMS instance '7' does not have a valid connection string."
                )
            );

        var sut = new MssqlRelationalCommandExecutor(
            requestConnectionProvider,
            _ => throw new AssertionException("Connection factory should not be called."),
            NullLogger<MssqlRelationalCommandExecutor>.Instance
        );

        Func<Task> act = () =>
            sut.ExecuteReaderAsync(
                new RelationalCommand("select 1", []),
                static (_, _) => Task.FromResult("done")
            );

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Selected DMS instance '7' does not have a valid connection string.");
        A.CallTo(() => requestConnectionProvider.GetRequestConnection()).MustHaveHappenedOnceExactly();
    }

    private static DbDataReader CreateReader(params DataTable[] resultSets) =>
        resultSets.Length switch
        {
            0 => CreateLookupTable().CreateDataReader(),
            1 => resultSets[0].CreateDataReader(),
            _ => CreateDataSet(resultSets).CreateDataReader(),
        };

    private static DataSet CreateDataSet(params DataTable[] tables)
    {
        var dataSet = new DataSet();

        foreach (var table in tables)
        {
            dataSet.Tables.Add(table);
        }

        return dataSet;
    }

    private static DataTable CreateLookupTable(
        params (
            Guid ReferentialId,
            long DocumentId,
            short ResourceKeyId,
            short ReferentialIdentityResourceKeyId,
            bool IsDescriptor,
            string? VerificationIdentityKey
        )[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("ReferentialId", typeof(Guid));
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("ResourceKeyId", typeof(short));
        table.Columns.Add("ReferentialIdentityResourceKeyId", typeof(short));
        table.Columns.Add("IsDescriptor", typeof(bool));
        table.Columns.Add("VerificationIdentityKey", typeof(string));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.ReferentialId,
                row.DocumentId,
                row.ResourceKeyId,
                row.ReferentialIdentityResourceKeyId,
                row.IsDescriptor,
                row.VerificationIdentityKey
            );
        }

        return table;
    }
}
