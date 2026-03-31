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
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

public abstract class PostgresqlRuntimeInstanceMappingValidatorTests
{
    protected const string ConnectionString =
        "Host=localhost;Database=dms-instance-1;Username=test;Password=test";

    protected static MappingSet CreateMappingSet()
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
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    protected static DmsInstance CreateInstance(
        long id,
        string name,
        string connectionString = ConnectionString
    )
    {
        return new DmsInstance(
            Id: id,
            InstanceType: "test",
            InstanceName: name,
            ConnectionString: connectionString,
            RouteContext: new Dictionary<RouteQualifierName, RouteQualifierValue>()
        );
    }

    protected static IDmsInstanceProvider CreateInstanceProvider(
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

    private protected static PostgresqlDatabaseFingerprint CreateMatchingFingerprint(MappingSet mappingSet)
    {
        var effectiveSchema = mappingSet.Model.EffectiveSchema;

        return new PostgresqlDatabaseFingerprint(
            effectiveSchema.ApiSchemaFormatVersion,
            effectiveSchema.EffectiveSchemaHash,
            effectiveSchema.ResourceKeyCount,
            effectiveSchema.ResourceKeySeedHash.ToArray()
        );
    }

    protected static void AssertFailureContext(string message, DmsInstance instance, MappingSet mappingSet)
    {
        message.Should().Contain(instance.InstanceName);
        message.Should().Contain(instance.Id.ToString());
        message.Should().Contain(mappingSet.Key.EffectiveSchemaHash);
        message.Should().Contain(mappingSet.Key.RelationalMappingVersion);
        message.Should().Contain(mappingSet.Key.Dialect.ToString());
    }
}

[TestFixture]
public class Given_PostgresqlRuntimeInstanceMappingValidator_With_Matching_Fingerprints
    : PostgresqlRuntimeInstanceMappingValidatorTests
{
    private MappingSet _mappingSet = null!;
    private IDmsInstanceProvider _instanceProvider = null!;
    private IPostgresqlRuntimeDatabaseMetadataReader _databaseMetadataReader = null!;
    private PostgresqlValidatedResourceKeyMapCache _validatedResourceKeyMapCache = null!;
    private PostgresqlRuntimeInstanceMappingValidator _validator = null!;
    private PostgresqlRuntimeInstanceMappingValidationSummary _summary = null!;

    [SetUp]
    public async Task Setup()
    {
        _mappingSet = CreateMappingSet();
        _instanceProvider = CreateInstanceProvider(
            (null, CreateInstance(1, "Alpha")),
            (null, CreateInstance(2, "Beta"))
        );
        _databaseMetadataReader = A.Fake<IPostgresqlRuntimeDatabaseMetadataReader>();
        _validatedResourceKeyMapCache = new PostgresqlValidatedResourceKeyMapCache();
        _validator = new PostgresqlRuntimeInstanceMappingValidator(
            _instanceProvider,
            _databaseMetadataReader,
            _validatedResourceKeyMapCache,
            NullLogger<PostgresqlRuntimeInstanceMappingValidator>.Instance
        );

        A.CallTo(() =>
                _databaseMetadataReader.ReadFingerprintAsync(ConnectionString, A<CancellationToken>.Ignored)
            )
            .Returns(
                new PostgresqlDatabaseFingerprintReadResult.Success(CreateMatchingFingerprint(_mappingSet))
            );

        _summary = await _validator.ValidateLoadedInstancesAsync(_mappingSet, CancellationToken.None);
    }

    [Test]
    public void It_caches_validated_resource_key_maps_per_connection_string()
    {
        _summary.InstanceCount.Should().Be(2);
        _summary.ValidatedDatabaseCount.Should().Be(1);
        _summary.ReusedValidationCount.Should().Be(1);
        _validatedResourceKeyMapCache.Count.Should().Be(1);
        _validatedResourceKeyMapCache.TryGet(ConnectionString, out var cachedMaps).Should().BeTrue();
        cachedMaps.MappingSetKey.Should().Be(_mappingSet.Key);
        cachedMaps.ResourceKeyIdByResource.Should().BeEquivalentTo(_mappingSet.ResourceKeyIdByResource);
        cachedMaps.ResourceKeyById.Should().BeEquivalentTo(_mappingSet.ResourceKeyById);
        A.CallTo(() =>
                _databaseMetadataReader.ReadFingerprintAsync(ConnectionString, A<CancellationToken>.Ignored)
            )
            .MustHaveHappenedOnceExactly();
        A.CallTo(() =>
                _databaseMetadataReader.ReadResourceKeysAsync(ConnectionString, A<CancellationToken>.Ignored)
            )
            .MustNotHaveHappened();
    }
}

[TestFixture]
public class Given_PostgresqlRuntimeInstanceMappingValidator_With_EffectiveSchemaHash_Mismatch
    : PostgresqlRuntimeInstanceMappingValidatorTests
{
    private Exception _exception = null!;
    private MappingSet _mappingSet = null!;
    private DmsInstance _instance = null!;

    [SetUp]
    public async Task Setup()
    {
        _mappingSet = CreateMappingSet();
        _instance = CreateInstance(7, "Mismatch");

        var reader = A.Fake<IPostgresqlRuntimeDatabaseMetadataReader>();
        var validator = new PostgresqlRuntimeInstanceMappingValidator(
            CreateInstanceProvider((null, _instance)),
            reader,
            new PostgresqlValidatedResourceKeyMapCache(),
            NullLogger<PostgresqlRuntimeInstanceMappingValidator>.Instance
        );

        A.CallTo(() => reader.ReadFingerprintAsync(ConnectionString, A<CancellationToken>.Ignored))
            .Returns(
                new PostgresqlDatabaseFingerprintReadResult.Success(
                    CreateMatchingFingerprint(_mappingSet) with
                    {
                        EffectiveSchemaHash =
                            "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                    }
                )
            );

        _exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateLoadedInstancesAsync(_mappingSet, CancellationToken.None)
        )!;
    }

    [Test]
    public void It_reports_instance_identity_and_expected_mapping_key_details()
    {
        AssertFailureContext(_exception.Message, _instance, _mappingSet);
        _exception.Message.Should().Contain("stored EffectiveSchemaHash");
    }
}

[TestFixture]
public class Given_PostgresqlRuntimeInstanceMappingValidator_With_ResourceKey_Fingerprint_Mismatch
    : PostgresqlRuntimeInstanceMappingValidatorTests
{
    private Exception _exception = null!;
    private MappingSet _mappingSet = null!;
    private DmsInstance _instance = null!;

    [SetUp]
    public void Setup()
    {
        _mappingSet = CreateMappingSet();
        _instance = CreateInstance(9, "Drift");
        var reader = A.Fake<IPostgresqlRuntimeDatabaseMetadataReader>();
        var validator = new PostgresqlRuntimeInstanceMappingValidator(
            CreateInstanceProvider((null, _instance)),
            reader,
            new PostgresqlValidatedResourceKeyMapCache(),
            NullLogger<PostgresqlRuntimeInstanceMappingValidator>.Instance
        );

        A.CallTo(() => reader.ReadFingerprintAsync(ConnectionString, A<CancellationToken>.Ignored))
            .Returns(
                new PostgresqlDatabaseFingerprintReadResult.Success(
                    CreateMatchingFingerprint(_mappingSet) with
                    {
                        ResourceKeyCount = 2,
                    }
                )
            );
        A.CallTo(() => reader.ReadResourceKeysAsync(ConnectionString, A<CancellationToken>.Ignored))
            .Returns(
                new PostgresqlResourceKeyReadResult.Success([
                    new PostgresqlResourceKeyRow(1, "Ed-Fi", "WrongStudent", "5.0.0"),
                ])
            );

        _exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateLoadedInstancesAsync(_mappingSet, CancellationToken.None)
        )!;
    }

