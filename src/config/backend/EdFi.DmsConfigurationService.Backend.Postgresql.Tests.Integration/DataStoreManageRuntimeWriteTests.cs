// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Text.Json.Nodes;
using Dapper;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Postgresql;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using static EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.DataStoreProvisioning.DmsProvisioningTestServer;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public partial class Given_data_store_namespace_reservations
{
    [Test, Category("DataStoreManageRuntimeWrite"), Category("DmsProvisioning")]
    public Task It_writes_a_descriptor_with_the_discovered_managed_runtime_principal() =>
        VerifyLiveRecovery("Minimal", false, runtimeWrite: true);

    private static async Task VerifyManagedRuntimeWrite(
        DataStore store,
        string login,
        string schemaHash,
        string mappingVersion
    )
    {
        // Operator setup only: grant access on the fixture-owned restored target. CMS does not grant.
        // The existing live check already proves this is the discovered non-admin credential.
        await using (
            var admin = new NpgsqlConnection(
                new NpgsqlConnectionStringBuilder(AdminConnectionString!)
                {
                    Database = new NpgsqlConnectionStringBuilder(store.ConnectionString).Database,
                }.ConnectionString
            )
        )
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync(
                $"""GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA dms TO "{login}"; GRANT USAGE ON ALL SEQUENCES IN SCHEMA dms TO "{login}";"""
            );
        }

        await using var connection = new NpgsqlConnection(store.ConnectionString);
        await connection.OpenAsync();
        var resource = new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor");
        var keyRow = await connection.QuerySingleAsync<(short Id, string Version)>(
            """SELECT "ResourceKeyId", "ResourceVersion" FROM dms."ResourceKey" WHERE "ProjectName" = 'Ed-Fi' AND "ResourceName" = 'SchoolTypeDescriptor';"""
        );
        var key = new ResourceKeyEntry(keyRow.Id, resource, keyRow.Version, false);
        // Reuse the DMS integration descriptor model, binding its key/hash to the genuine target.
        // This qualifies a backend descriptor write, not full API-schema loading or resource HTTP.
        var seed = ReferenceResolverIntegrationFixture.CreateDefault().CreateMappingSet(SqlDialect.Pgsql);
        var mapping = seed with
        {
            Key = new(schemaHash, SqlDialect.Pgsql, mappingVersion),
            ResourceKeyIdByResource = new Dictionary<QualifiedResourceName, short> { [resource] = keyRow.Id },
            ResourceKeyById = new Dictionary<short, ResourceKeyEntry> { [keyRow.Id] = key },
            Model = seed.Model with
            {
                ConcreteResourcesInNameOrder =
                [
                    seed.Model.ConcreteResourcesInNameOrder.Single(model =>
                        model.ResourceKey.Resource == resource
                    ) with
                    {
                        ResourceKey = key,
                    },
                ],
            },
        };
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new DeadlockRetrySettings());
        services.Configure<EdFi.DataManagementService.Backend.DatabaseOptions>(options =>
            options.IsolationLevel = IsolationLevel.ReadCommitted
        );
        services.AddSelectedDataStoreIntegrationTestProvider();
        services.AddTestReadableProfileProjector();
        services.AddPostgresqlDatastore(new ConfigurationBuilder().Build());
        services.AddPostgresqlReferenceResolver();
        services.AddSingleton(A.Fake<IDocumentCacheProjectionSupervisor>());
        var registry = A.Fake<IDocumentCacheTargetRegistry>();
        A.CallTo(() => registry.CurrentSnapshot).Returns(new([], DateTimeOffset.UtcNow));
        A.CallTo(() => registry.CurrentRuntimeSnapshot).Returns(new([], DateTimeOffset.UtcNow));
        services.AddSingleton(registry);
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true }
        );
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IDataStoreSelection>().SetSelectedDataStore(store);
        var handler = scope.ServiceProvider.GetRequiredService<IDescriptorWriteHandler>();
        var uuid = new DocumentUuid(Guid.NewGuid());
        string code = "Managed" + Guid.NewGuid().ToString("N");
        const string descriptorNamespace = "uri://dms1439.example/SchoolTypeDescriptor";
        var request = new DescriptorWriteRequest(
            mapping,
            resource,
            new JsonObject
            {
                ["namespace"] = descriptorNamespace,
                ["codeValue"] = code,
                ["shortDescription"] = "Managed runtime write",
            },
            uuid,
            new TraceId("managed-runtime-write")
        );
        var result = await handler.HandlePostWithSamePolicyForCreateAndUpdateAsync(request);
        result.Should().BeOfType<UpsertResult.InsertSuccess>().Which.NewDocumentUuid.Should().Be(uuid);
        // A fresh connection sees the committed resource and tracking row; no raw INSERT substitutes
        // for the production handler/session/transaction in this qualification.
        await using var observer = new NpgsqlConnection(store.ConnectionString);
        await observer.OpenAsync();
        var written = await observer.QuerySingleAsync<(
            short Key,
            string Namespace,
            string Code,
            string Description,
            long Version
        )>(
            """
            SELECT doc."ResourceKeyId", descriptor."Namespace", descriptor."CodeValue",
                   descriptor."ShortDescription", doc."ContentVersion"
            FROM dms."Document" doc
            JOIN dms."Descriptor" descriptor ON descriptor."DocumentId" = doc."DocumentId"
            WHERE doc."DocumentUuid" = @Uuid;
            """,
            new { Uuid = uuid.Value }
        );
        written.Key.Should().Be(keyRow.Id);
        written.Namespace.Should().Be(descriptorNamespace);
        written.Code.Should().Be(code);
        written.Description.Should().Be("Managed runtime write");
        written.Version.Should().BePositive();
    }
}
