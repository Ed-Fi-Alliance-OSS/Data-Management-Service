// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_MssqlDocumentCacheMaterializationDataStore
{
    [Test]
    public async Task It_opens_connections_from_the_materialization_target_key_data_store()
    {
        const string targetConnectionString = "Server=target;Database=dms;TrustServerCertificate=true";
        var dataStoreProvider = A.Fake<IDataStoreProvider>();
        var connection = new RecordingDbConnection(new RecordingDbCommand(CreateReader()));
        string? capturedConnectionString = null;
        var sut = new MssqlDocumentCacheMaterializationDataStore(
            dataStoreProvider,
            connectionString =>
            {
                capturedConnectionString = connectionString;
                return connection;
            },
            NullLogger<MssqlDocumentCacheMaterializationDataStore>.Instance
        );

        A.CallTo(() => dataStoreProvider.GetById(7, "tenant-a"))
            .Returns(
                new DataStore(
                    Id: 7,
                    DataStoreType: "test",
                    Name: "target",
                    ConnectionString: targetConnectionString,
                    RouteContext: []
                )
            );

        var result = await sut.ExecuteReaderAsync(
            CreateRequest(),
            new RelationalCommand("select [Value] from [dms].[TargetProbe]"),
            async (reader, cancellationToken) =>
            {
                await reader.ReadAsync(cancellationToken);
                return reader.GetRequiredFieldValue<int>("Value");
            }
        );

        result.Should().Be(42);
        capturedConnectionString.Should().Be(targetConnectionString);
        connection.OpenAsyncCallCount.Should().Be(1);
        connection.Command.CommandText.Should().Be("select [Value] from [dms].[TargetProbe]");
        A.CallTo(() => dataStoreProvider.GetById(7, "tenant-a")).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataStoreProvider.GetById(8, A<string?>._)).MustNotHaveHappened();
    }

    private static DocumentCacheMaterializationRequest CreateRequest()
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(
            new QualifiedResourceName("Ed-Fi", "TargetProbe")
        ) with
        {
            Key = new MappingSetKey("test-hash", SqlDialect.Mssql, "v1"),
        };

        return new DocumentCacheMaterializationRequest(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet
            ),
            documentId: 123L,
            selectedRequiredContentVersion: null,
            DocumentCacheMaterializationPurpose.Fixture,
            CancellationToken.None
        );
    }

    private static DbDataReader CreateReader()
    {
        var table = new DataTable();
        table.Columns.Add("Value", typeof(int));
        table.Rows.Add(42);
        return table.CreateDataReader();
    }
}
