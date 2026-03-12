// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Old.Postgresql.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Old.Postgresql.Tests.Integration;

file sealed class TestHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

[TestFixture]
public class Given_PostgresqlRuntimeInstanceMappingValidator_Against_A_Provisioned_Database : DatabaseTestBase
{
    private MappingSet _mappingSet = null!;
    private DmsInstance _instance = null!;
    private PostgresqlValidatedResourceKeyMapCache _validatedResourceKeyMapCache = null!;
    private NpgsqlDataSourceCache _dataSourceCache = null!;
    private PostgresqlRuntimeInstanceMappingValidator _validator = null!;

    [SetUp]
    public async Task Setup()
    {
        _mappingSet = CreateMappingSet();
        _instance = new DmsInstance(
            Id: 42,
            InstanceType: "test",
            InstanceName: "IntegrationDatabase",
            ConnectionString: Configuration.DatabaseConnectionString,
            RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>()
        );
        _validatedResourceKeyMapCache = new PostgresqlValidatedResourceKeyMapCache();
        _dataSourceCache = new NpgsqlDataSourceCache(
            new TestHostApplicationLifetime(),
            NullLogger<NpgsqlDataSourceCache>.Instance
        );
        _validator = new PostgresqlRuntimeInstanceMappingValidator(
            CreateInstanceProvider((null, _instance)),
            new PostgresqlRuntimeDatabaseMetadataReader(_dataSourceCache),
            _validatedResourceKeyMapCache,
            NullLogger<PostgresqlRuntimeInstanceMappingValidator>.Instance
        );

        await ResetProvisioningTablesAsync();
    }

    [TearDown]
    public void TearDownValidator()
    {
        _dataSourceCache.Dispose();
    }

    [Test]
    public async Task It_succeeds_when_the_database_fingerprint_matches_the_compiled_mapping_set()
    {
        await SeedMatchingRuntimeFingerprintAsync();

        var summary = await _validator.ValidateLoadedInstancesAsync(_mappingSet, CancellationToken.None);

        summary.InstanceCount.Should().Be(1);
        summary.ValidatedDatabaseCount.Should().Be(1);
        summary.ReusedValidationCount.Should().Be(0);
        _validatedResourceKeyMapCache.Count.Should().Be(1);
        _validatedResourceKeyMapCache
            .TryGet(_instance.ConnectionString!, out var cachedMaps)
            .Should()
            .BeTrue();
        cachedMaps.MappingSetKey.Should().Be(_mappingSet.Key);
    }

    [Test]
    public async Task It_fails_fast_when_the_seeded_fingerprint_and_resourcekey_rows_are_corrupted()
    {
        await SeedMatchingRuntimeFingerprintAsync();
        await CorruptSeededFingerprintAndResourceKeysAsync();

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            _validator.ValidateLoadedInstancesAsync(_mappingSet, CancellationToken.None)
        )!;