    [Test]
    public void It_falls_back_to_dms_resourcekey_and_reports_the_row_diff()
    {
        AssertFailureContext(_exception.Message, _instance, _mappingSet);
        _exception.Message.Should().Contain("ResourceKeyCount stored=2 expected=1.");
        _exception.Message.Should().Contain("Seed data mismatch in dms.ResourceKey");
        _exception.Message.Should().Contain("WrongStudent");
    }
}

[TestFixture]
public class Given_PostgresqlRuntimeInstanceMappingValidator_With_Missing_EffectiveSchema_Table
    : PostgresqlRuntimeInstanceMappingValidatorTests
{
    private Exception _exception = null!;
    private MappingSet _mappingSet = null!;
    private DmsInstance _instance = null!;

    [SetUp]
    public void Setup()
    {
        _mappingSet = CreateMappingSet();
        _instance = CreateInstance(11, "MissingTable");
        var reader = A.Fake<IPostgresqlRuntimeDatabaseMetadataReader>();
        var validator = new PostgresqlRuntimeInstanceMappingValidator(
            CreateInstanceProvider((null, _instance)),
            reader,
            new PostgresqlValidatedResourceKeyMapCache(),
            NullLogger<PostgresqlRuntimeInstanceMappingValidator>.Instance
        );

        A.CallTo(() => reader.ReadFingerprintAsync(ConnectionString, A<CancellationToken>.Ignored))
            .Returns(new PostgresqlDatabaseFingerprintReadResult.MissingEffectiveSchemaTable());

        _exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateLoadedInstancesAsync(_mappingSet, CancellationToken.None)
        )!;
    }

    [Test]
    public void It_reports_that_the_database_must_be_provisioned()
    {
        AssertFailureContext(_exception.Message, _instance, _mappingSet);
        _exception.Message.Should().Contain("dms.\"EffectiveSchema\"");
        _exception.Message.Should().Contain("Provision the database before startup.");
    }
}

[TestFixture]
public class Given_PostgresqlRuntimeInstanceMappingValidator_With_Missing_EffectiveSchema_Row
    : PostgresqlRuntimeInstanceMappingValidatorTests
{
    private Exception _exception = null!;
    private MappingSet _mappingSet = null!;
    private DmsInstance _instance = null!;

    [SetUp]
    public void Setup()
    {
        _mappingSet = CreateMappingSet();
        _instance = CreateInstance(13, "MissingRow");
        var reader = A.Fake<IPostgresqlRuntimeDatabaseMetadataReader>();
        var validator = new PostgresqlRuntimeInstanceMappingValidator(
            CreateInstanceProvider((null, _instance)),
            reader,
            new PostgresqlValidatedResourceKeyMapCache(),
            NullLogger<PostgresqlRuntimeInstanceMappingValidator>.Instance
        );

        A.CallTo(() => reader.ReadFingerprintAsync(ConnectionString, A<CancellationToken>.Ignored))
            .Returns(new PostgresqlDatabaseFingerprintReadResult.MissingEffectiveSchemaRow());

        _exception = Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateLoadedInstancesAsync(_mappingSet, CancellationToken.None)
        )!;
    }

    [Test]
    public void It_reports_that_the_singleton_fingerprint_row_is_missing()
    {
        AssertFailureContext(_exception.Message, _instance, _mappingSet);
        _exception.Message.Should().Contain("singleton fingerprint row is missing");
    }
}
