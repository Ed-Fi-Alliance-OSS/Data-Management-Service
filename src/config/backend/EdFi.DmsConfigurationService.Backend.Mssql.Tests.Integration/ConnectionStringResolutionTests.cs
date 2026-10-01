// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreDerivative;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.Secrets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The read rule through the repositories against a real database: every projection site resolves
/// what it read, the collections are complete before they are returned, a derivative read as part
/// of a data store is contained while every other read fails, and nothing is resolved twice.
/// </summary>
public class ConnectionStringResolutionTests : DatabaseTest
{
    private const string FailingSecret = "broken";

    /// <summary>Not the engine this deployment runs, so a read that consulted it would fail to parse.</summary>
    private const string MisleadingProvider = "postgresql";

    private static readonly ConnectionStringEncryptionService _encryption = new(
        MssqlTestConfiguration.DatabaseOptions
    );

    private static string ConnectionStringWithPassword(string password) =>
        $"Server=db;User Id=edfi;Database=edfi;Password={password}";

    protected static string Rendered(string password) =>
        $"Data Source=db;Initial Catalog=edfi;User ID=edfi;Password={password}";

    private const string CorruptParentSql = """
        UPDATE dmscs.DataStore SET ConnectionString = @Bytes WHERE Id = @Id;
        """;

    private const string CorruptDerivativeSql = """
        UPDATE dmscs.DataStoreDerivative SET ConnectionString = @Bytes WHERE Id = @Id;
        """;

    private const string StoredParentSql = """
        SELECT ConnectionString FROM dmscs.DataStore WHERE Id = @Id;
        """;

    /// <summary>Resolves each name to <c>value-of-&lt;name&gt;</c>, except the failing one, recording every call.</summary>
    protected sealed class RecordingResolver : ISecretResolver
    {
        public ConcurrentQueue<SecretReference> Calls { get; } = new();

        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
        {
            Calls.Enqueue(reference);
            return reference.Name == FailingSecret
                ? throw new InvalidOperationException("the vault refused")
                : ValueTask.FromResult($"value-of-{reference.Name}");
        }
    }

    protected RecordingResolver Resolver { get; private set; } = null!;
    protected IDataStoreRepository DataStores { get; private set; } = null!;
    protected IDataStoreDerivativeRepository Derivatives { get; private set; } = null!;

    [SetUp]
    public void CreateRepositories()
    {
        Resolver = new RecordingResolver();
        (DataStores, Derivatives) = CreateRepositories(new TenantContextProvider(), Resolver);
    }