        exception!.Message.Should().Contain(_instance.InstanceName);
        exception.Message.Should().Contain(_mappingSet.Key.EffectiveSchemaHash);
        exception.Message.Should().Contain("ResourceKeySeedHash");
        exception.Message.Should().Contain("CorruptedStudent");
    }

    private async Task ResetProvisioningTablesAsync()
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS dms."EffectiveSchema" (
                "EffectiveSchemaSingletonId" smallint NOT NULL PRIMARY KEY,
                "ApiSchemaFormatVersion" character varying(64) NOT NULL,
                "EffectiveSchemaHash" character varying(64) NOT NULL,
                "ResourceKeyCount" smallint NOT NULL,
                "ResourceKeySeedHash" bytea NOT NULL,
                "AppliedAt" timestamp without time zone NOT NULL DEFAULT CURRENT_TIMESTAMP,
                CONSTRAINT "CK_EffectiveSchema_Singleton" CHECK ("EffectiveSchemaSingletonId" = 1),
                CONSTRAINT "CK_EffectiveSchema_ResourceKeySeedHash_Length" CHECK (octet_length("ResourceKeySeedHash") = 32)
            );
            CREATE TABLE IF NOT EXISTS dms."ResourceKey" (
                "ResourceKeyId" smallint NOT NULL PRIMARY KEY,
                "ProjectName" character varying(256) NOT NULL,
                "ResourceName" character varying(256) NOT NULL,
                "ResourceVersion" character varying(32) NOT NULL,
                CONSTRAINT "UX_ResourceKey_ProjectName_ResourceName" UNIQUE ("ProjectName", "ResourceName")
            );
            DELETE FROM dms."EffectiveSchema";
            DELETE FROM dms."ResourceKey";
            """;

        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedMatchingRuntimeFingerprintAsync()
    {
        var effectiveSchema = _mappingSet.Model.EffectiveSchema;

        await using var connection = await DataSource!.OpenConnectionAsync();

        await using (var effectiveSchemaCommand = connection.CreateCommand())
        {
            effectiveSchemaCommand.CommandText = """
                INSERT INTO dms."EffectiveSchema" (
                    "EffectiveSchemaSingletonId",
                    "ApiSchemaFormatVersion",
                    "EffectiveSchemaHash",
                    "ResourceKeyCount",
                    "ResourceKeySeedHash"
                )
                VALUES (1, @apiSchemaFormatVersion, @effectiveSchemaHash, @resourceKeyCount, @resourceKeySeedHash)
                """;
            effectiveSchemaCommand.Parameters.AddWithValue(
                "@apiSchemaFormatVersion",
                effectiveSchema.ApiSchemaFormatVersion
            );
            effectiveSchemaCommand.Parameters.AddWithValue(
                "@effectiveSchemaHash",
                effectiveSchema.EffectiveSchemaHash
            );
            effectiveSchemaCommand.Parameters.AddWithValue(
                "@resourceKeyCount",
                effectiveSchema.ResourceKeyCount
            );
            effectiveSchemaCommand.Parameters.AddWithValue(
                "@resourceKeySeedHash",
                effectiveSchema.ResourceKeySeedHash
            );

            await effectiveSchemaCommand.ExecuteNonQueryAsync();
        }

        foreach (var resourceKeyEntry in effectiveSchema.ResourceKeysInIdOrder)
        {
            await using var resourceKeyCommand = connection.CreateCommand();
            resourceKeyCommand.CommandText = """
                INSERT INTO dms."ResourceKey" (
                    "ResourceKeyId",
                    "ProjectName",
                    "ResourceName",
                    "ResourceVersion"
                )
                VALUES (@resourceKeyId, @projectName, @resourceName, @resourceVersion)
                """;
            resourceKeyCommand.Parameters.AddWithValue("@resourceKeyId", resourceKeyEntry.ResourceKeyId);
            resourceKeyCommand.Parameters.AddWithValue("@projectName", resourceKeyEntry.Resource.ProjectName);
            resourceKeyCommand.Parameters.AddWithValue(
                "@resourceName",
                resourceKeyEntry.Resource.ResourceName
            );
            resourceKeyCommand.Parameters.AddWithValue("@resourceVersion", resourceKeyEntry.ResourceVersion);

            await resourceKeyCommand.ExecuteNonQueryAsync();
        }
    }

    private async Task CorruptSeededFingerprintAndResourceKeysAsync()
    {
        await using var connection = await DataSource!.OpenConnectionAsync();

        await using (var fingerprintCommand = connection.CreateCommand())
        {
            fingerprintCommand.CommandText = """
                UPDATE dms."EffectiveSchema"
                SET "ResourceKeySeedHash" = @badSeedHash
                WHERE "EffectiveSchemaSingletonId" = 1
                """;
            fingerprintCommand.Parameters.AddWithValue(
                "@badSeedHash",
                Enumerable.Repeat((byte)0xFF, 32).ToArray()
            );

            await fingerprintCommand.ExecuteNonQueryAsync();
        }

        await using (var resourceKeyCommand = connection.CreateCommand())
        {
            resourceKeyCommand.CommandText = """
                UPDATE dms."ResourceKey"
                SET "ResourceName" = 'CorruptedStudent'
                WHERE "ResourceKeyId" = 1
                """;

            await resourceKeyCommand.ExecuteNonQueryAsync();
        }
    }

    private static MappingSet CreateMappingSet()
    {
        var resourceKeyEntry = new ResourceKeyEntry(
            ResourceKeyId: 1,
            Resource: new QualifiedResourceName("Ed-Fi", "Student"),
            ResourceVersion: "5.0.0",
            IsAbstractResource: false
        );

        var effectiveSchema = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "rmv-test",
            EffectiveSchemaHash: "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ResourceKeyCount: 1,
            ResourceKeySeedHash: Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(),
            SchemaComponentsInEndpointOrder:
            [
                new SchemaComponentInfo("ed-fi", "Ed-Fi", "5.0.0", false, "project-hash"),
            ],
            ResourceKeysInIdOrder: [resourceKeyEntry]
        );

        return new MappingSet(
            Key: new MappingSetKey(
                effectiveSchema.EffectiveSchemaHash,
                SqlDialect.Pgsql,
                effectiveSchema.RelationalMappingVersion
            ),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: effectiveSchema,
                Dialect: SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder:
                [
                    new ProjectSchemaInfo("ed-fi", "Ed-Fi", "5.0.0", false, new DbSchemaName("edfi")),
                ],
                ConcreteResourcesInNameOrder: [],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [resourceKeyEntry.Resource] = resourceKeyEntry.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [resourceKeyEntry.ResourceKeyId] = resourceKeyEntry,
            }
        );
    }

    private static IDmsInstanceProvider CreateInstanceProvider(
        params (string? Tenant, DmsInstance Instance)[] entries
    )
    {
        var provider = A.Fake<IDmsInstanceProvider>();
        var entriesByTenant = entries
            .GroupBy(entry => entry.Tenant ?? string.Empty)
            .ToDictionary(
                grouping => grouping.Key,
                grouping => (IReadOnlyList<DmsInstance>)grouping.Select(entry => entry.Instance).ToArray(),
                StringComparer.Ordinal
            );

        A.CallTo(() => provider.GetLoadedTenantKeys()).Returns(entriesByTenant.Keys.ToArray());
        A.CallTo(() => provider.GetAll(A<string?>.Ignored))
            .ReturnsLazily(
                (string? tenant) =>
                    entriesByTenant.TryGetValue(tenant ?? string.Empty, out var instances)
                        ? instances
                        : Array.Empty<DmsInstance>()
            );

        return provider;
    }
}
