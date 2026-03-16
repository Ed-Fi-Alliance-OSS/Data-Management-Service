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
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

[TestFixture]
[Parallelizable]
public class ValidateStartupInstancesTaskTests
{
    private static IOptions<AppSettings> CreateAppSettings(bool useRelational) =>
        Options.Create(
            new AppSettings { AllowIdentityUpdateOverrides = "", UseRelationalBackend = useRelational }
        );

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
        var task = CreateTask(useRelational: true);
        task.Order.Should().Be(310);
    }

    [Test]
    public void It_has_expected_name()
    {
        var task = CreateTask(useRelational: true);
        task.Name.Should().Be("Validate Startup Database Instances");
    }

    private static ValidateStartupInstancesTask CreateTask(
        bool useRelational,
        IDmsInstanceProvider? instanceProvider = null,
        IConnectionStringProvider? connectionStringProvider = null,
        DatabaseFingerprintProvider? fingerprintProvider = null,
        IResourceKeyValidator? resourceKeyValidator = null,
        ResourceKeyValidationCacheProvider? cacheProvider = null,
        IEffectiveSchemaSetProvider? schemaSetProvider = null
    )
    {
        instanceProvider ??= A.Fake<IDmsInstanceProvider>();
        connectionStringProvider ??= A.Fake<IConnectionStringProvider>();
        fingerprintProvider ??= new DatabaseFingerprintProvider(A.Fake<IDatabaseFingerprintReader>());
        resourceKeyValidator ??= A.Fake<IResourceKeyValidator>();
        cacheProvider ??= new ResourceKeyValidationCacheProvider();
        schemaSetProvider ??= A.Fake<IEffectiveSchemaSetProvider>();

        return new ValidateStartupInstancesTask(
            CreateAppSettings(useRelational),
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
    public class Given_UseRelationalBackend_Is_Disabled : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_skips_validation()
        {
            var instanceProvider = A.Fake<IDmsInstanceProvider>();
            var task = CreateTask(useRelational: false, instanceProvider: instanceProvider);

            await task.ExecuteAsync(CancellationToken.None);

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).MustNotHaveHappened();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Loaded_Tenants : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_completes_without_errors()
        {
            var instanceProvider = A.Fake<IDmsInstanceProvider>();
            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(Array.Empty<string>());

            var task = CreateTask(useRelational: true, instanceProvider: instanceProvider);

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Instance_Without_ConnectionString : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_throws_InvalidOperationException()
        {
            var instanceProvider = A.Fake<IDmsInstanceProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DmsInstance(1, "Type", "TestInstance", null, [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns(null);

            var fingerprintProvider = new DatabaseFingerprintProvider(fingerprintReader);
            var task = CreateTask(
                useRelational: true,
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            var exception = await act.Should().ThrowAsync<InvalidOperationException>();
            exception.Which.Message.Should().Contain("no connection string");
            exception.Which.Message.Should().Contain("TestInstance");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<string>._)).MustNotHaveHappened();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_All_Instances_Valid : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_completes_successfully()
        {
            var instanceProvider = A.Fake<IDmsInstanceProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
            var resourceKeyValidator = A.Fake<IResourceKeyValidator>();
            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            var fingerprint = CreateFingerprint();
            var schemaSet = CreateEffectiveSchemaSet();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DmsInstance(1, "Type", "TestInstance", "Server=test", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=test");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<string>._)).Returns(fingerprint);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(schemaSet);
            A.CallTo(() =>
                    resourceKeyValidator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<string>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ResourceKeyValidationResult.ValidationSuccess());

            var fingerprintProvider = new DatabaseFingerprintProvider(fingerprintReader);
            var cacheProvider = new ResourceKeyValidationCacheProvider();
            var task = CreateTask(
                useRelational: true,
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
    public class Given_Unprovisioned_Database : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_throws_InvalidOperationException()
        {
            var instanceProvider = A.Fake<IDmsInstanceProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DmsInstance(1, "Type", "TestInstance", "Server=test", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=test");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<string>._))
                .Returns((DatabaseFingerprint?)null);

            var fingerprintProvider = new DatabaseFingerprintProvider(fingerprintReader);
            var task = CreateTask(
                useRelational: true,
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            var exception = await act.Should().ThrowAsync<InvalidOperationException>();
            exception.Which.Message.Should().Contain("Database not provisioned");
            exception.Which.Message.Should().Contain("TestInstance");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Malformed_Fingerprint : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_throws_DatabaseFingerprintValidationException()
        {
            var instanceProvider = A.Fake<IDmsInstanceProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DmsInstance(1, "Type", "TestInstance", "Server=test", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=test");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<string>._))
                .ThrowsAsync(new DatabaseFingerprintValidationException("bad data"));

            var fingerprintProvider = new DatabaseFingerprintProvider(fingerprintReader);
            var task = CreateTask(
                useRelational: true,
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            await act.Should().ThrowAsync<DatabaseFingerprintValidationException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_EffectiveSchemaHash_Mismatch : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_throws_InvalidOperationException_with_hash_details()
        {
            var instanceProvider = A.Fake<IDmsInstanceProvider>();
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
                .Returns([new DmsInstance(1, "Type", "TestInstance", "Server=test", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=test");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<string>._)).Returns(fingerprint);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(schemaSet);

            var fingerprintProvider = new DatabaseFingerprintProvider(fingerprintReader);
            var task = CreateTask(
                useRelational: true,
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider,
                schemaSetProvider: schemaSetProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            var exception = await act.Should().ThrowAsync<InvalidOperationException>();
            exception.Which.Message.Should().Contain("EffectiveSchemaHash mismatch");
            exception.Which.Message.Should().Contain("TestInstance");
            exception.Which.Message.Should().Contain("db_hash_999");
            exception.Which.Message.Should().Contain("abc123");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Fingerprint_Reader_Throws_Unexpected_Exception : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_wraps_exception_with_instance_context()
        {
            var instanceProvider = A.Fake<IDmsInstanceProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DmsInstance(1, "Type", "TestInstance", "Server=test", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=test");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<string>._))
                .ThrowsAsync(new TimeoutException("connection timed out"));

            var fingerprintProvider = new DatabaseFingerprintProvider(fingerprintReader);
            var task = CreateTask(
                useRelational: true,
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            var exception = await act.Should().ThrowAsync<InvalidOperationException>();
            exception.Which.Message.Should().Contain("TestInstance");
            exception.Which.Message.Should().Contain("connection timed out");
            exception.Which.InnerException.Should().BeOfType<TimeoutException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_ResourceKey_Mismatch : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_throws_InvalidOperationException_with_diff_report()
        {
            var instanceProvider = A.Fake<IDmsInstanceProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
            var resourceKeyValidator = A.Fake<IResourceKeyValidator>();
            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            var fingerprint = CreateFingerprint();
            var schemaSet = CreateEffectiveSchemaSet();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "" });
            A.CallTo(() => instanceProvider.GetAll(null))
                .Returns([new DmsInstance(1, "Type", "TestInstance", "Server=test", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, null)).Returns("Server=test");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<string>._)).Returns(fingerprint);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(schemaSet);
            A.CallTo(() =>
                    resourceKeyValidator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<string>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ResourceKeyValidationResult.ValidationFailure("missing: Ed-Fi.Student"));

            var fingerprintProvider = new DatabaseFingerprintProvider(fingerprintReader);
            var cacheProvider = new ResourceKeyValidationCacheProvider();
            var task = CreateTask(
                useRelational: true,
                instanceProvider: instanceProvider,
                connectionStringProvider: connectionStringProvider,
                fingerprintProvider: fingerprintProvider,
                resourceKeyValidator: resourceKeyValidator,
                cacheProvider: cacheProvider,
                schemaSetProvider: schemaSetProvider
            );

            Func<Task> act = async () => await task.ExecuteAsync(CancellationToken.None);

            var exception = await act.Should().ThrowAsync<InvalidOperationException>();
            exception.Which.Message.Should().Contain("Resource key seed mismatch");
            exception.Which.Message.Should().Contain("TestInstance");
            exception.Which.Message.Should().Contain("missing: Ed-Fi.Student");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Multiple_Tenants_With_Multiple_Instances : ValidateStartupInstancesTaskTests
    {
        [Test]
        public async Task It_validates_all_instances_across_tenants()
        {
            var instanceProvider = A.Fake<IDmsInstanceProvider>();
            var connectionStringProvider = A.Fake<IConnectionStringProvider>();
            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
            var resourceKeyValidator = A.Fake<IResourceKeyValidator>();
            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

            var fingerprint = CreateFingerprint();
            var schemaSet = CreateEffectiveSchemaSet();

            A.CallTo(() => instanceProvider.GetLoadedTenantKeys()).Returns(new[] { "tenantA", "tenantB" });
            A.CallTo(() => instanceProvider.GetAll("tenantA"))
                .Returns([new DmsInstance(1, "Type", "Instance1", "Server=a", [])]);
            A.CallTo(() => instanceProvider.GetAll("tenantB"))
                .Returns([new DmsInstance(2, "Type", "Instance2", "Server=b", [])]);
            A.CallTo(() => connectionStringProvider.GetConnectionString(1, "tenantA")).Returns("Server=a");
            A.CallTo(() => connectionStringProvider.GetConnectionString(2, "tenantB")).Returns("Server=b");
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(A<string>._)).Returns(fingerprint);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(schemaSet);
            A.CallTo(() =>
                    resourceKeyValidator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<string>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ResourceKeyValidationResult.ValidationSuccess());

            var fingerprintProvider = new DatabaseFingerprintProvider(fingerprintReader);
            var cacheProvider = new ResourceKeyValidationCacheProvider();
            var task = CreateTask(
                useRelational: true,
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
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync("Server=a")).MustHaveHappenedOnceExactly();
            A.CallTo(() => fingerprintReader.ReadFingerprintAsync("Server=b")).MustHaveHappenedOnceExactly();
        }
    }
}
