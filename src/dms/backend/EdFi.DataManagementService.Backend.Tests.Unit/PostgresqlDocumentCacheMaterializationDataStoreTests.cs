// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_PostgresqlDocumentCacheMaterializationDataStore
{
    [Test]
    public void It_binds_connections_from_the_materialization_target_key_data_store()
    {
        const string targetConnectionString = "Host=target;Database=dms";
        var dataStoreProvider = A.Fake<IDataStoreProvider>();
        using var dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        var sut = new PostgresqlDocumentCacheMaterializationDataStore(
            dataStoreProvider,
            dataSourceCache,
            NullLogger<PostgresqlDocumentCacheMaterializationDataStore>.Instance
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

        var request = sut.BindToTargetDataStore(CreateRequest());

        request
            .TargetContext.TargetDataStore.Should()
            .Be(new DocumentCacheMaterializationTargetDataStore(targetConnectionString));
        A.CallTo(() => dataStoreProvider.GetById(7, "tenant-a")).MustHaveHappenedOnceExactly();
        A.CallTo(() => dataStoreProvider.GetById(8, A<string?>._)).MustNotHaveHappened();
    }

    [Test]
    public void It_rejects_mapping_sets_not_selected_for_the_postgresql_target_before_resolving_the_data_store()
    {
        var dataStoreProvider = A.Fake<IDataStoreProvider>();
        using var dataSourceCache = new NpgsqlDataSourceCache(NullLogger<NpgsqlDataSourceCache>.Instance);
        var sut = new PostgresqlDocumentCacheMaterializationDataStore(
            dataStoreProvider,
            dataSourceCache,
            NullLogger<PostgresqlDocumentCacheMaterializationDataStore>.Instance
        );

        var act = () => sut.BindToTargetDataStore(CreateRequest(SqlDialect.Mssql));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*target context dialect*ResourceKey seed*");
        A.CallTo(() => dataStoreProvider.GetById(A<long>._, A<string?>._)).MustNotHaveHappened();
    }

    private static DocumentCacheMaterializationRequest CreateRequest(SqlDialect dialect = SqlDialect.Pgsql)
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(
            new QualifiedResourceName("Ed-Fi", "TargetProbe")
        ) with
        {
            Key = new MappingSetKey("test-hash", dialect, "v1"),
        };

        return new DocumentCacheMaterializationRequest(
            new DocumentCacheMaterializationTargetContext(
                new DocumentCacheProjectionTargetKey("tenant-a", new DataStoreId(7)),
                mappingSet,
                DocumentCacheMaterializationTargetValidation.EffectiveSchemaAndResourceKeySeedValidated
            ),
            documentId: 123L,
            selectedRequiredContentVersion: null,
            DocumentCacheMaterializationPurpose.Fixture,
            CancellationToken.None
        );
    }
}
