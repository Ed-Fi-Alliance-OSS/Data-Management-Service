// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Mssql;
using EdFi.DataManagementService.Core.External.Model;
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
        var connection = new RecordingDbConnection(new RecordingDbCommand(CreateReader()));
        string? capturedConnectionString = null;
        var sut = new MssqlDocumentCacheMaterializationDataStore(
            connectionString =>
            {
                capturedConnectionString = connectionString;
                return connection;
            },
            NullLogger<MssqlDocumentCacheMaterializationDataStore>.Instance
        );

        var request = sut.BindToTargetDataStore(
            CreateRequest(targetConnectionString: targetConnectionString)
        );

        var result = await sut.ExecuteReaderAsync(
            request,
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
    }

    [Test]
    public async Task It_binds_the_target_data_store_once_for_multiple_operations_in_one_attempt()
    {
        const string initialConnectionString = "Server=initial;Database=dms;TrustServerCertificate=true";
        const string replacementConnectionString =
            "Server=replacement;Database=dms;TrustServerCertificate=true";
        var connections = new Queue<RecordingDbConnection>([
            new RecordingDbConnection(new RecordingDbCommand(CreateReader())),
            new RecordingDbConnection(new RecordingDbCommand(CreateReader())),
        ]);
        List<string> capturedConnectionStrings = [];
        var selectedTargetConnectionString = initialConnectionString;
        var sut = new MssqlDocumentCacheMaterializationDataStore(
            connectionString =>
            {
                capturedConnectionStrings.Add(connectionString);
                return connections.Dequeue();
            },
            NullLogger<MssqlDocumentCacheMaterializationDataStore>.Instance
        );

        var request = sut.BindToTargetDataStore(
            CreateRequest(targetConnectionString: selectedTargetConnectionString)
        );
        selectedTargetConnectionString = replacementConnectionString;

        await sut.ExecuteReaderAsync(
            request,
            new RelationalCommand("select [Value] from [dms].[TargetProbe]"),
            async (reader, cancellationToken) =>
            {
                await reader.ReadAsync(cancellationToken);
                return reader.GetRequiredFieldValue<int>("Value");
            }
        );
        await sut.ExecuteReaderAsync(
            request,
            new RelationalCommand("select [Value] from [dms].[TargetProbe]"),
            async (reader, cancellationToken) =>
            {
                await reader.ReadAsync(cancellationToken);
                return reader.GetRequiredFieldValue<int>("Value");
            }
        );

        request
            .TargetContext.TargetDataStore.Should()
            .Be(new DocumentCacheMaterializationTargetDataStore(initialConnectionString));
        capturedConnectionStrings.Should().Equal(initialConnectionString, initialConnectionString);
        connections.Should().BeEmpty();
        selectedTargetConnectionString.Should().Be(replacementConnectionString);
    }

    [Test]
    public async Task It_rejects_unbound_target_data_store_contexts()
    {
        var sut = new MssqlDocumentCacheMaterializationDataStore(
            _ => throw new AssertionException("Connection factory should not be called."),
            NullLogger<MssqlDocumentCacheMaterializationDataStore>.Instance
        );

        Func<Task> act = () =>
            sut.ExecuteReaderAsync(
                CreateRequest(),
                new RelationalCommand("select [Value] from [dms].[TargetProbe]"),
                (_, _) => Task.FromResult(42)
            );

        var exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Subject.Single();
        exception.Message.Should().Contain("target data store").And.Contain("bound once");
    }

    [Test]
    public async Task It_rejects_mapping_sets_not_selected_for_the_mssql_target()
    {
        var sut = new MssqlDocumentCacheMaterializationDataStore(
            _ => throw new AssertionException("Connection factory should not be called."),
            NullLogger<MssqlDocumentCacheMaterializationDataStore>.Instance
        );

        Func<Task> act = () =>
            sut.ExecuteReaderAsync(
                CreateRequest(SqlDialect.Pgsql, targetConnectionString: "Server=target"),
                new RelationalCommand("select [Value] from [dms].[TargetProbe]"),
                (_, _) => Task.FromResult(42)
            );

        var exception = (await act.Should().ThrowAsync<InvalidOperationException>()).Subject.Single();
        exception.Message.Should().Contain("target context dialect").And.Contain("ResourceKey seed");
    }

    private static DocumentCacheMaterializationRequest CreateRequest(
        SqlDialect dialect = SqlDialect.Mssql,
        string? targetConnectionString = null
    )
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(
            new QualifiedResourceName("Ed-Fi", "TargetProbe")
        ) with
        {
            Key = new MappingSetKey("test-hash", dialect, "v1"),
        };
        var targetKey = new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7));
        var targetContext = targetConnectionString is null
            ? new DocumentCacheMaterializationTargetContext(
                targetKey,
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
            )
            : new DocumentCacheMaterializationTargetContext(
                targetKey,
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated,
                targetConnectionString
            );

        return new DocumentCacheMaterializationRequest(
            targetContext,
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
