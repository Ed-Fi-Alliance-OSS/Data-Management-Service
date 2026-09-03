// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
[Parallelizable]
public class ValidateStartupInstancesTaskTests
{
    private static DatabaseFingerprint CreateFingerprint() =>
        new("1.0", "abc123", 2, new byte[32].ToImmutableArray());

    private static EffectiveSchemaInfo CreateEffectiveSchemaInfo() =>
        new(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "1.0",
            EffectiveSchemaHash: "abc123",
            ResourceKeyCount: 2,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder:
            [
                new ResourceKeyEntry(1, new QualifiedResourceName("Ed-Fi", "Student"), "1.0", false),
                new ResourceKeyEntry(2, new QualifiedResourceName("Ed-Fi", "School"), "1.0", false),
            ]
        );

    private static EffectiveSchemaSet CreateEffectiveSchemaSet() => new(CreateEffectiveSchemaInfo(), []);

    [Test]
    public void It_has_order_310()
    {
        var task = CreateTask();
        task.Order.Should().Be(310);
    }

    [Test]
    public void It_has_expected_name()
    {
        var task = CreateTask();
        task.Name.Should().Be("Validate Startup Database Instances");
    }

    private static ValidateStartupInstancesTask CreateTask(
        IDataStoreProvider? instanceProvider = null,
        IConnectionStringProvider? connectionStringProvider = null,
        DatabaseFingerprintProvider? fingerprintProvider = null,
        IResourceKeyValidator? resourceKeyValidator = null,
        ResourceKeyValidationCacheProvider? cacheProvider = null,
        IEffectiveSchemaSetProvider? schemaSetProvider = null
    )
    {
        instanceProvider ??= A.Fake<IDataStoreProvider>();
        connectionStringProvider ??= A.Fake<IConnectionStringProvider>();
        fingerprintProvider ??= new DatabaseFingerprintProvider(
            A.Fake<IDatabaseFingerprintReader>(),
            TimeProvider.System,
            new CacheSettings()
        );
        resourceKeyValidator ??= A.Fake<IResourceKeyValidator>();
        cacheProvider ??= new ResourceKeyValidationCacheProvider(TimeProvider.System, new CacheSettings());
        schemaSetProvider ??= A.Fake<IEffectiveSchemaSetProvider>();

        return new ValidateStartupInstancesTask(
            instanceProvider,
            connectionStringProvider,
            fingerprintProvider,
            resourceKeyValidator,
            cacheProvider,
            schemaSetProvider,
            NullLogger<ValidateStartupInstancesTask>.Instance
        );
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Loaded_Tenants : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_completes_without_errors()
        {
            var instanceProvider = A.Fake<IDataStoreProvider>();
            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(Array.Empty<string>());

            var task = CreateTask(instanceProvider: instanceProvider);

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Instance_Without_ConnectionString : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_completes_without_throwing()
        {
            var instanceProvider = A.Fake<IDataStoreProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DataStore(1, "Type", "TestInstance", null, [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns(null);

            var fingerprintProvider = new DatabaseFingerprintProvider(
                fingerprintReader,
                TimeProvider.System,
                new CacheSettings()
            );
            var task = CreateTask(
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<EffectiveDataStoreTarget>._))
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_All_Instances_Valid : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_completes_successfully()
        {
            var instanceProvider = A.Fake<IDataStoreProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
            var resourceKeyValidator = A.Fake<IResourceKeyValidator>();
            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            var fingerprint = CreateFingerprint();
            var schemaSet = CreateEffectiveSchemaSet();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DataStore(1, "Type", "TestInstance", "Server=test", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=test");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<EffectiveDataStoreTarget>._))
                .Returns(fingerprint);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(schemaSet);
            A.CallTo(() =>
                    resourceKeyValidator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<EffectiveDataStoreTarget>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ResourceKeyValidationResult.ValidationSuccess());

            var fingerprintProvider = new DatabaseFingerprintProvider(
                fingerprintReader,
                TimeProvider.System,
                new CacheSettings()
            );
            var cacheProvider = new ResourceKeyValidationCacheProvider(
                TimeProvider.System,
                new CacheSettings()
            );
            var task = CreateTask(
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider,
                resourceKeyValidator: resourceKeyValidator,
                cacheProvider: cacheProvider,
                schemaSetProvider: schemaSetProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }

    /// <summary>
    /// Startup is primary-only by construction, and unit 6 must not have changed that. A derivative is
    /// optional and may be intentionally offline between extraction windows, so probing one at startup
    /// would turn an expected absence into a startup failure - and priming one would cache a verdict
    /// nobody asked for, under a lifetime startup has no way to renew.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_An_Instance_With_Configured_Derivatives : ValidateStartupInstancesTaskTests
    {
        private readonly List<EffectiveDataStoreTarget> _fingerprintTargets = [];
        private readonly List<EffectiveDataStoreTarget> _validatedTargets = [];

        [SetUp]
        public async Task Setup()
        {
            _fingerprintTargets.Clear();
            _validatedTargets.Clear();

            var instanceProvider = A.Fake<IDataStoreProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
            var resourceKeyValidator = A.Fake<IResourceKeyValidator>();
            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            DataStore instance = new(
                Id: 1,
                DataStoreType: "Type",
                Name: "TestInstance",
                ConnectionString: "Server=primary",
                RouteContext: [],
                DerivativeConnectionStrings:
                [
                    KeyValuePair.Create(DataStoreDerivativeType.ReadReplica, "Server=replica"),
                    KeyValuePair.Create(DataStoreDerivativeType.Snapshot, "Server=snapshot"),
                ]
            );

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null)).Returns([instance]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=primary");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<EffectiveDataStoreTarget>._))
                .Invokes((EffectiveDataStoreTarget target) => _fingerprintTargets.Add(target))
                .Returns(CreateFingerprint());
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(CreateEffectiveSchemaSet());
            A.CallTo(() =>
                    resourceKeyValidator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<EffectiveDataStoreTarget>._,
                        A<CancellationToken>._
                    )
                )
                .Invokes(
                    (
                        DatabaseFingerprint _,
                        short _,
                        ImmutableArray<byte> _,
                        IReadOnlyList<ResourceKeyRow> _,
                        EffectiveDataStoreTarget target,
                        CancellationToken _
                    ) => _validatedTargets.Add(target)
                )
                .Returns(new ResourceKeyValidationResult.ValidationSuccess());

            var task = CreateTask(
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: new DatabaseFingerprintProvider(
                    fingerprintReader,
                    TimeProvider.System,
                    new CacheSettings()
                ),
                resourceKeyValidator: resourceKeyValidator,
                cacheProvider: new ResourceKeyValidationCacheProvider(
                    TimeProvider.System,
                    new CacheSettings()
                ),
                schemaSetProvider: schemaSetProvider
            );

            await task.ExecuteAsync(CancellationToken.None);
        }

        [Test]
        public void It_reads_only_the_primary_fingerprint()
        {
            _fingerprintTargets.Should().ContainSingle();
            _fingerprintTargets[0].Kind.Should().Be(EffectiveTargetKind.Primary);
            _fingerprintTargets[0].ConnectionString.Should().Be("Server=primary");
        }

        [Test]
        public void It_validates_only_the_primary_resource_keys()
        {
            _validatedTargets.Should().ContainSingle();
            _validatedTargets[0].Kind.Should().Be(EffectiveTargetKind.Primary);
            _validatedTargets[0].ConnectionString.Should().Be("Server=primary");
        }

        [Test]
        public void It_never_touches_a_configured_derivative()
        {
            _fingerprintTargets
                .Concat(_validatedTargets)
                .Should()
                .NotContain(target =>
                    target.ConnectionString == "Server=replica"
                    || target.ConnectionString == "Server=snapshot"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Unprovisioned_Database : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_completes_without_throwing()
        {
            var instanceProvider = A.Fake<IDataStoreProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DataStore(1, "Type", "TestInstance", "Server=test", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=test");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<EffectiveDataStoreTarget>._))
                .Returns((DatabaseFingerprint?)null);

            var fingerprintProvider = new DatabaseFingerprintProvider(
                fingerprintReader,
                TimeProvider.System,
                new CacheSettings()
            );
            var task = CreateTask(
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Malformed_Fingerprint : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_completes_without_throwing()
        {
            var instanceProvider = A.Fake<IDataStoreProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DataStore(1, "Type", "TestInstance", "Server=test", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=test");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<EffectiveDataStoreTarget>._))
                .ThrowsAsync(new DatabaseFingerprintValidationException("bad data"));

            var fingerprintProvider = new DatabaseFingerprintProvider(
                fingerprintReader,
                TimeProvider.System,
                new CacheSettings()
            );
            var task = CreateTask(
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_EffectiveSchemaHash_Mismatch : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_completes_without_throwing()
        {
            var instanceProvider = A.Fake<IDataStoreProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            // Fingerprint has hash "db_hash_999" but effective schema expects "abc123"
            var fingerprint = new DatabaseFingerprint(
                "1.0",
                "db_hash_999",
                2,
                new byte[32].ToImmutableArray()
            );
            var schemaSet = CreateEffectiveSchemaSet();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DataStore(1, "Type", "TestInstance", "Server=test", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=test");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<EffectiveDataStoreTarget>._))
                .Returns(fingerprint);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(schemaSet);

            var fingerprintProvider = new DatabaseFingerprintProvider(
                fingerprintReader,
                TimeProvider.System,
                new CacheSettings()
            );
            var task = CreateTask(
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider,
                schemaSetProvider: schemaSetProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Fingerprint_Reader_Throws_Unexpected_Exception : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_completes_without_throwing()
        {
            var instanceProvider = A.Fake<IDataStoreProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DataStore(1, "Type", "TestInstance", "Server=test", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=test");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<EffectiveDataStoreTarget>._))
                .ThrowsAsync(new TimeoutException("connection timed out"));

            var fingerprintProvider = new DatabaseFingerprintProvider(
                fingerprintReader,
                TimeProvider.System,
                new CacheSettings()
            );
            var task = CreateTask(
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ResourceKey_Mismatch : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_completes_without_throwing()
        {
            var instanceProvider = A.Fake<IDataStoreProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
            var resourceKeyValidator = A.Fake<IResourceKeyValidator>();
            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            var fingerprint = CreateFingerprint();
            var schemaSet = CreateEffectiveSchemaSet();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DataStore(1, "Type", "TestInstance", "Server=test", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=test");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<EffectiveDataStoreTarget>._))
                .Returns(fingerprint);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(schemaSet);
            A.CallTo(() =>
                    resourceKeyValidator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<EffectiveDataStoreTarget>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ResourceKeyValidationResult.ValidationFailure("missing: Ed-Fi.Student"));

            var fingerprintProvider = new DatabaseFingerprintProvider(
                fingerprintReader,
                TimeProvider.System,
                new CacheSettings()
            );
            var cacheProvider = new ResourceKeyValidationCacheProvider(
                TimeProvider.System,
                new CacheSettings()
            );
            var task = CreateTask(
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider,
                resourceKeyValidator: resourceKeyValidator,
                cacheProvider: cacheProvider,
                schemaSetProvider: schemaSetProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_One_Bad_Instance_And_One_Good_Instance : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_validates_both_without_throwing()
        {
            var instanceProvider = A.Fake<IDataStoreProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
            var resourceKeyValidator = A.Fake<IResourceKeyValidator>();
            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            var goodFingerprint = CreateFingerprint();
            var schemaSet = CreateEffectiveSchemaSet();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([
                    new DataStore(1, "Type", "BadInstance", "Server=bad", []),
                    new DataStore(2, "Type", "GoodInstance", "Server=good", []),
                ]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=bad");
            A.CallTo(() => connectionStringProvider.GetConnectionString(2, null)).Returns("Server=good");

            // Bad instance: unprovisioned database
            A.CallTo(() =>
                    fingerprintReader.ReadFingerprintAsync(EffectiveDataStoreTarget.Primary("Server=bad"))
                )
                .Returns((DatabaseFingerprint?)null);

            // Good instance: valid fingerprint
            A.CallTo(() =>
                    fingerprintReader.ReadFingerprintAsync(EffectiveDataStoreTarget.Primary("Server=good"))
                )
                .Returns(goodFingerprint);

            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(schemaSet);
            A.CallTo(() =>
                    resourceKeyValidator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<EffectiveDataStoreTarget>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ResourceKeyValidationResult.ValidationSuccess());

            var fingerprintProvider = new DatabaseFingerprintProvider(
                fingerprintReader,
                TimeProvider.System,
                new CacheSettings()
            );
            var cacheProvider = new ResourceKeyValidationCacheProvider(
                TimeProvider.System,
                new CacheSettings()
            );
            var task = CreateTask(
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider,
                resourceKeyValidator: resourceKeyValidator,
                cacheProvider: cacheProvider,
                schemaSetProvider: schemaSetProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            // The bad instance does not prevent the good instance from being validated
            await act.Should().NotThrowAsync();
            A.CallTo(() =>
                    fingerprintReader.ReadFingerprintAsync(EffectiveDataStoreTarget.Primary("Server=bad"))
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    fingerprintReader.ReadFingerprintAsync(EffectiveDataStoreTarget.Primary("Server=good"))
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Multiple_Tenants_With_Multiple_Instances : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_validates_all_instances_across_tenants()
        {
            var instanceProvider = A.Fake<IDataStoreProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
            var resourceKeyValidator = A.Fake<IResourceKeyValidator>();
            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            var fingerprint = CreateFingerprint();
            var schemaSet = CreateEffectiveSchemaSet();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "tenantA", "tenantB" });
            A.CallTo(() => instanceProvider.GetAll("tenantA"))
                .Returns([new DataStore(1, "Type", "Instance1", "Server=a", [])]);
            A.CallTo(() => instanceProvider.GetAll("tenantB"))
                .Returns([new DataStore(2, "Type", "Instance2", "Server=b", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, "tenantA")).Returns("Server=a");
            A.CallTo(() => connectionStringProvider.GetConnectionString(2, "tenantB")).Returns("Server=b");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<EffectiveDataStoreTarget>._))
                .Returns(fingerprint);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(schemaSet);
            A.CallTo(() =>
                    resourceKeyValidator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<EffectiveDataStoreTarget>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ResourceKeyValidationResult.ValidationSuccess());

            var fingerprintProvider = new DatabaseFingerprintProvider(
                fingerprintReader,
                TimeProvider.System,
                new CacheSettings()
            );
            var cacheProvider = new ResourceKeyValidationCacheProvider(
                TimeProvider.System,
                new CacheSettings()
            );
            var task = CreateTask(
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider,
                resourceKeyValidator: resourceKeyValidator,
                cacheProvider: cacheProvider,
                schemaSetProvider: schemaSetProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();

            // Verify both connection strings were read
            A.CallTo(() =>
                    fingerprintReader.ReadFingerprintAsync(EffectiveDataStoreTarget.Primary("Server=a"))
                )
                .MustHaveHappenedOnceExactly();
            A.CallTo(() =>
                    fingerprintReader.ReadFingerprintAsync(EffectiveDataStoreTarget.Primary("Server=b"))
                )
                .MustHaveHappenedOnceExactly();
        }
    }
}