    protected static (IDataStoreRepository, IDataStoreDerivativeRepository) CreateRepositories(
        TenantContextProvider tenant,
        ISecretResolver? resolver
    )
    {
        // The cache is off, so a value resolved twice reaches the resolver twice.
        IConnectionStringReader reader = TestConnectionStringReader.Create(_encryption, tenant, resolver);
        var derivatives = new DataStoreDerivativeRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<DataStoreDerivativeRepository>.Instance,
            _encryption,
            reader,
            new TestAuditContext(),
            tenant
        );
        var dataStores = new DataStoreRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<DataStoreRepository>.Instance,
            _encryption,
            reader,
            new DataStoreContextRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<DataStoreContextRepository>.Instance,
                new TestAuditContext(),
                tenant
            ),
            derivatives,
            new TestAuditContext(),
            tenant
        );
        return (dataStores, derivatives);
    }

    protected async Task<int> InsertDataStore(
        string name,
        string? connectionString,
        IDataStoreRepository? into = null
    )
    {
        var result = await (into ?? DataStores).InsertDataStore(
            new DataStoreInsertCommand
            {
                DataStoreType = "Production",
                Name = name,
                Provider = MisleadingProvider,
                ConnectionString = connectionString,
            }
        );
        return result.Should().BeOfType<DataStoreInsertResult.Success>().Subject.Id;
    }

    protected async Task<int> InsertDerivative(int dataStoreId, string type, string connectionString)
    {
        var result = await Derivatives.InsertDataStoreDerivative(
            new DataStoreDerivativeInsertCommand
            {
                DataStoreId = dataStoreId,
                DerivativeType = type,
                ConnectionString = connectionString,
            }
        );
        return result.Should().BeOfType<DataStoreDerivativeInsertResult.Success>().Subject.Id;
    }

    protected Task CorruptDataStore(int id) =>
        Connection!.ExecuteAsync(CorruptParentSql, new { Id = id, Bytes = CorruptBytes() });

    protected Task CorruptDerivative(int id) =>
        Connection!.ExecuteAsync(CorruptDerivativeSql, new { Id = id, Bytes = CorruptBytes() });

    protected async Task<string> StoredParentBase64(int id) =>
        Convert.ToBase64String(await Connection!.QuerySingleAsync<byte[]>(StoredParentSql, new { Id = id }));

    /// <summary>Not a whole number of AES blocks after the initialization vector, so decryption throws.</summary>
    private static byte[] CorruptBytes() => [.. Enumerable.Range(0, 20).Select(i => (byte)i)];

    protected static string? Password(string? base64) =>
        base64 is null
            ? null
            : (string?)
                (
                    (IDataStoreConnectionStringBuilderSource)new MssqlDataStoreConnectionStringValidator()
                ).CreateBuilder(_encryption.Decrypt(Convert.FromBase64String(base64))!)["Password"];

    protected static string? Decrypted(string? base64) =>
        base64 is null ? null : _encryption.Decrypt(Convert.FromBase64String(base64));

    [TestFixture]
    public class Given_a_data_store_and_derivatives_carrying_references : ConnectionStringResolutionTests
    {
        private int _dataStoreId;
        private int _replicaId;
        private int _snapshotId;
        private string _snapshotStored = null!;

        [SetUp]
        public async Task Arrange()
        {
            _dataStoreId = await InsertDataStore(
                "Tokenized",
                ConnectionStringWithPassword("${secret:parent}")
            );
            _replicaId = await InsertDerivative(
                _dataStoreId,
                "ReadReplica",
                ConnectionStringWithPassword("${secret:replica}")
            );
            _snapshotId = await InsertDerivative(
                _dataStoreId,
                "Snapshot",
                ConnectionStringWithPassword("plain")
            );

            var standalone = await Derivatives.GetDataStoreDerivative(_snapshotId);
            _snapshotStored = standalone
                .Should()
                .BeOfType<DataStoreDerivativeGetResult.Success>()
                .Subject.DataStoreDerivativeResponse.ConnectionString!;
            Resolver.Calls.Clear();
        }

        [Test]
        public async Task It_resolves_the_collection_read_and_its_nested_derivative_once_each()
        {
            var result = await DataStores.QueryDataStore(new DataStoreQuery());

            var dataStores = result
                .Should()
                .BeOfType<DataStoreQueryResult.Success>()
                .Subject.DataStoreResponses;
            dataStores.Should().BeOfType<List<DataStoreResponse>>();
            DataStoreResponse dataStore = dataStores.Single();
            Decrypted(dataStore.ConnectionString).Should().Be(Rendered("value-of-parent"));
            Password(dataStore.DataStoreDerivatives.Single(d => d.Id == _replicaId).ConnectionString)
                .Should()
                .Be("value-of-replica");
            dataStore
                .DataStoreDerivatives.Single(d => d.Id == _snapshotId)
                .ConnectionString.Should()
                .Be(_snapshotStored);
            Resolver.Calls.Select(call => call.Name).Should().BeEquivalentTo("parent", "replica");
        }

        [Test]
        public async Task It_resolves_the_single_row_read_and_its_nested_derivative_once_each()
        {
            var result = await DataStores.GetDataStore(_dataStoreId);

            DataStoreResponse dataStore = result
                .Should()
                .BeOfType<DataStoreGetResult.Success>()
                .Subject.DataStoreResponse;
            Password(dataStore.ConnectionString).Should().Be("value-of-parent");
            Password(dataStore.DataStoreDerivatives.Single(d => d.Id == _replicaId).ConnectionString)
                .Should()
                .Be("value-of-replica");
            Resolver.Calls.Select(call => call.Name).Should().BeEquivalentTo("parent", "replica");
        }

        [Test]
        public async Task It_resolves_every_derivative_read()
        {
            var query = await Derivatives.QueryDataStoreDerivative(new PagingQuery());
            var get = await Derivatives.GetDataStoreDerivative(_replicaId);
            var byDataStore = await Derivatives.GetDataStoreDerivativesByDataStore(_dataStoreId);
            var byIds = await Derivatives.GetDataStoreDerivativesByDataStoreIds([_dataStoreId]);

            var queried = query
                .Should()
                .BeOfType<DataStoreDerivativeQueryResult.Success>()
                .Subject.DataStoreDerivativeResponses;
            queried.Should().BeOfType<List<DataStoreDerivativeResponse>>();
            Password(queried.Single(d => d.Id == _replicaId).ConnectionString)
                .Should()
                .Be("value-of-replica");
            Password(
                    get.Should()
                        .BeOfType<DataStoreDerivativeGetResult.Success>()
                        .Subject.DataStoreDerivativeResponse.ConnectionString
                )
                .Should()
                .Be("value-of-replica");

            var nested = byDataStore
                .Should()
                .BeOfType<DataStoreDerivativeQueryByDataStoreResult.Success>()
                .Subject.DataStoreDerivativeResponses;
            nested.Should().BeOfType<List<DataStoreDerivativeResponse>>();
            Password(nested.Single(d => d.Id == _replicaId).ConnectionString).Should().Be("value-of-replica");

            var nestedByIds = byIds
                .Should()
                .BeOfType<DataStoreDerivativeQueryByDataStoreIdsResult.Success>()
                .Subject.DataStoreDerivativeResponses;
            nestedByIds.Should().BeOfType<List<DataStoreDerivativeResponse>>();
            Password(nestedByIds.Single(d => d.Id == _replicaId).ConnectionString)
                .Should()
                .Be("value-of-replica");
        }

        [Test]
        public async Task It_builds_with_the_configured_engine_and_not_the_rows_provider()
        {
            var result = await DataStores.GetDataStore(_dataStoreId);

            DataStoreResponse dataStore = result
                .Should()
                .BeOfType<DataStoreGetResult.Success>()
                .Subject.DataStoreResponse;
            dataStore.Provider.Should().Be(MisleadingProvider);
            Decrypted(dataStore.ConnectionString).Should().Be(Rendered("value-of-parent"));
        }
    }

    [TestFixture]
    public class Given_a_derivative_whose_reference_cannot_be_resolved : ConnectionStringResolutionTests
    {
        private int _dataStoreId;
        private int _failingId;
        private int _healthyId;

        [SetUp]
        public async Task Arrange()
        {
            _dataStoreId = await InsertDataStore("Parent", ConnectionStringWithPassword("${secret:parent}"));
            _failingId = await InsertDerivative(
                _dataStoreId,
                "ReadReplica",
                ConnectionStringWithPassword($"${{secret:{FailingSecret}}}")
            );
            _healthyId = await InsertDerivative(
                _dataStoreId,
                "Snapshot",
                ConnectionStringWithPassword("${secret:snapshot}")
            );
        }

        [Test]
        public async Task It_contains_the_failure_to_that_derivative_in_a_collection_read_of_the_data_store()
        {
            var result = await DataStores.QueryDataStore(new DataStoreQuery());

            DataStoreResponse dataStore = result
                .Should()
                .BeOfType<DataStoreQueryResult.Success>()
                .Subject.DataStoreResponses.Single();
            Password(dataStore.ConnectionString).Should().Be("value-of-parent");
            dataStore.DataStoreDerivatives.Single(d => d.Id == _failingId).ConnectionString.Should().BeNull();
            Password(dataStore.DataStoreDerivatives.Single(d => d.Id == _healthyId).ConnectionString)
                .Should()
                .Be("value-of-snapshot");
        }

        [Test]
        public async Task It_contains_the_failure_to_that_derivative_in_a_single_row_read_of_the_data_store()
        {
            var result = await DataStores.GetDataStore(_dataStoreId);

            DataStoreResponse dataStore = result
                .Should()
                .BeOfType<DataStoreGetResult.Success>()
                .Subject.DataStoreResponse;
            Password(dataStore.ConnectionString).Should().Be("value-of-parent");
            dataStore.DataStoreDerivatives.Single(d => d.Id == _failingId).ConnectionString.Should().BeNull();
            Password(dataStore.DataStoreDerivatives.Single(d => d.Id == _healthyId).ConnectionString)
                .Should()
                .Be("value-of-snapshot");
        }

        [Test]
        public async Task It_returns_completed_nested_collections_with_the_derivative_unconfigured()
        {
            var byDataStore = await Derivatives.GetDataStoreDerivativesByDataStore(_dataStoreId);
            var byIds = await Derivatives.GetDataStoreDerivativesByDataStoreIds([_dataStoreId]);

            byDataStore
                .Should()
                .BeOfType<DataStoreDerivativeQueryByDataStoreResult.Success>()
                .Subject.DataStoreDerivativeResponses.Should()
                .BeOfType<List<DataStoreDerivativeResponse>>()
                .Which.Single(d => d.Id == _failingId)
                .ConnectionString.Should()
                .BeNull();
            byIds
                .Should()
                .BeOfType<DataStoreDerivativeQueryByDataStoreIdsResult.Success>()
                .Subject.DataStoreDerivativeResponses.Should()
                .BeOfType<List<DataStoreDerivativeResponse>>()
                .Which.Single(d => d.Id == _failingId)
                .ConnectionString.Should()
                .BeNull();
        }

        [Test]
        public async Task It_fails_the_standalone_collection_read()
        {
            var result = await Derivatives.QueryDataStoreDerivative(new PagingQuery());

            result
                .Should()
                .BeOfType<DataStoreDerivativeQueryResult.FailureUnknown>()
                .Subject.FailureMessage.Should()
                .Contain($"secret {FailingSecret} could not be resolved");
        }

        [Test]
        public async Task It_fails_the_standalone_single_row_read()
        {
            var result = await Derivatives.GetDataStoreDerivative(_failingId);

            result
                .Should()
                .BeOfType<DataStoreDerivativeGetResult.FailureUnknown>()
                .Subject.FailureMessage.Should()
                .Contain($"secret {FailingSecret} could not be resolved");
        }
    }

    /// <summary>
    /// The same unresolvable reference fails a collection read when a data store carries it and
    /// leaves the read successful when a derivative does.
    /// </summary>
    [TestFixture("parent")]
    [TestFixture("derivative")]
    public class Given_two_data_stores_with_one_unresolvable_reference(string carrier)
        : ConnectionStringResolutionTests
    {
        private DataStoreQueryResult _result = null!;

        [SetUp]
        public async Task Arrange()
        {
            string failing = ConnectionStringWithPassword($"${{secret:{FailingSecret}}}");
            await InsertDataStore("Healthy", ConnectionStringWithPassword("${secret:healthy}"));
            int carrierId = await InsertDataStore(
                "Carrier",
                carrier == "parent" ? failing : ConnectionStringWithPassword("${secret:carrier}")
            );

            if (carrier == "derivative")
            {
                await InsertDerivative(carrierId, "ReadReplica", failing);
            }

            _result = await DataStores.QueryDataStore(new DataStoreQuery());
        }

        [Test]
        public void It_fails_the_read_only_when_a_data_store_carries_it()
        {
            if (carrier == "parent")
            {
                _result
                    .Should()
                    .BeOfType<DataStoreQueryResult.FailureUnknown>()
                    .Subject.FailureMessage.Should()
                    .Contain($"secret {FailingSecret} could not be resolved");
            }
            else
            {
                _result
                    .Should()
                    .BeOfType<DataStoreQueryResult.Success>()
                    .Subject.DataStoreResponses.Select(d => Password(d.ConnectionString))
                    .Should()
                    .BeEquivalentTo("value-of-healthy", "value-of-carrier");
            }
        }
    }

    [TestFixture]
    public class Given_values_without_references : ConnectionStringResolutionTests
    {
        private int _nullId;
        private int _plainId;

        [SetUp]
        public async Task Arrange()
        {
            _nullId = await InsertDataStore("No connection string", null);
            _plainId = await InsertDataStore("Plain", ConnectionStringWithPassword("plain"));
        }

        [Test]
        public async Task It_returns_null_for_a_null_column()
        {
            var result = await DataStores.GetDataStore(_nullId);

            result
                .Should()
                .BeOfType<DataStoreGetResult.Success>()
                .Subject.DataStoreResponse.ConnectionString.Should()
                .BeNull();
        }

        [Test]
        public async Task It_returns_the_stored_bytes_unchanged()
        {
            var result = await DataStores.GetDataStore(_plainId);

            result
                .Should()
                .BeOfType<DataStoreGetResult.Success>()
                .Subject.DataStoreResponse.ConnectionString.Should()
                .Be(await StoredParentBase64(_plainId));
        }

        [Test]
        public async Task It_never_asks_the_resolver()
        {
            await DataStores.QueryDataStore(new DataStoreQuery());
            Resolver.Calls.Should().BeEmpty();
        }
    }

    /// <summary>
    /// A value that cannot be decrypted fails every read that reaches it as the resource, carrying no
    /// reference at all, which is what shows every value is decrypted. A derivative read as part of its
    /// data store reads as not configured instead, as DMS treats a derivative it cannot decrypt, so
    /// one row still under an old key leaves its siblings and every other data store's derivatives in
    /// place.
    /// </summary>
    [TestFixture]
    public class Given_stored_values_that_cannot_be_decrypted : ConnectionStringResolutionTests
    {
        private int _corruptParentId;
        private int _parentOfCorruptDerivativeId;
        private int _corruptDerivativeId;
        private int _healthyDerivativeId;

        [SetUp]
        public async Task Arrange()
        {
            _corruptParentId = await InsertDataStore("Corrupt parent", ConnectionStringWithPassword("plain"));
            await CorruptDataStore(_corruptParentId);

            _parentOfCorruptDerivativeId = await InsertDataStore(
                "Healthy parent",
                ConnectionStringWithPassword("plain")
            );
            _corruptDerivativeId = await InsertDerivative(
                _parentOfCorruptDerivativeId,
                "ReadReplica",
                ConnectionStringWithPassword("plain")
            );
            await CorruptDerivative(_corruptDerivativeId);
            _healthyDerivativeId = await InsertDerivative(
                _parentOfCorruptDerivativeId,
                "Snapshot",
                ConnectionStringWithPassword("sibling")
            );
        }

        private void ShouldContainOnlyTheCorruptRow(IEnumerable<DataStoreDerivativeItem> derivatives)
        {
            List<DataStoreDerivativeItem> items = derivatives.ToList();
            items.Single(d => d.Id == _corruptDerivativeId).ConnectionString.Should().BeNull();
            Password(items.Single(d => d.Id == _healthyDerivativeId).ConnectionString).Should().Be("sibling");
        }

        private static string DecryptMessage(string row) =>
            $"The stored connection string for {row} could not be decrypted.";

        private string DerivativeRow =>
            $"data store derivative {_corruptDerivativeId} (ReadReplica) of data store {_parentOfCorruptDerivativeId}";

        [Test]
        public async Task It_fails_the_data_store_reads()
        {
            (await DataStores.QueryDataStore(new DataStoreQuery()))
                .Should()
                .BeOfType<DataStoreQueryResult.FailureUnknown>()
                .Subject.FailureMessage.Should()
                .Be(DecryptMessage($"data store {_corruptParentId}"));
            (await DataStores.GetDataStore(_corruptParentId))
                .Should()
                .BeOfType<DataStoreGetResult.FailureUnknown>()
                .Subject.FailureMessage.Should()
                .Be(DecryptMessage($"data store {_corruptParentId}"));
        }

        [Test]
        public async Task It_fails_the_standalone_derivative_reads()
        {
            (await Derivatives.QueryDataStoreDerivative(new PagingQuery()))
                .Should()
                .BeOfType<DataStoreDerivativeQueryResult.FailureUnknown>()
                .Subject.FailureMessage.Should()
                .Be(DecryptMessage(DerivativeRow));
            (await Derivatives.GetDataStoreDerivative(_corruptDerivativeId))
                .Should()
                .BeOfType<DataStoreDerivativeGetResult.FailureUnknown>()
                .Subject.FailureMessage.Should()
                .Be(DecryptMessage(DerivativeRow));
        }

        [Test]
        public async Task It_contains_the_row_in_the_nested_derivative_methods()
        {
            var byDataStore = await Derivatives.GetDataStoreDerivativesByDataStore(
                _parentOfCorruptDerivativeId
            );
            var byIds = await Derivatives.GetDataStoreDerivativesByDataStoreIds([
                _parentOfCorruptDerivativeId,
            ]);

            ShouldContainOnlyTheCorruptRow(
                byDataStore
                    .Should()
                    .BeOfType<DataStoreDerivativeQueryByDataStoreResult.Success>()
                    .Subject.DataStoreDerivativeResponses.Select(d => new DataStoreDerivativeItem(
                        d.Id,
                        d.DataStoreId,
                        d.DerivativeType,
                        d.ConnectionString
                    ))
            );
            ShouldContainOnlyTheCorruptRow(
                byIds
                    .Should()
                    .BeOfType<DataStoreDerivativeQueryByDataStoreIdsResult.Success>()
                    .Subject.DataStoreDerivativeResponses.Select(d => new DataStoreDerivativeItem(
                        d.Id,
                        d.DataStoreId,
                        d.DerivativeType,
                        d.ConnectionString
                    ))
            );
        }

        [Test]
        public async Task It_contains_the_row_on_the_single_row_read()
        {
            var result = await DataStores.GetDataStore(_parentOfCorruptDerivativeId);

            DataStoreResponse dataStore = result
                .Should()
                .BeOfType<DataStoreGetResult.Success>()
                .Subject.DataStoreResponse;
            dataStore.ConnectionString.Should().NotBeNull();
            ShouldContainOnlyTheCorruptRow(dataStore.DataStoreDerivatives);
        }

        [Test]
        public async Task It_contains_the_row_on_the_collection_read()
        {
            await DataStores.DeleteDataStore(_corruptParentId);

            var result = await DataStores.QueryDataStore(new DataStoreQuery());

            ShouldContainOnlyTheCorruptRow(
                result
                    .Should()
                    .BeOfType<DataStoreQueryResult.Success>()
                    .Subject.DataStoreResponses.Single()
                    .DataStoreDerivatives
            );
        }

        [Test]
        public async Task It_never_asks_the_resolver()
        {
            await Derivatives.GetDataStoreDerivative(_corruptDerivativeId);
            Resolver.Calls.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_multitenant_read : ConnectionStringResolutionTests
    {
        private string _tenantName = null!;
        private IDataStoreRepository _tenantDataStores = null!;
        private IDataStoreRepository _otherTenantDataStores = null!;
        private int _dataStoreId;

        [SetUp]
        public async Task Arrange()
        {
            var tenants = new TenantRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<TenantRepository>.Instance,
                new TestAuditContext()
            );
            _tenantName = $"ResolutionTenant-{Guid.NewGuid()}";
            TenantContextProvider tenant = await TenantProvider(tenants, _tenantName);
            TenantContextProvider otherTenant = await TenantProvider(
                tenants,
                $"OtherTenant-{Guid.NewGuid()}"
            );

            (_tenantDataStores, _) = CreateRepositories(tenant, Resolver);
            (_otherTenantDataStores, _) = CreateRepositories(otherTenant, Resolver);
            _dataStoreId = await InsertDataStore(
                "Tenant data store",
                ConnectionStringWithPassword("${secret:parent}"),
                _tenantDataStores
            );
        }

        private static async Task<TenantContextProvider> TenantProvider(TenantRepository tenants, string name)
        {
            var result = await tenants.InsertTenant(new TenantInsertCommand { Name = name });
            return new TenantContextProvider
            {
                Context = new TenantContext.Multitenant(
                    result.Should().BeOfType<TenantInsertResult.Success>().Subject.Id,
                    name
                ),
            };
        }

        [Test]
        public async Task It_passes_the_requests_tenant_to_the_resolver()
        {
            await _tenantDataStores.GetDataStore(_dataStoreId);

            Resolver.Calls.Should().Equal(new SecretReference("parent", _tenantName));
        }

        [Test]
        public async Task It_does_not_reach_another_tenants_row()
        {
            var result = await _otherTenantDataStores.GetDataStore(_dataStoreId);

            result.Should().BeOfType<DataStoreGetResult.FailureNotFound>();
            Resolver.Calls.Should().BeEmpty();
        }
    }
}
