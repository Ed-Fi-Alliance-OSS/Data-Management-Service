// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public abstract class ConsistencyOperationTestBase : DatabaseTest
{
    protected readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
        MssqlTestConfiguration.DatabaseOptions,
        NullLogger<ApplicationRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    protected readonly IApiClientRepository _apiClientRepository = new ApiClientRepository(
        MssqlTestConfiguration.DatabaseOptions,
        NullLogger<ApiClientRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    protected int _vendorId;
    protected int _profileId;
    protected int _applicationId;
    protected string _clientId = null!;
    protected Guid _clientUuid;
    protected string _secondClientId = null!;
    protected Guid _secondClientUuid;
    protected int _secondClientRowId;
    protected int _dataStoreId1;
    protected int _dataStoreId2;

    [SetUp]
    public async Task SeedAggregateAsync()
    {
        _clientId = Guid.NewGuid().ToString();
        _clientUuid = Guid.NewGuid();
        _secondClientId = Guid.NewGuid().ToString();
        _secondClientUuid = Guid.NewGuid();

        IVendorRepository vendorRepository = new VendorRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<VendorRepository>.Instance,
            new TestAuditContext(),
            new TenantContextProvider()
        );
        var vendorResult = await vendorRepository.InsertVendor(
            new VendorInsertCommand
            {
                Company = "Consistency Test Company",
                ContactEmailAddress = "test@test.com",
                ContactName = "Fake Name",
                NamespacePrefixes = "FakePrefix1",
            }
        );
        vendorResult.Should().BeOfType<VendorInsertResult.Success>();
        _vendorId = ((VendorInsertResult.Success)vendorResult).Id;

        _dataStoreId1 = await Connection!.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.DataStore (DataStoreType, Name)
            OUTPUT INSERTED.Id VALUES ('Production', 'Consistency Store One');
            """
        );
        _dataStoreId2 = await Connection!.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.DataStore (DataStoreType, Name)
            OUTPUT INSERTED.Id VALUES ('Production', 'Consistency Store Two');
            """
        );

        _profileId = await Connection!.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.Profile (ProfileName, Definition)
            OUTPUT INSERTED.Id VALUES (@ProfileName, '<Profile/>');
            """,
            new { ProfileName = $"Consistency Profile {Guid.NewGuid()}" }
        );

        var applicationResult = await _applicationRepository.InsertApplication(
            new ApplicationInsertCommand
            {
                ApplicationName = "Consistency Test Application",
                VendorId = _vendorId,
                ClaimSetName = "ConsistencyClaimSet",
                EducationOrganizationIds = [100, 200],
                DataStoreIds = [_dataStoreId1],
                ProfileIds = [_profileId],
            },
            new() { ClientId = _clientId, ClientUuid = _clientUuid }
        );
        applicationResult.Should().BeOfType<ApplicationInsertResult.Success>();
        _applicationId = ((ApplicationInsertResult.Success)applicationResult).Id;

        var secondClientResult = await _apiClientRepository.InsertApiClient(
            new ApiClientInsertCommand
            {
                ApplicationId = _applicationId,
                Name = "Second Client",
                IsApproved = true,
                DataStoreIds = [_dataStoreId2],
            },
            new() { ClientId = _secondClientId, ClientUuid = _secondClientUuid }
        );
        secondClientResult.Should().BeOfType<ApiClientInsertResult.Success>();
        _secondClientRowId = ((ApiClientInsertResult.Success)secondClientResult).Id;
    }

    protected async Task<Guid> ReadStoredClientUuidAsync(string clientId) =>
        await Connection!.ExecuteScalarAsync<Guid>(
            """SELECT ClientUuid FROM dmscs.ApiClient WHERE ClientId = @ClientId;""",
            new { ClientId = clientId }
        );
}

[TestFixture]
public class Given_an_application_sync_state_read_during_an_uncommitted_update : ConsistencyOperationTestBase
{
    private bool _completedWhileWriterHeldLock;
    private ApplicationSyncStateResult _result = null!;

    [SetUp]
    public async Task Act()
    {
        await using var writer = await Connection!.BeginTransactionAsync();
        await Connection.ExecuteAsync(
            """UPDATE dmscs.Application SET ApplicationName = 'Changed In Flight' WHERE Id = @Id;""",
            new { Id = _applicationId },
            writer
        );

        Task<ApplicationSyncStateResult> reading = _applicationRepository.GetApplicationSyncState(
            _applicationId
        );
        await Task.Delay(300);
        _completedWhileWriterHeldLock = reading.IsCompleted;

        await writer.CommitAsync();
        _result = await reading;
    }

    [Test]
    public void It_waits_for_the_in_flight_transaction() => _completedWhileWriterHeldLock.Should().BeFalse();

    [Test]
    public void It_reads_the_committed_state() =>
        ((ApplicationSyncStateResult.Success)_result).State.ApplicationName.Should().Be("Changed In Flight");
}

[TestFixture]
public class Given_an_application_save_of_a_multi_client_application : ConsistencyOperationTestBase
{
    private ApplicationSaveResult _result = null!;

    [SetUp]
    public async Task Act()
    {
        _result = await _applicationRepository.SaveApplication(
            new ApplicationUpdateCommand
            {
                Id = _applicationId,
                ApplicationName = "Updated Consistency Application",
                VendorId = _vendorId,
                ClaimSetName = "UpdatedClaimSet",
                EducationOrganizationIds = [300],
                ProfileIds = [_profileId],
            }
        );
    }

    [Test]
    public void It_succeeds() => _result.Should().BeOfType<ApplicationSaveResult.Success>();

    [Test]
    public void It_keeps_each_clients_data_store_assignment()
    {
        // The rename marks both clients, so the save returns each with its own data store set.
        ApplicationSyncPendingClient[] clients = _result
            .Should()
            .BeOfType<ApplicationSaveResult.Success>()
            .Subject.State.PendingClients;

        clients.Single(client => client.ClientId == _clientId).DataStoreIds.Should().Equal(_dataStoreId1);
        clients
            .Single(client => client.ClientId == _secondClientId)
            .DataStoreIds.Should()
            .Equal(_dataStoreId2);
    }

    [Test]
    public async Task It_reports_the_aggregate_data_stores()
    {
        var getResult = await _applicationRepository.GetApplication(_applicationId);

        ((ApplicationGetResult.Success)getResult)
            .ApplicationResponse.DataStoreIds.Should()
            .BeEquivalentTo([_dataStoreId1, _dataStoreId2]);
    }

    [Test]
    public async Task It_applies_the_application_scoped_fields()
    {
        var getResult = await _applicationRepository.GetApplication(_applicationId);

        var application = ((ApplicationGetResult.Success)getResult).ApplicationResponse;
        application.ApplicationName.Should().Be("Updated Consistency Application");
        application.ClaimSetName.Should().Be("UpdatedClaimSet");
        application.EducationOrganizationIds.Should().Equal(300L);
    }

    [Test]
    public async Task It_keeps_every_clients_uuid()
    {
        (await ReadStoredClientUuidAsync(_clientId)).Should().Be(_clientUuid);
        (await ReadStoredClientUuidAsync(_secondClientId)).Should().Be(_secondClientUuid);
    }
}

[TestFixture]
public class Given_an_api_client_resolution_state_read : ConsistencyOperationTestBase
{
    private ApiClientResolutionResult _result = null!;

    [SetUp]
    public async Task Act() =>
        _result = await _apiClientRepository.GetApiClientResolutionState(_secondClientRowId);

    [Test]
    public void It_returns_the_complete_client_state()
    {
        var state = ((ApiClientResolutionResult.Success)_result).State;
        state.ApplicationId.Should().Be(_applicationId);
        state.Name.Should().Be("Second Client");
        state.IsApproved.Should().BeTrue();
        state.ClientId.Should().Be(_secondClientId);
        state.ClientUuid.Should().Be(_secondClientUuid);
    }

    [Test]
    public void It_returns_the_exact_data_store_set()
    {
        var state = ((ApiClientResolutionResult.Success)_result).State;
        state.DataStoreIds.Should().Equal(_dataStoreId2);
    }
}

[TestFixture]
public class Given_an_api_client_resolution_state_read_for_a_missing_row : ConsistencyOperationTestBase
{
    private ApiClientResolutionResult _result = null!;

    [SetUp]
    public async Task Act() => _result = await _apiClientRepository.GetApiClientResolutionState(999999);

    [Test]
    public void It_returns_not_exists() =>
        _result.Should().BeOfType<ApiClientResolutionResult.FailureNotExists>();
}

[TestFixture]
public class Given_an_api_client_uuid_sync_with_the_expected_stored_value : ConsistencyOperationTestBase
{
    private Guid _newUuid;
    private ApiClientUuidSyncResult _result = null!;
    private Guid _storedUuid;

    [SetUp]
    public async Task Act()
    {
        _newUuid = Guid.NewGuid();
        _result = await _apiClientRepository.SyncApiClientUuid(
            _secondClientRowId,
            _secondClientUuid,
            _newUuid
        );
        _storedUuid = await ReadStoredClientUuidAsync(_secondClientId);
    }

    [Test]
    public void It_returns_success() => _result.Should().BeOfType<ApiClientUuidSyncResult.Success>();

    [Test]
    public void It_persists_the_new_uuid() => _storedUuid.Should().Be(_newUuid);
}

[TestFixture]
public class Given_an_api_client_uuid_sync_against_a_stale_row : ConsistencyOperationTestBase
{
    private ApiClientUuidSyncResult _result = null!;
    private Guid _storedUuid;

    [SetUp]
    public async Task Act()
    {
        _result = await _apiClientRepository.SyncApiClientUuid(
            _secondClientRowId,
            Guid.NewGuid(),
            Guid.NewGuid()
        );
        _storedUuid = await ReadStoredClientUuidAsync(_secondClientId);
    }

    [Test]
    public void It_returns_stale_state() =>
        _result.Should().BeOfType<ApiClientUuidSyncResult.FailureStaleState>();

    [Test]
    public void It_does_not_overwrite_the_stored_uuid() => _storedUuid.Should().Be(_secondClientUuid);
}

[TestFixture]
public class Given_an_api_client_uuid_sync_for_a_missing_row : ConsistencyOperationTestBase
{
    private ApiClientUuidSyncResult _unreferencedResult = null!;
    private ApiClientUuidSyncResult _referencedResult = null!;

    [SetUp]
    public async Task Act()
    {
        _unreferencedResult = await _apiClientRepository.SyncApiClientUuid(
            999999,
            Guid.NewGuid(),
            Guid.NewGuid()
        );
        _referencedResult = await _apiClientRepository.SyncApiClientUuid(999999, Guid.NewGuid(), _clientUuid);
    }

    [Test]
    public void It_reports_an_unreferenced_uuid_as_safe_to_delete() =>
        _unreferencedResult.Should().BeOfType<ApiClientUuidSyncResult.FailureNotExistsSafeToDelete>();

    [Test]
    public void It_reports_a_referenced_uuid_without_deletion_permission() =>
        _referencedResult.Should().BeOfType<ApiClientUuidSyncResult.FailureNotExists>();
}

[TestFixture]
public class Given_a_client_uuid_reference_check : ConsistencyOperationTestBase
{
    private ApiClientUuidReferenceResult _referencedResult = null!;
    private ApiClientUuidReferenceResult _unreferencedResult = null!;

    [SetUp]
    public async Task Act()
    {
        _referencedResult = await _apiClientRepository.HasApiClientUuidReference(_clientUuid);
        _unreferencedResult = await _apiClientRepository.HasApiClientUuidReference(Guid.NewGuid());
    }

    [Test]
    public void It_reports_a_stored_uuid_as_referenced() =>
        _referencedResult.Should().BeOfType<ApiClientUuidReferenceResult.Referenced>();

    [Test]
    public void It_reports_an_unknown_uuid_as_unreferenced() =>
        _unreferencedResult.Should().BeOfType<ApiClientUuidReferenceResult.None>();
}

[TestFixture]
public class Given_an_api_client_uuid_sync_that_was_already_applied : ConsistencyOperationTestBase
{
    private ApiClientUuidSyncResult _result = null!;
    private Guid _storedUuid;

    [SetUp]
    public async Task Act()
    {
        _result = await _apiClientRepository.SyncApiClientUuid(
            _secondClientRowId,
            Guid.NewGuid(),
            _secondClientUuid
        );
        _storedUuid = await ReadStoredClientUuidAsync(_secondClientId);
    }

    [Test]
    public void It_returns_already_applied() =>
        _result.Should().BeOfType<ApiClientUuidSyncResult.AlreadyApplied>();

    [Test]
    public void It_leaves_the_stored_uuid_unchanged() => _storedUuid.Should().Be(_secondClientUuid);
}

[TestFixture]
public class Given_an_api_client_resolution_state_read_during_an_uncommitted_update
    : ConsistencyOperationTestBase
{
    private bool _completedWhileWriterHeldLock;
    private ApiClientResolutionResult _result = null!;

    [SetUp]
    public async Task Act()
    {
        await using var writer = await Connection!.BeginTransactionAsync();
        await Connection.ExecuteAsync(
            """UPDATE dmscs.ApiClient SET Name = 'Changed In Flight' WHERE Id = @Id;""",
            new { Id = _secondClientRowId },
            writer
        );

        Task<ApiClientResolutionResult> reading = _apiClientRepository.GetApiClientResolutionState(
            _secondClientRowId
        );
        await Task.Delay(300);
        _completedWhileWriterHeldLock = reading.IsCompleted;

        await writer.CommitAsync();
        _result = await reading;
    }

    [Test]
    public void It_waits_for_the_in_flight_transaction() => _completedWhileWriterHeldLock.Should().BeFalse();

    [Test]
    public void It_reads_the_committed_state() =>
        ((ApiClientResolutionResult.Success)_result).State.Name.Should().Be("Changed In Flight");
}

[TestFixture]
public class Given_an_api_client_uuid_sync_blocked_by_a_concurrent_writer : ConsistencyOperationTestBase
{
    private Guid _interloperUuid;
    private bool _completedWhileWriterHeldLock;
    private ApiClientUuidSyncResult _result = null!;
    private Guid _storedUuid;

    [SetUp]
    public async Task Act()
    {
        _interloperUuid = Guid.NewGuid();
        await using var writer = await Connection!.BeginTransactionAsync();
        await Connection.ExecuteAsync(
            """UPDATE dmscs.ApiClient SET ClientUuid = @Uuid WHERE Id = @Id;""",
            new { Uuid = _interloperUuid, Id = _secondClientRowId },
            writer
        );

        Task<ApiClientUuidSyncResult> syncing = _apiClientRepository.SyncApiClientUuid(
            _secondClientRowId,
            _secondClientUuid,
            Guid.NewGuid()
        );
        await Task.Delay(300);
        _completedWhileWriterHeldLock = syncing.IsCompleted;

        await writer.CommitAsync();
        _result = await syncing;
        _storedUuid = await ReadStoredClientUuidAsync(_secondClientId);
    }

    [Test]
    public void It_waits_for_the_concurrent_writer() => _completedWhileWriterHeldLock.Should().BeFalse();

    [Test]
    public void It_returns_stale_state() =>
        _result.Should().BeOfType<ApiClientUuidSyncResult.FailureStaleState>();

    [Test]
    public void It_preserves_the_concurrent_writers_value() => _storedUuid.Should().Be(_interloperUuid);
}

/// <summary>
/// Pauses the uuid sync inside its transaction, after the lock-taking read, by blocking the
/// first audit-timestamp call the UPDATE parameters make.
/// </summary>
public sealed class BarrierAuditContext : EdFi.DmsConfigurationService.DataModel.Infrastructure.IAuditContext
{
    private readonly SemaphoreSlim _entered = new(0);
    private readonly SemaphoreSlim _release = new(0);
    private int _fired;

    public string GetCurrentUser() => "barrier-user";

    public DateTime GetCurrentTimestamp()
    {
        if (Interlocked.Exchange(ref _fired, 1) == 0)
        {
            _entered.Release();
            _release.Wait(TimeSpan.FromSeconds(30));
        }

        return DateTime.UtcNow;
    }

    public Task<bool> WaitUntilEnteredAsync() => _entered.WaitAsync(TimeSpan.FromSeconds(30));

    public void ReleaseBarrier() => _release.Release();
}

[TestFixture]
public class Given_a_second_writer_during_an_api_client_uuid_sync : ConsistencyOperationTestBase
{
    private Guid _newUuid;
    private bool _writerCompletedWhileSyncHeldLock;
    private ApiClientUuidSyncResult _syncResult = null!;
    private Guid _storedUuid;
    private string _finalName = null!;

    [SetUp]
    public async Task Act()
    {
        _newUuid = Guid.NewGuid();
        var barrier = new BarrierAuditContext();
        IApiClientRepository pausingRepository = new ApiClientRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<ApiClientRepository>.Instance,
            barrier,
            new TenantContextProvider()
        );

        Task<ApiClientUuidSyncResult> syncing = pausingRepository.SyncApiClientUuid(
            _secondClientRowId,
            _secondClientUuid,
            _newUuid
        );
        (await barrier.WaitUntilEnteredAsync()).Should().BeTrue();

        Task<int> writing = WriteClientNameAsync();
        await Task.Delay(300);
        _writerCompletedWhileSyncHeldLock = writing.IsCompleted;

        barrier.ReleaseBarrier();
        _syncResult = await syncing;
        await writing;

        _storedUuid = await ReadStoredClientUuidAsync(_secondClientId);
        _finalName = (
            await Connection!.ExecuteScalarAsync<string>(
                """SELECT Name FROM dmscs.ApiClient WHERE Id = @Id;""",
                new { Id = _secondClientRowId }
            )
        )!;
    }

    private async Task<int> WriteClientNameAsync()
    {
        await using var writerConnection = await OpenConnectionAsync();
        return await writerConnection.ExecuteAsync(
            """UPDATE dmscs.ApiClient SET Name = 'Interloper' WHERE Id = @Id;""",
            new { Id = _secondClientRowId }
        );
    }

    [Test]
    public void It_blocks_the_writer_while_the_sync_transaction_is_open() =>
        _writerCompletedWhileSyncHeldLock.Should().BeFalse();

    [Test]
    public void It_completes_the_sync()
    {
        _syncResult.Should().BeOfType<ApiClientUuidSyncResult.Success>();
        _storedUuid.Should().Be(_newUuid);
    }

    [Test]
    public void It_applies_the_writer_afterwards() => _finalName.Should().Be("Interloper");
}

public abstract class ForeignTenantOperationTestBase : ConsistencyOperationTestBase
{
    protected int _foreignApplicationId;
    protected string _foreignClientId = null!;
    protected Guid _foreignClientUuid;
    protected int _foreignClientRowId;

    [SetUp]
    public async Task SeedForeignTenantAggregateAsync()
    {
        _foreignClientId = Guid.NewGuid().ToString();
        _foreignClientUuid = Guid.NewGuid();

        long foreignTenantId = await Connection!.ExecuteScalarAsync<long>(
            """INSERT INTO dmscs.Tenant (Name) OUTPUT INSERTED.Id VALUES (@Name);""",
            new { Name = $"foreign-tenant-{Guid.NewGuid():N}" }
        );

        var foreignProvider = new TenantContextProvider
        {
            Context = new TenantContext.Multitenant(foreignTenantId, "foreign-tenant"),
        };

        IVendorRepository foreignVendorRepository = new VendorRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<VendorRepository>.Instance,
            new TestAuditContext(),
            foreignProvider
        );
        var vendorResult = await foreignVendorRepository.InsertVendor(
            new VendorInsertCommand
            {
                Company = "Foreign Tenant Company",
                ContactEmailAddress = "test@test.com",
                ContactName = "Fake Name",
                NamespacePrefixes = "ForeignPrefix",
            }
        );
        vendorResult.Should().BeOfType<VendorInsertResult.Success>();
        int foreignVendorId = ((VendorInsertResult.Success)vendorResult).Id;

        IApplicationRepository foreignApplicationRepository = new ApplicationRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<ApplicationRepository>.Instance,
            new TestAuditContext(),
            foreignProvider
        );
        var applicationResult = await foreignApplicationRepository.InsertApplication(
            new ApplicationInsertCommand
            {
                ApplicationName = "Foreign Tenant Application",
                VendorId = foreignVendorId,
                ClaimSetName = "ForeignClaimSet",
                EducationOrganizationIds = [300],
            },
            new() { ClientId = _foreignClientId, ClientUuid = _foreignClientUuid }
        );
        applicationResult.Should().BeOfType<ApplicationInsertResult.Success>();
        _foreignApplicationId = ((ApplicationInsertResult.Success)applicationResult).Id;

        _foreignClientRowId = await Connection!.ExecuteScalarAsync<int>(
            """SELECT Id FROM dmscs.ApiClient WHERE ClientId = @ClientId;""",
            new { ClientId = _foreignClientId }
        );
    }
}

[TestFixture]
public class Given_consistency_operations_against_a_foreign_tenant_aggregate : ForeignTenantOperationTestBase
{
    private ApplicationSyncStateResult _stateResult = null!;
    private ApiClientResolutionResult _resolutionResult = null!;
    private ApiClientUuidSyncResult _apiClientSyncResult = null!;
    private Guid _foreignStoredUuid;

    [SetUp]
    public async Task Act()
    {
        _stateResult = await _applicationRepository.GetApplicationSyncState(_foreignApplicationId);
        _resolutionResult = await _apiClientRepository.GetApiClientResolutionState(_foreignClientRowId);
        _apiClientSyncResult = await _apiClientRepository.SyncApiClientUuid(
            _foreignClientRowId,
            _foreignClientUuid,
            Guid.NewGuid()
        );
        _foreignStoredUuid = await ReadStoredClientUuidAsync(_foreignClientId);
    }

    [Test]
    public void It_hides_the_foreign_application_state() =>
        _stateResult.Should().BeOfType<ApplicationSyncStateResult.FailureNotExists>();

    [Test]
    public void It_hides_the_foreign_api_client_state() =>
        _resolutionResult.Should().BeOfType<ApiClientResolutionResult.FailureNotExists>();

    [Test]
    public void It_reports_the_invisible_sync_target_with_a_fresh_uuid_as_safe_to_delete() =>
        _apiClientSyncResult.Should().BeOfType<ApiClientUuidSyncResult.FailureNotExistsSafeToDelete>();

    [Test]
    public void It_leaves_the_foreign_rows_unchanged() => _foreignStoredUuid.Should().Be(_foreignClientUuid);
}

[TestFixture]
public class Given_a_uuid_referenced_only_by_a_foreign_tenant : ForeignTenantOperationTestBase
{
    private ApiClientUuidSyncResult _apiClientSyncResult = null!;
    private ApiClientUuidReferenceResult _referenceResult = null!;

    [SetUp]
    public async Task Act()
    {
        // The reference check protecting a provider-level deletion is deliberately
        // cross-tenant: a UUID stored only by another tenant must still forbid deletion.
        _apiClientSyncResult = await _apiClientRepository.SyncApiClientUuid(
            999999,
            Guid.NewGuid(),
            _foreignClientUuid
        );
        _referenceResult = await _apiClientRepository.HasApiClientUuidReference(_foreignClientUuid);
    }

    [Test]
    public void It_forbids_deletion_from_the_api_client_sync() =>
        _apiClientSyncResult.Should().BeOfType<ApiClientUuidSyncResult.FailureNotExists>();

    [Test]
    public void It_reports_the_foreign_uuid_as_referenced() =>
        _referenceResult.Should().BeOfType<ApiClientUuidReferenceResult.Referenced>();
}

[TestFixture]
public class Given_a_second_writer_during_an_api_client_resolution_read : ConsistencyOperationTestBase
{
    private bool _writerCompletedWhileReadHeldLock;
    private ApiClientResolutionResult _readResult = null!;
    private string _finalName = null!;

    [SetUp]
    public async Task Act()
    {
        // Holding the client's data store mapping row makes the resolution read pause after
        // it has locked the ApiClient row, so a second writer must queue behind that lock.
        // (SQL Server reads block behind the held row; this seam has no PostgreSQL analogue
        // because MVCC snapshot reads do not block.)
        await using var mappingHolder = await Connection!.BeginTransactionAsync();
        await Connection.ExecuteAsync(
            "UPDATE dmscs.ApiClientDataStore SET DataStoreId = DataStoreId WHERE ApiClientId = @Id;",
            new { Id = _secondClientRowId },
            mappingHolder
        );

        Task<ApiClientResolutionResult> reading = _apiClientRepository.GetApiClientResolutionState(
            _secondClientRowId
        );
        await Task.Delay(300);

        Task<int> writing = WriteClientNameAfterLockAsync();
        await Task.Delay(300);
        _writerCompletedWhileReadHeldLock = writing.IsCompleted;

        await mappingHolder.RollbackAsync();
        _readResult = await reading;
        await writing;

        _finalName = (
            await Connection.ExecuteScalarAsync<string>(
                "SELECT Name FROM dmscs.ApiClient WHERE Id = @Id;",
                new { Id = _secondClientRowId }
            )
        )!;
    }

    private async Task<int> WriteClientNameAfterLockAsync()
    {
        await using var writerConnection = await OpenConnectionAsync();
        return await writerConnection.ExecuteAsync(
            "UPDATE dmscs.ApiClient SET Name = 'Written After Read Lock' WHERE Id = @Id;",
            new { Id = _secondClientRowId }
        );
    }

    [Test]
    public void It_blocks_the_writer_until_the_read_completes() =>
        _writerCompletedWhileReadHeldLock.Should().BeFalse();

    [Test]
    public void It_reads_the_pre_writer_state() =>
        ((ApiClientResolutionResult.Success)_readResult).State.Name.Should().Be("Second Client");

    [Test]
    public void It_applies_the_writer_afterwards() => _finalName.Should().Be("Written After Read Lock");
}

/// <summary>
/// DMS-1529: the target row exists and matches the expected UUID, but the new UUID already belongs to
/// another API client of the same tenant. The sync is a classified failure that writes nothing, so the
/// caller knows the provider client behind the new UUID is not its own to delete.
/// </summary>
[TestFixture]
public class Given_a_sync_to_a_uuid_another_api_client_holds : ConsistencyOperationTestBase
{
    private ApiClientUuidSyncResult _apiClientSyncResult = null!;
    private Guid _secondStoredUuid;
    private Guid _firstStoredUuid;

    [SetUp]
    public async Task Act()
    {
        _apiClientSyncResult = await _apiClientRepository.SyncApiClientUuid(
            _secondClientRowId,
            _secondClientUuid,
            _clientUuid
        );
        _secondStoredUuid = await ReadStoredClientUuidAsync(_secondClientId);
        _firstStoredUuid = await ReadStoredClientUuidAsync(_clientId);
    }

    [Test]
    public void It_classifies_the_api_client_sync_as_a_uuid_in_use() =>
        _apiClientSyncResult.Should().BeOfType<ApiClientUuidSyncResult.FailureNewUuidInUse>();

    [Test]
    public void It_leaves_both_rows_unchanged()
    {
        _secondStoredUuid.Should().Be(_secondClientUuid);
        _firstStoredUuid.Should().Be(_clientUuid);
    }
}

/// <summary>
/// DMS-1529: the same collision against a UUID held only by another tenant's client. The constraint is
/// global, so the sync is rejected the same way, and neither tenant's row changes.
/// </summary>
[TestFixture]
public class Given_a_sync_to_a_uuid_held_by_a_foreign_tenant : ForeignTenantOperationTestBase
{
    private ApiClientUuidSyncResult _apiClientSyncResult = null!;
    private Guid _secondStoredUuid;
    private Guid _foreignStoredUuid;

    [SetUp]
    public async Task Act()
    {
        _apiClientSyncResult = await _apiClientRepository.SyncApiClientUuid(
            _secondClientRowId,
            _secondClientUuid,
            _foreignClientUuid
        );
        _secondStoredUuid = await ReadStoredClientUuidAsync(_secondClientId);
        _foreignStoredUuid = await ReadStoredClientUuidAsync(_foreignClientId);
    }

    [Test]
    public void It_classifies_the_api_client_sync_as_a_uuid_in_use() =>
        _apiClientSyncResult.Should().BeOfType<ApiClientUuidSyncResult.FailureNewUuidInUse>();

    [Test]
    public void It_leaves_both_tenants_rows_unchanged()
    {
        _secondStoredUuid.Should().Be(_secondClientUuid);
        _foreignStoredUuid.Should().Be(_foreignClientUuid);
    }
}
