// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_PostgresqlDocumentCacheMaterializationDataStore
{
    [Test]
    public void It_requires_the_materialization_target_context_to_carry_the_bound_data_store()
    {
        const string targetConnectionString = "Host=target;Database=dms";
        using var dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        var sut = new PostgresqlDocumentCacheMaterializationDataStore(
            dataSourceCache,
            NullLogger<PostgresqlDocumentCacheMaterializationDataStore>.Instance
        );

        var request = CreateRequest(targetConnectionString: targetConnectionString);

        var boundRequest = sut.BindToTargetDataStore(request);

        boundRequest.Should().BeSameAs(request);
        boundRequest
            .TargetContext.TargetDataStore.Should()
            .Be(new DocumentCacheMaterializationTargetDataStore(targetConnectionString));
    }

    [Test]
    public void It_rejects_unbound_target_data_store_contexts()
    {
        using var dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        var sut = new PostgresqlDocumentCacheMaterializationDataStore(
            dataSourceCache,
            NullLogger<PostgresqlDocumentCacheMaterializationDataStore>.Instance
        );

        var act = () => sut.BindToTargetDataStore(CreateRequest());

        act.Should().Throw<InvalidOperationException>().WithMessage("*target data store*bound once*");
    }

    [Test]
    public void It_rejects_mapping_sets_not_selected_for_the_postgresql_target()
    {
        using var dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        var sut = new PostgresqlDocumentCacheMaterializationDataStore(
            dataSourceCache,
            NullLogger<PostgresqlDocumentCacheMaterializationDataStore>.Instance
        );

        var act = () =>
            sut.BindToTargetDataStore(CreateRequest(SqlDialect.Mssql, targetConnectionString: "Host=target"));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*target context dialect*ResourceKey seed*");
    }

    private static DocumentCacheMaterializationRequest CreateRequest(
        SqlDialect dialect = SqlDialect.Pgsql,
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
}
