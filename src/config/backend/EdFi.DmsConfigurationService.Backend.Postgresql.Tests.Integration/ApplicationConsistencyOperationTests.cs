// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

public abstract class ConsistencyOperationTestBase : DatabaseTest
{
    protected readonly IApplicationRepository _applicationRepository = new ApplicationRepository(
        Configuration.DatabaseOptions,
        NullLogger<ApplicationRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    protected readonly IApiClientRepository _apiClientRepository = new ApiClientRepository(
        Configuration.DatabaseOptions,
        NullLogger<ApiClientRepository>.Instance,
        new TestAuditContext(),
        new TenantContextProvider()
    );

    protected long _vendorId;
    protected long _profileId;
    protected long _applicationId;
    protected string _clientId = null!;
    protected Guid _clientUuid;
    protected string _secondClientId = null!;
    protected Guid _secondClientUuid;
    protected long _secondClientRowId;
    protected long _dataStoreId1;
    protected long _dataStoreId2;

    [SetUp]
    public async Task SeedAggregateAsync()
    {
        _clientId = Guid.NewGuid().ToString();
        _clientUuid = Guid.NewGuid();
        _secondClientId = Guid.NewGuid().ToString();
        _secondClientUuid = Guid.NewGuid();

        IVendorRepository vendorRepository = new VendorRepository(
            Configuration.DatabaseOptions,
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

        _dataStoreId1 = await Connection!.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dmscs"."DataStore" ("DataStoreType", "Name")
            VALUES ('Production', 'Consistency Store One') RETURNING "Id";
            """
        );
        _dataStoreId2 = await Connection!.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dmscs"."DataStore" ("DataStoreType", "Name")
            VALUES ('Production', 'Consistency Store Two') RETURNING "Id";
            """
        );

        _profileId = await Connection!.ExecuteScalarAsync<long>(
            """
            INSERT INTO "dmscs"."Profile" ("ProfileName", "Definition")
            VALUES (@ProfileName, '<Profile/>') RETURNING "Id";
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
            """SELECT "ClientUuid" FROM "dmscs"."ApiClient" WHERE "ClientId" = @ClientId;""",
            new { ClientId = clientId }
        );
}

[TestFixture]
public class Given_an_application_update_state_read : ConsistencyOperationTestBase
{
    private ApplicationUpdateStateResult _result = null!;

    [SetUp]
    public async Task Act() =>
        _result = await _applicationRepository.GetApplicationUpdateState(_applicationId, _clientId);

    [Test]
    public void It_returns_the_application_scalars()
    {
        var state = ((ApplicationUpdateStateResult.Success)_result).State;
        state.ApplicationName.Should().Be("Consistency Test Application");
        state.ClaimSetName.Should().Be("ConsistencyClaimSet");
    }

    [Test]
    public void It_returns_the_vendor_reference()
    {
        var state = ((ApplicationUpdateStateResult.Success)_result).State;
        state.VendorId.Should().Be(_vendorId);
    }

    [Test]
    public void It_returns_the_mapping_sets()
    {
        var state = ((ApplicationUpdateStateResult.Success)_result).State;
        state.EducationOrganizationIds.Should().BeEquivalentTo([100L, 200L]);
        state.ProfileIds.Should().Equal(_profileId);
    }

    [Test]
    public void It_returns_the_selected_clients_identity()
    {
        var state = ((ApplicationUpdateStateResult.Success)_result).State;
        state.ClientId.Should().Be(_clientId);
        state.ClientUuid.Should().Be(_clientUuid);
        state.IsApproved.Should().BeTrue();
    }

    [Test]
    public void It_returns_only_the_selected_clients_data_stores()
    {
        // The aggregate read unions data stores across every client; the update state must
        // carry exactly the selected client's set.
        var state = ((ApplicationUpdateStateResult.Success)_result).State;
        state.ClientDataStoreIds.Should().Equal(_dataStoreId1);
    }
}

[TestFixture]
public class Given_an_application_update_state_read_for_a_missing_application : ConsistencyOperationTestBase
{
    private ApplicationUpdateStateResult _result = null!;

    [SetUp]
    public async Task Act() =>
        _result = await _applicationRepository.GetApplicationUpdateState(999999, _clientId);

    [Test]
    public void It_returns_not_exists() =>
        _result.Should().BeOfType<ApplicationUpdateStateResult.FailureNotExists>();
}

[TestFixture]
public class Given_an_application_update_state_read_during_an_uncommitted_update
    : ConsistencyOperationTestBase
{
    private bool _completedWhileWriterHeldLock;
    private ApplicationUpdateStateResult _result = null!;

    [SetUp]
    public async Task Act()
    {
        await using var writer = await Connection!.BeginTransactionAsync();
        await Connection.ExecuteAsync(
            """UPDATE "dmscs"."Application" SET "ApplicationName" = 'Changed In Flight' WHERE "Id" = @Id;""",
            new { Id = _applicationId },
            writer
        );

        Task<ApplicationUpdateStateResult> reading = _applicationRepository.GetApplicationUpdateState(
            _applicationId,
            _clientId
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
        ((ApplicationUpdateStateResult.Success)_result)
            .State.ApplicationName.Should()
            .Be("Changed In Flight");
}

[TestFixture]
public class Given_a_client_uuid_sync_with_the_expected_stored_value : ConsistencyOperationTestBase
{
    private Guid _newUuid;
    private ApiClientUuidSyncResult _result = null!;
    private Guid _storedUuid;

    [SetUp]
    public async Task Act()
    {
        _newUuid = Guid.NewGuid();
        _result = await _applicationRepository.SyncApplicationApiClientUuid(
            _applicationId,
            _clientId,
            _clientUuid,
            _newUuid
        );
        _storedUuid = await ReadStoredClientUuidAsync(_clientId);
    }

    [Test]
    public void It_returns_success() => _result.Should().BeOfType<ApiClientUuidSyncResult.Success>();

    [Test]
    public void It_persists_the_new_uuid() => _storedUuid.Should().Be(_newUuid);
}

[TestFixture]
public class Given_a_client_uuid_sync_that_was_already_applied : ConsistencyOperationTestBase
{
    private ApiClientUuidSyncResult _result = null!;
    private Guid _storedUuid;

    [SetUp]
    public async Task Act()
    {
        _result = await _applicationRepository.SyncApplicationApiClientUuid(
            _applicationId,
            _clientId,
            Guid.NewGuid(),
            _clientUuid
        );
        _storedUuid = await ReadStoredClientUuidAsync(_clientId);
    }

    [Test]
    public void It_returns_already_applied() =>
        _result.Should().BeOfType<ApiClientUuidSyncResult.AlreadyApplied>();

    [Test]
    public void It_leaves_the_stored_uuid_unchanged() => _storedUuid.Should().Be(_clientUuid);
}

[TestFixture]
public class Given_a_client_uuid_sync_against_a_stale_row : ConsistencyOperationTestBase
{
    private ApiClientUuidSyncResult _result = null!;
    private Guid _storedUuid;

    [SetUp]
    public async Task Act()
    {
        _result = await _applicationRepository.SyncApplicationApiClientUuid(
            _applicationId,
            _clientId,
            Guid.NewGuid(),
            Guid.NewGuid()
        );
        _storedUuid = await ReadStoredClientUuidAsync(_clientId);
    }

    [Test]
    public void It_returns_stale_state() =>
        _result.Should().BeOfType<ApiClientUuidSyncResult.FailureStaleState>();

    [Test]
    public void It_does_not_overwrite_the_stored_uuid() => _storedUuid.Should().Be(_clientUuid);
}

[TestFixture]
public class Given_a_client_uuid_sync_for_a_missing_row_with_an_unreferenced_uuid
    : ConsistencyOperationTestBase
{
    private ApiClientUuidSyncResult _result = null!;

    [SetUp]
    public async Task Act() =>
        _result = await _applicationRepository.SyncApplicationApiClientUuid(
            _applicationId,
            "no-such-client",
            Guid.NewGuid(),
            Guid.NewGuid()
        );

    [Test]
    public void It_reports_the_missing_row_as_safe_to_delete() =>
        _result.Should().BeOfType<ApiClientUuidSyncResult.FailureNotExistsSafeToDelete>();
}

[TestFixture]
public class Given_a_client_uuid_sync_for_a_missing_row_with_a_referenced_uuid : ConsistencyOperationTestBase
{
    private ApiClientUuidSyncResult _result = null!;

    [SetUp]
    public async Task Act() =>
        // The "new" UUID is already stored on the second client, so deletion is unsafe.
        _result = await _applicationRepository.SyncApplicationApiClientUuid(
            _applicationId,
            "no-such-client",
            Guid.NewGuid(),
            _secondClientUuid
        );

    [Test]
    public void It_reports_the_missing_row_without_deletion_permission() =>
        _result.Should().BeOfType<ApiClientUuidSyncResult.FailureNotExists>();
}

[TestFixture]
public class Given_a_client_uuid_sync_blocked_by_a_concurrent_writer : ConsistencyOperationTestBase
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
            """UPDATE "dmscs"."ApiClient" SET "ClientUuid" = @Uuid WHERE "ClientId" = @ClientId;""",
            new { Uuid = _interloperUuid, ClientId = _clientId },
            writer
        );

        Task<ApiClientUuidSyncResult> syncing = _applicationRepository.SyncApplicationApiClientUuid(
            _applicationId,
            _clientId,
            _clientUuid,
            Guid.NewGuid()
        );
        await Task.Delay(300);
        _completedWhileWriterHeldLock = syncing.IsCompleted;

        await writer.CommitAsync();
        _result = await syncing;
        _storedUuid = await ReadStoredClientUuidAsync(_clientId);
    }

    [Test]
    public void It_waits_for_the_concurrent_writer() => _completedWhileWriterHeldLock.Should().BeFalse();

    [Test]
    public void It_returns_stale_state() =>
        _result.Should().BeOfType<ApiClientUuidSyncResult.FailureStaleState>();

    [Test]
    public void It_preserves_the_concurrent_writers_value() => _storedUuid.Should().Be(_interloperUuid);
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
            """UPDATE "dmscs"."ApiClient" SET "Name" = 'Changed In Flight' WHERE "Id" = @Id;""",
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
            """UPDATE "dmscs"."ApiClient" SET "ClientUuid" = @Uuid WHERE "Id" = @Id;""",
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

[TestFixture]
public class Given_a_second_writer_during_an_application_state_read : ConsistencyOperationTestBase
{
    private bool _writerCompletedWhileReadHeldLock;
    private ApplicationUpdateStateResult _readResult = null!;
    private string _finalClaimSetName = null!;

    [SetUp]
    public async Task Act()
    {
        // Holding the client row makes the state read pause after it has locked the
        // Application row, so a second writer must queue behind that held lock.
        await using var clientRowHolder = await Connection!.BeginTransactionAsync();
        await Connection.ExecuteAsync(
            """SELECT "Id" FROM "dmscs"."ApiClient" WHERE "ClientId" = @ClientId FOR UPDATE;""",
            new { ClientId = _clientId },
            clientRowHolder
        );

        Task<ApplicationUpdateStateResult> reading = _applicationRepository.GetApplicationUpdateState(
            _applicationId,
            _clientId
        );
        await Task.Delay(300);

        Task<int> writing = WriteClaimSetNameAsync();
        await Task.Delay(300);
        _writerCompletedWhileReadHeldLock = writing.IsCompleted;

        await clientRowHolder.RollbackAsync();
        _readResult = await reading;
        await writing;

        _finalClaimSetName = (
            await Connection.ExecuteScalarAsync<string>(
                """SELECT "ClaimSetName" FROM "dmscs"."Application" WHERE "Id" = @Id;""",
                new { Id = _applicationId }
            )
        )!;
    }

    private async Task<int> WriteClaimSetNameAsync()
    {
        await using var writerConnection = await DataSource!.OpenConnectionAsync();
        return await writerConnection.ExecuteAsync(
            """UPDATE "dmscs"."Application" SET "ClaimSetName" = 'Written After Lock' WHERE "Id" = @Id;""",
            new { Id = _applicationId }
        );
    }

    [Test]
    public void It_blocks_the_writer_until_the_read_completes() =>
        _writerCompletedWhileReadHeldLock.Should().BeFalse();

    [Test]
    public void It_reads_the_pre_writer_state() =>
        ((ApplicationUpdateStateResult.Success)_readResult)
            .State.ClaimSetName.Should()
            .Be("ConsistencyClaimSet");

    [Test]
    public void It_applies_the_writer_afterwards() => _finalClaimSetName.Should().Be("Written After Lock");
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
            Configuration.DatabaseOptions,
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
                """SELECT "Name" FROM "dmscs"."ApiClient" WHERE "Id" = @Id;""",
                new { Id = _secondClientRowId }
            )
        )!;
    }

    private async Task<int> WriteClientNameAsync()
    {
        await using var writerConnection = await DataSource!.OpenConnectionAsync();
        return await writerConnection.ExecuteAsync(
            """UPDATE "dmscs"."ApiClient" SET "Name" = 'Interloper' WHERE "Id" = @Id;""",
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

[TestFixture]
public class Given_a_second_writer_during_an_application_client_uuid_sync : ConsistencyOperationTestBase
{
    private Guid _newUuid;
    private bool _writerCompletedWhileSyncHeldLock;
    private ApiClientUuidSyncResult _syncResult = null!;
    private Guid _storedUuid;
    private string _finalFirstClientName = null!;

    [SetUp]
    public async Task Act()
    {
        _newUuid = Guid.NewGuid();
        var barrier = new BarrierAuditContext();
        IApplicationRepository pausingRepository = new ApplicationRepository(
            Configuration.DatabaseOptions,
            NullLogger<ApplicationRepository>.Instance,
            barrier,
            new TenantContextProvider()
        );

        Task<ApiClientUuidSyncResult> syncing = pausingRepository.SyncApplicationApiClientUuid(
            _applicationId,
            _clientId,
            _clientUuid,
            _newUuid
        );
        (await barrier.WaitUntilEnteredAsync()).Should().BeTrue();

        Task<int> writing = WriteFirstClientNameAsync();
        await Task.Delay(300);
        _writerCompletedWhileSyncHeldLock = writing.IsCompleted;

        barrier.ReleaseBarrier();
        _syncResult = await syncing;
        await writing;

        _storedUuid = await ReadStoredClientUuidAsync(_clientId);
        _finalFirstClientName = (
            await Connection!.ExecuteScalarAsync<string>(
                """SELECT "Name" FROM "dmscs"."ApiClient" WHERE "ClientId" = @ClientId;""",
                new { ClientId = _clientId }
            )
        )!;
    }

    private async Task<int> WriteFirstClientNameAsync()
    {
        await using var writerConnection = await DataSource!.OpenConnectionAsync();
        return await writerConnection.ExecuteAsync(
            """UPDATE "dmscs"."ApiClient" SET "Name" = 'Interloper Two' WHERE "ClientId" = @ClientId;""",
            new { ClientId = _clientId }
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
    public void It_applies_the_writer_afterwards() => _finalFirstClientName.Should().Be("Interloper Two");
}

public abstract class ForeignTenantOperationTestBase : ConsistencyOperationTestBase
{
    protected long _foreignApplicationId;
    protected string _foreignClientId = null!;
    protected Guid _foreignClientUuid;
    protected long _foreignClientRowId;

    [SetUp]
    public async Task SeedForeignTenantAggregateAsync()
    {
        _foreignClientId = Guid.NewGuid().ToString();
        _foreignClientUuid = Guid.NewGuid();

        long foreignTenantId = await Connection!.ExecuteScalarAsync<long>(
            """INSERT INTO "dmscs"."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
            new { Name = $"foreign-tenant-{Guid.NewGuid():N}" }
        );

        var foreignProvider = new TenantContextProvider
        {
            Context = new TenantContext.Multitenant(foreignTenantId, "foreign-tenant"),
        };

        IVendorRepository foreignVendorRepository = new VendorRepository(
            Configuration.DatabaseOptions,
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
        long foreignVendorId = ((VendorInsertResult.Success)vendorResult).Id;

        IApplicationRepository foreignApplicationRepository = new ApplicationRepository(
            Configuration.DatabaseOptions,
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

        _foreignClientRowId = await Connection!.ExecuteScalarAsync<long>(
            """SELECT "Id" FROM "dmscs"."ApiClient" WHERE "ClientId" = @ClientId;""",
            new { ClientId = _foreignClientId }
        );
    }
}

[TestFixture]
public class Given_consistency_operations_against_a_foreign_tenant_aggregate : ForeignTenantOperationTestBase
{
    private ApplicationUpdateStateResult _stateResult = null!;
    private ApiClientResolutionResult _resolutionResult = null!;
    private ApiClientUuidSyncResult _applicationSyncResult = null!;
    private ApiClientUuidSyncResult _apiClientSyncResult = null!;
    private Guid _foreignStoredUuid;

    [SetUp]
    public async Task Act()
    {
        _stateResult = await _applicationRepository.GetApplicationUpdateState(
            _foreignApplicationId,
            _foreignClientId
        );
        _resolutionResult = await _apiClientRepository.GetApiClientResolutionState(_foreignClientRowId);
        _applicationSyncResult = await _applicationRepository.SyncApplicationApiClientUuid(
            _foreignApplicationId,
            _foreignClientId,
            _foreignClientUuid,
            Guid.NewGuid()
        );
        _apiClientSyncResult = await _apiClientRepository.SyncApiClientUuid(
            _foreignClientRowId,
            _foreignClientUuid,
            Guid.NewGuid()
        );
        _foreignStoredUuid = await ReadStoredClientUuidAsync(_foreignClientId);
    }

    [Test]
    public void It_hides_the_foreign_application_state() =>
        _stateResult.Should().BeOfType<ApplicationUpdateStateResult.FailureNotExists>();

    [Test]
    public void It_hides_the_foreign_api_client_state() =>
        _resolutionResult.Should().BeOfType<ApiClientResolutionResult.FailureNotExists>();

    [Test]
    public void It_reports_the_invisible_sync_targets_with_fresh_uuids_as_safe_to_delete()
    {
        _applicationSyncResult.Should().BeOfType<ApiClientUuidSyncResult.FailureNotExistsSafeToDelete>();
        _apiClientSyncResult.Should().BeOfType<ApiClientUuidSyncResult.FailureNotExistsSafeToDelete>();
    }

    [Test]
    public void It_leaves_the_foreign_rows_unchanged() => _foreignStoredUuid.Should().Be(_foreignClientUuid);
}

[TestFixture]
public class Given_a_uuid_referenced_only_by_a_foreign_tenant : ForeignTenantOperationTestBase
{
    private ApiClientUuidSyncResult _applicationSyncResult = null!;
    private ApiClientUuidSyncResult _apiClientSyncResult = null!;
    private ApiClientUuidReferenceResult _referenceResult = null!;

    [SetUp]
    public async Task Act()
    {
        // The reference check protecting a provider-level deletion is deliberately
        // cross-tenant: a UUID stored only by another tenant must still forbid deletion.
        _applicationSyncResult = await _applicationRepository.SyncApplicationApiClientUuid(
            _applicationId,
            "no-such-client",
            Guid.NewGuid(),
            _foreignClientUuid
        );
        _apiClientSyncResult = await _apiClientRepository.SyncApiClientUuid(
            999999,
            Guid.NewGuid(),
            _foreignClientUuid
        );
        _referenceResult = await _apiClientRepository.HasApiClientUuidReference(_foreignClientUuid);
    }

    [Test]
    public void It_forbids_deletion_from_the_application_sync() =>
        _applicationSyncResult.Should().BeOfType<ApiClientUuidSyncResult.FailureNotExists>();

    [Test]
    public void It_forbids_deletion_from_the_api_client_sync() =>
        _apiClientSyncResult.Should().BeOfType<ApiClientUuidSyncResult.FailureNotExists>();

    [Test]
    public void It_reports_the_foreign_uuid_as_referenced() =>
        _referenceResult.Should().BeOfType<ApiClientUuidReferenceResult.Referenced>();
}
