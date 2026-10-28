// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Profile;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The Application save that marks affected API clients as awaiting an identity provider update
/// (DMS-1642), over the seed of <see cref="ApiClientEducationOrganizationRepositoryTests"/>: the
/// scoped Application holds 1001, 1002 and 1003 with an inheriting client and two clients narrowed
/// to {1002} and {1001, 1003}; an unrelated Application of the same vendor holds 1001 with one client.
/// </summary>
public abstract class ApplicationSaveRepositoryTests : ApiClientEducationOrganizationRepositoryTests
{
    protected const string ScopedApplicationName = "Scoped Application";
    protected const string ScopedClaimSetName = "Test Claim set";

    protected DateTime SaveStartedAt { get; private set; }
    protected DateTime SaveFinishedAt { get; private set; }

    protected async Task<ApplicationSaveResult> SaveScopedApplicationAsync(
        long[] educationOrganizationIds,
        string applicationName = ScopedApplicationName,
        string claimSetName = ScopedClaimSetName,
        int? vendorId = null,
        int[]? profileIds = null
    )
    {
        SaveStartedAt = DateTime.UtcNow;
        ApplicationSaveResult result = await ApplicationRepository.SaveApplication(
            new ApplicationUpdateCommand
            {
                Id = ScopedApplicationId,
                ApplicationName = applicationName,
                VendorId = vendorId ?? VendorId,
                ClaimSetName = claimSetName,
                EducationOrganizationIds = educationOrganizationIds,
                ProfileIds = profileIds ?? [],
            }
        );
        SaveFinishedAt = DateTime.UtcNow;
        return result;
    }

    /// <summary>Every client awaiting an identity provider update, by id.</summary>
    protected async Task<Dictionary<int, DateTime>> PendingClientsAsync() =>
        (
            await Connection!.QueryAsync<(int Id, DateTime PendingSince)>(
                """
                SELECT Id, IdentityProviderSyncPendingSince FROM dmscs.ApiClient
                WHERE IdentityProviderSyncPendingSince IS NOT NULL;
                """
            )
        ).ToDictionary(row => row.Id, row => DateTime.SpecifyKind(row.PendingSince, DateTimeKind.Utc));

    protected static ApplicationSyncState ShouldSucceed(ApplicationSaveResult result) =>
        result.Should().BeOfType<ApplicationSaveResult.Success>().Subject.State;

    /// <summary>The pending clients' ids and effective education organizations, in id order.</summary>
    protected static (int Id, long[] EducationOrganizationIds)[] Scopes(ApplicationSyncState state) =>
        [.. state.PendingClients.Select(client => (client.Id, client.EducationOrganizationIds))];

    /// <summary>Asserts that exactly the given clients were newly marked, all at one time within the save.</summary>
    protected async Task ShouldHaveNewlyMarkedExactlyAsync(params int[] apiClientIds)
    {
        Dictionary<int, DateTime> pending = await PendingClientsAsync();
        pending.Keys.Should().BeEquivalentTo(apiClientIds);
        DateTime markedAt = pending.Values.Distinct().Should().ContainSingle().Subject;
        markedAt.Should().BeOnOrAfter(SaveStartedAt).And.BeOnOrBefore(SaveFinishedAt);
        markedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    /// <summary>
    /// Returns once a database session waits on <paramref name="blockingSession"/>; the bound only
    /// turns a write that never waits into a failure instead of a hang.
    /// </summary>
    protected static async Task WaitUntilBlockedByAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        short blockingSession,
        Task write
    )
    {
        using CancellationTokenSource bound = new(TimeSpan.FromSeconds(60));
        while (
            await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @BlockingSession;",
                new { BlockingSession = blockingSession },
                transaction
            ) == 0
        )
        {
            write.IsCompleted.Should().BeFalse("the write must wait on the uncommitted transaction");
            await Task.Delay(TimeSpan.FromMilliseconds(20), bound.Token);
        }
    }

    [TestFixture]
    public class Given_a_save_removing_an_education_organization : ApplicationSaveRepositoryTests
    {
        private ApplicationSyncState _state = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _state = ShouldSucceed(await SaveScopedApplicationAsync([1003, 1001]));
        }

        [Test]
        public async Task It_marks_the_inheriting_client_and_the_narrowed_client_that_held_it() =>
            await ShouldHaveNewlyMarkedExactlyAsync(InheritingClientId, NarrowedClientId);

        [Test]
        public void It_returns_those_clients_with_their_scopes_after_the_cascade() =>
            Scopes(_state)
                .Should()
                .BeEquivalentTo(
                    [
                        (InheritingClientId, new long[] { 1001, 1003 }),
                        (NarrowedClientId, Array.Empty<long>()),
                    ],
                    options => options.WithStrictOrdering()
                );

        [Test]
        public async Task It_returns_the_stored_marker_times() =>
            _state
                .PendingClients.Select(client => client.PendingSince)
                .Should()
                .Equal((await PendingClientsAsync()).OrderBy(pair => pair.Key).Select(pair => pair.Value));

        [Test]
        public void It_returns_the_committed_Application() =>
            _state
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        ApplicationName = ScopedApplicationName,
                        VendorId,
                        ClaimSetName = ScopedClaimSetName,
                        EducationOrganizationIds = new long[] { 1001, 1003 },
                        ProfileIds = Array.Empty<int>(),
                    },
                    options => options.ExcludingMissingMembers()
                );

        [Test]
        public async Task It_keeps_the_narrowed_client_narrowed() =>
            ShouldMatch(
                await StateAsync(NarrowedClientId),
                State(ScopedApplicationId, false, [], [], (await PendingClientsAsync())[NarrowedClientId])
            );
    }

    [TestFixture]
    public class Given_a_save_adding_an_education_organization : ApplicationSaveRepositoryTests
    {
        private ApplicationSyncState _state = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _state = ShouldSucceed(await SaveScopedApplicationAsync([1001, 1002, 1003, 1004]));
        }

        [Test]
        public async Task It_marks_only_the_inheriting_client() =>
            await ShouldHaveNewlyMarkedExactlyAsync(InheritingClientId);

        [Test]
        public void It_returns_the_inheriting_client_with_the_Application_list() =>
            Scopes(_state)
                .Should()
                .BeEquivalentTo([(InheritingClientId, new long[] { 1001, 1002, 1003, 1004 })]);
    }

    [TestFixture]
    public class Given_a_save_renaming_the_Application : ApplicationSaveRepositoryTests
    {
        private ApplicationSyncState _state = null!;
        private (int Id, string ClientId, Guid ClientUuid, bool IsApproved)[] _clients = [];
        private int[] _dataStoreIds = [];

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            await Connection!.ExecuteAsync(
                """
                INSERT INTO dmscs.DataStore (DataStoreType, Name) VALUES ('Production', 'First'), ('Production', 'Second');
                INSERT INTO dmscs.ApiClientDataStore (ApiClientId, DataStoreId)
                SELECT @NarrowedClientId, Id FROM dmscs.DataStore
                UNION ALL SELECT @SecondNarrowedClientId, Id FROM dmscs.DataStore WHERE Name = 'Second';
                UPDATE dmscs.ApiClient SET IsApproved = 0 WHERE Id = @SecondNarrowedClientId;
                """,
                new { NarrowedClientId, SecondNarrowedClientId }
            );
            _clients =
            [
                .. await Connection!.QueryAsync<(int, string, Guid, bool)>(
                    """SELECT Id, ClientId, ClientUuid, IsApproved FROM dmscs.ApiClient WHERE ApplicationId = @ApplicationId ORDER BY Id;""",
                    new { ApplicationId = ScopedApplicationId }
                ),
            ];
            _dataStoreIds =
            [
                .. await Connection!.QueryAsync<int>("""SELECT Id FROM dmscs.DataStore ORDER BY Name;"""),
            ];
            _state = ShouldSucceed(
                await SaveScopedApplicationAsync([1001, 1002, 1003], applicationName: "Renamed Application")
            );
        }

        [Test]
        public async Task It_marks_every_client_of_the_Application_and_no_other() =>
            await ShouldHaveNewlyMarkedExactlyAsync(
                InheritingClientId,
                NarrowedClientId,
                SecondNarrowedClientId
            );

        [Test]
        public void It_returns_each_client_with_its_own_scope() =>
            Scopes(_state)
                .Should()
                .BeEquivalentTo(
                    [
                        (InheritingClientId, new long[] { 1001, 1002, 1003 }),
                        (NarrowedClientId, new long[] { 1002 }),
                        (SecondNarrowedClientId, new long[] { 1001, 1003 }),
                    ],
                    options => options.WithStrictOrdering()
                );

        [Test]
        public void It_returns_each_client_with_its_key_uuid_approval_and_data_stores() =>
            _state
                .PendingClients.Select(client =>
                    (client.Id, client.ClientId, client.ClientUuid, client.IsApproved, client.DataStoreIds)
                )
                .Should()
                .BeEquivalentTo(
                    [
                        (
                            _clients[0].Id,
                            _clients[0].ClientId,
                            _clients[0].ClientUuid,
                            true,
                            Array.Empty<int>()
                        ),
                        (_clients[1].Id, _clients[1].ClientId, _clients[1].ClientUuid, true, _dataStoreIds),
                        (
                            _clients[2].Id,
                            _clients[2].ClientId,
                            _clients[2].ClientUuid,
                            false,
                            new[] { _dataStoreIds[1] }
                        ),
                    ],
                    options => options.WithStrictOrdering()
                );

        [Test]
        public void It_returns_the_new_name() => _state.ApplicationName.Should().Be("Renamed Application");
    }

    [TestFixture]
    public class Given_a_save_changing_the_claim_set : ApplicationSaveRepositoryTests
    {
        private ApplicationSyncState _state = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _state = ShouldSucceed(
                await SaveScopedApplicationAsync([1001, 1002, 1003], claimSetName: "Other Claim set")
            );
        }

        [Test]
        public async Task It_marks_every_client_of_the_Application_and_no_other() =>
            await ShouldHaveNewlyMarkedExactlyAsync(
                InheritingClientId,
                NarrowedClientId,
                SecondNarrowedClientId
            );

        [Test]
        public void It_returns_the_new_claim_set() => _state.ClaimSetName.Should().Be("Other Claim set");
    }

    [TestFixture]
    public class Given_a_save_changing_only_the_vendor_and_profiles : ApplicationSaveRepositoryTests
    {
        private ApplicationSyncState _state = null!;
        private int _otherVendorId;
        private int _profileId;

        [SetUp]
        public async Task Setup()
        {
            var tenantContextProvider = new TenantContextProvider();
            await SeedAsync(tenantContextProvider);
            _otherVendorId = (
                await new VendorRepository(
                    MssqlTestConfiguration.DatabaseOptions,
                    NullLogger<VendorRepository>.Instance,
                    new TestAuditContext(),
                    tenantContextProvider
                ).InsertVendor(
                    new VendorInsertCommand
                    {
                        Company = $"Other Vendor {Guid.NewGuid():N}",
                        ContactEmailAddress = "test@test.com",
                        ContactName = "Fake Name",
                        NamespacePrefixes = "",
                    }
                )
            ).Should().BeOfType<VendorInsertResult.Success>().Subject.Id;
            _profileId = (
                await new ProfileRepository(
                    MssqlTestConfiguration.DatabaseOptions,
                    NullLogger<ProfileRepository>.Instance,
                    new TestAuditContext(),
                    tenantContextProvider
                ).InsertProfile(
                    new ProfileInsertCommand
                    {
                        Name = "Save Profile",
                        Definition = "<Profile name=\"Save Profile\"/>",
                    }
                )
            )
                .Should()
                .BeOfType<ProfileInsertResult.Success>()
                .Subject.Id;

            _state = ShouldSucceed(
                await SaveScopedApplicationAsync(
                    [1001, 1002, 1003],
                    vendorId: _otherVendorId,
                    profileIds: [_profileId]
                )
            );
        }

        [Test]
        public async Task It_marks_no_client() => (await PendingClientsAsync()).Should().BeEmpty();

        [Test]
        public void It_returns_no_pending_client() => _state.PendingClients.Should().BeEmpty();

        [Test]
        public void It_saves_the_vendor_and_profiles()
        {
            _state.VendorId.Should().Be(_otherVendorId);
            _state.ProfileIds.Should().Equal(_profileId);
        }
    }

    [TestFixture]
    public class Given_a_save_with_the_education_organizations_reordered_and_repeated
        : ApplicationSaveRepositoryTests
    {
        private ApplicationSaveResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await SaveScopedApplicationAsync([1003, 1001, 1002, 1003, 1001]);
        }

        [Test]
        public void It_succeeds_with_no_pending_client() =>
            ShouldSucceed(_result).PendingClients.Should().BeEmpty();

        [Test]
        public async Task It_marks_no_client() => (await PendingClientsAsync()).Should().BeEmpty();

        [Test]
        public async Task It_leaves_every_row_untouched()
        {
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);
            (await ApplicationRowsAsync()).Should().Equal(ApplicationRowsBefore);
        }
    }

    [TestFixture]
    public class Given_a_narrowed_client_emptied_by_an_earlier_removal : ApplicationSaveRepositoryTests
    {
        private ApplicationSyncState _state = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            ShouldSucceed(await SaveScopedApplicationAsync([1001, 1003]));
            await Connection!.ExecuteAsync(
                """UPDATE dmscs.ApiClient SET IdentityProviderSyncPendingSince = NULL;"""
            );

            _state = ShouldSucceed(await SaveScopedApplicationAsync([1003, 1004]));
        }

        [Test]
        public async Task It_marks_the_inheriting_client_and_the_narrowed_client_that_held_the_removal() =>
            await ShouldHaveNewlyMarkedExactlyAsync(InheritingClientId, SecondNarrowedClientId);

        [Test]
        public void It_returns_the_remaining_scopes() =>
            Scopes(_state)
                .Should()
                .BeEquivalentTo(
                    [
                        (InheritingClientId, new long[] { 1003, 1004 }),
                        (SecondNarrowedClientId, new long[] { 1003 }),
                    ],
                    options => options.WithStrictOrdering()
                );

        [Test]
        public async Task It_keeps_the_emptied_client_narrowed_and_empty() =>
            ShouldMatch(await StateAsync(NarrowedClientId), State(ScopedApplicationId, false, [], []));
    }

    [TestFixture]
    public class Given_a_pending_client_and_an_identical_save : ApplicationSaveRepositoryTests
    {
        private ApplicationSyncState _state = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            await MarkPendingAsync(SecondNarrowedClientId);
            _state = ShouldSucceed(await SaveScopedApplicationAsync([1001, 1002, 1003]));
        }

        [Test]
        public void It_returns_the_pending_client_with_its_original_time() =>
            _state
                .PendingClients.Should()
                .BeEquivalentTo(
                    [
                        new
                        {
                            Id = SecondNarrowedClientId,
                            EducationOrganizationIds = new long[] { 1001, 1003 },
                            PendingSince,
                        },
                    ],
                    options => options.ExcludingMissingMembers()
                );

        [Test]
        public async Task It_marks_no_other_client() =>
            (await PendingClientsAsync())
                .Should()
                .BeEquivalentTo(new Dictionary<int, DateTime> { [SecondNarrowedClientId] = PendingSince });
    }

    [TestFixture]
    public class Given_a_pending_client_and_a_save_affecting_every_client : ApplicationSaveRepositoryTests
    {
        private ApplicationSyncState _state = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            await MarkPendingAsync(SecondNarrowedClientId);
            _state = ShouldSucceed(
                await SaveScopedApplicationAsync([1001, 1002, 1003], applicationName: "Renamed Application")
            );
        }

        [Test]
        public async Task It_keeps_the_original_time_of_the_pending_client() =>
            (await PendingClientsAsync())[SecondNarrowedClientId].Should().Be(PendingSince);

        [Test]
        public async Task It_marks_the_other_clients_at_the_save_time()
        {
            Dictionary<int, DateTime> pending = await PendingClientsAsync();
            pending
                .Keys.Should()
                .BeEquivalentTo([InheritingClientId, NarrowedClientId, SecondNarrowedClientId]);
            pending[InheritingClientId].Should().Be(pending[NarrowedClientId]);
            pending[InheritingClientId].Should().BeOnOrAfter(SaveStartedAt).And.BeOnOrBefore(SaveFinishedAt);
        }

        [Test]
        public void It_returns_every_pending_client_with_its_stored_time() =>
            _state
                .PendingClients.Select(client => client.Id)
                .Should()
                .Equal(InheritingClientId, NarrowedClientId, SecondNarrowedClientId);
    }

    [TestFixture]
    public class Given_a_save_of_an_Application_with_no_client : ApplicationSaveRepositoryTests
    {
        private int _applicationId;
        private ApplicationSaveResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _applicationId = (
                await ApplicationRepository.InsertApplication(
                    new ApplicationInsertCommand
                    {
                        ApplicationName = "Clientless Application",
                        VendorId = VendorId,
                        ClaimSetName = ScopedClaimSetName,
                        EducationOrganizationIds = [1001],
                        CreateDefaultCredentials = false,
                    },
                    new ApiClientCommand { ClientId = "", ClientUuid = Guid.Empty }
                )
            ).Should().BeOfType<ApplicationInsertResult.Success>().Subject.Id;

            _result = await ApplicationRepository.SaveApplication(
                new ApplicationUpdateCommand
                {
                    Id = _applicationId,
                    ApplicationName = "Renamed Clientless Application",
                    VendorId = VendorId,
                    ClaimSetName = "Other Claim set",
                    EducationOrganizationIds = [1002],
                }
            );
        }

        [Test]
        public void It_succeeds_with_no_pending_client() =>
            ShouldSucceed(_result)
                .Should()
                .BeEquivalentTo(
                    new
                    {
                        ApplicationName = "Renamed Clientless Application",
                        ClaimSetName = "Other Claim set",
                        EducationOrganizationIds = new long[] { 1002 },
                        PendingClients = Array.Empty<ApplicationSyncPendingClient>(),
                    },
                    options => options.ExcludingMissingMembers()
                );

        [Test]
        public async Task It_marks_no_client() => (await PendingClientsAsync()).Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_save_whose_new_name_is_taken : ApplicationSaveRepositoryTests
    {
        private ApplicationSaveResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await SaveScopedApplicationAsync([1003], applicationName: "Unrelated Application");
        }

        [Test]
        public void It_reports_the_duplicate() =>
            _result
                .Should()
                .BeOfType<ApplicationSaveResult.FailureDuplicateApplication>()
                .Which.ApplicationName.Should()
                .Be("Unrelated Application");

        [Test]
        public async Task It_marks_no_client() => (await PendingClientsAsync()).Should().BeEmpty();

        [Test]
        public async Task It_keeps_the_Application_and_every_client_row()
        {
            (await ScopedApplicationNameAsync()).Should().Be(ScopedApplicationName);
            (await ApplicationRowsAsync()).Should().Equal(ApplicationRowsBefore);
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);
        }
    }

    [TestFixture]
    public class Given_a_save_with_a_missing_profile : ApplicationSaveRepositoryTests
    {
        private ApplicationSaveResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await SaveScopedApplicationAsync(
                [1003],
                applicationName: "Renamed Application",
                profileIds: [int.MaxValue]
            );
        }

        [Test]
        public void It_reports_the_profile() =>
            _result.Should().BeOfType<ApplicationSaveResult.FailureProfileNotFound>();

        [Test]
        public async Task It_marks_no_client() => (await PendingClientsAsync()).Should().BeEmpty();

        [Test]
        public async Task It_keeps_the_Application_and_every_client_row()
        {
            (await ScopedApplicationNameAsync()).Should().Be(ScopedApplicationName);
            (await ApplicationRowsAsync()).Should().Equal(ApplicationRowsBefore);
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);
        }
    }

    [TestFixture]
    public class Given_a_save_with_a_missing_vendor : ApplicationSaveRepositoryTests
    {
        private ApplicationSaveResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            _result = await SaveScopedApplicationAsync(
                [1003],
                applicationName: "Renamed Application",
                vendorId: int.MaxValue
            );
        }

        [Test]
        public void It_reports_the_vendor() =>
            _result.Should().BeOfType<ApplicationSaveResult.FailureVendorNotFound>();

        [Test]
        public async Task It_marks_no_client() => (await PendingClientsAsync()).Should().BeEmpty();

        [Test]
        public async Task It_keeps_every_client_row() =>
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);
    }

    [TestFixture]
    public class Given_a_save_from_another_tenant : ApplicationSaveRepositoryTests
    {
        private ApplicationSaveResult _saveResult = null!;
        private ApplicationSyncStateResult _readResult = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(await CreateTenantProviderAsync("Owner"));
            await MarkPendingAsync(SecondNarrowedClientId);
            var foreignRepository = new ApplicationRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance,
                new TestAuditContext(),
                await CreateTenantProviderAsync("Foreign")
            );

            _saveResult = await foreignRepository.SaveApplication(
                new ApplicationUpdateCommand
                {
                    Id = ScopedApplicationId,
                    ApplicationName = "Renamed Application",
                    VendorId = VendorId,
                    ClaimSetName = ScopedClaimSetName,
                    EducationOrganizationIds = [1003],
                }
            );
            _readResult = await foreignRepository.GetApplicationSyncState(ScopedApplicationId);
        }

        [Test]
        public void It_does_not_find_the_Application_to_save() =>
            _saveResult.Should().BeOfType<ApplicationSaveResult.FailureNotExists>();

        [Test]
        public void It_does_not_find_the_Application_to_read() =>
            _readResult.Should().BeOfType<ApplicationSyncStateResult.FailureNotExists>();

        [Test]
        public async Task It_marks_no_other_client() =>
            (await PendingClientsAsync()).Keys.Should().Equal(SecondNarrowedClientId);

        [Test]
        public async Task It_keeps_the_Application_and_every_client_row()
        {
            (await ScopedApplicationNameAsync()).Should().Be(ScopedApplicationName);
            (await ClientRowsAsync()).Should().Equal(ClientRowsBefore);
        }
    }

    /// <summary>
    /// Direct client updates of two pending clients: only the one whose command completes the
    /// identity provider synchronization loses its marker.
    /// </summary>
    [TestFixture]
    public class Given_client_updates_of_pending_clients : ApplicationSaveRepositoryTests
    {
        private ApiClientUpdateResult _keeping = null!;
        private ApiClientUpdateResult _completing = null!;
        private ApiClientResolutionState _completedState = null!;
        private ApiClientResolutionState _keptState = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            await MarkPendingAsync(NarrowedClientId);
            await MarkPendingAsync(SecondNarrowedClientId);

            _keeping = await ApiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = NarrowedClientId,
                    ApplicationId = ScopedApplicationId,
                    Name = "Kept Client",
                    IsApproved = true,
                }
            );
            _completing = await ApiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = SecondNarrowedClientId,
                    ApplicationId = ScopedApplicationId,
                    Name = "Completed Client",
                    IsApproved = true,
                    CompletesIdentityProviderSync = true,
                    SentClaimSetName = ScopedClaimSetName,
                }
            );
            _keptState = (await ApiClientRepository.GetApiClientResolutionState(NarrowedClientId))
                .Should()
                .BeOfType<ApiClientResolutionResult.Success>()
                .Subject.State;
            _completedState = (await ApiClientRepository.GetApiClientResolutionState(SecondNarrowedClientId))
                .Should()
                .BeOfType<ApiClientResolutionResult.Success>()
                .Subject.State;
        }

        [Test]
        public void It_saves_both_updates()
        {
            _keeping.Should().BeOfType<ApiClientUpdateResult.Success>();
            _completing.Should().BeOfType<ApiClientUpdateResult.Success>();
        }

        [Test]
        public async Task It_clears_only_the_completed_client() =>
            (await PendingClientsAsync())
                .Should()
                .BeEquivalentTo(new Dictionary<int, DateTime> { [NarrowedClientId] = PendingSince });

        [Test]
        public void It_reads_the_pending_state_back_for_outcome_resolution()
        {
            _keptState.IdentityProviderSyncPendingSince.Should().Be(PendingSince);
            _completedState.IdentityProviderSyncPendingSince.Should().BeNull();
            _completedState.Name.Should().Be("Completed Client");
        }
    }

    /// <summary>
    /// A completing update of a pending client that the database rejects after the client row was
    /// already updated, its marker cleared and its scope rewritten: the missing data store fails the
    /// transaction, and every change rolls back together, the marker included.
    /// </summary>
    [TestFixture]
    public class Given_a_completing_client_update_rolled_back_by_the_database : ApplicationSaveRepositoryTests
    {
        private ApiClientResolutionState _before = null!;
        private ClientRow[] _rowsBefore = [];
        private ApiClientUpdateResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            await MarkPendingAsync(NarrowedClientId);
            await MarkPendingAsync(SecondNarrowedClientId);
            _before = await ResolutionStateAsync(NarrowedClientId);
            _rowsBefore = await ClientRowsOfAsync(NarrowedClientId);

            _result = await ApiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = NarrowedClientId,
                    ApplicationId = ScopedApplicationId,
                    Name = "Changed Client",
                    IsApproved = false,
                    DataStoreIds = [MissingDataStoreId],
                    ClientUuid = Guid.NewGuid(),
                    EducationOrganizationScope = new ApiClientEducationOrganizationScope.Narrow([1002, 1003]),
                    CompletesIdentityProviderSync = true,
                    SentClaimSetName = ScopedClaimSetName,
                }
            );
        }

        private async Task<ApiClientResolutionState> ResolutionStateAsync(int apiClientId) =>
            (await ApiClientRepository.GetApiClientResolutionState(apiClientId))
                .Should()
                .BeOfType<ApiClientResolutionResult.Success>()
                .Subject.State;

        [Test]
        public void It_reports_the_missing_data_store() =>
            _result.Should().BeOfType<ApiClientUpdateResult.FailureDataStoreNotFound>();

        [Test]
        public async Task It_leaves_the_client_exactly_as_it_was_marker_included()
        {
            ApiClientResolutionState after = await ResolutionStateAsync(NarrowedClientId);
            after.Should().BeEquivalentTo(_before, options => options.WithStrictOrdering());
            after.IdentityProviderSyncPendingSince.Should().Be(PendingSince);
        }

        [Test]
        public async Task It_keeps_the_client_rows_with_their_audit_values() =>
            (await ClientRowsOfAsync(NarrowedClientId)).Should().Equal(_rowsBefore);

        [Test]
        public async Task It_keeps_every_marker_with_its_time() =>
            (await PendingClientsAsync())
                .Should()
                .BeEquivalentTo(
                    new Dictionary<int, DateTime>
                    {
                        [NarrowedClientId] = PendingSince,
                        [SecondNarrowedClientId] = PendingSince,
                    }
                );
    }

    /// <summary>
    /// Pending clients synchronized with the claim set name read before a claim set rename committed:
    /// the provider holds the old name, so neither the Application update's clear nor a completing
    /// client update may clear the marker. Sent the current name, the clear succeeds.
    /// </summary>
    [TestFixture]
    public class Given_pending_clients_synchronized_before_a_claim_set_rename : ApplicationSaveRepositoryTests
    {
        private const string RenamedClaimSetName = "TEST CLAIM SET";

        private ApiClientSyncPendingResult _staleClear = null!;
        private ApiClientUpdateResult _staleCompletion = null!;
        private Dictionary<int, DateTime> _pendingAfterStaleWrites = [];
        private ApiClientSyncPendingResult _currentClear = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            await MarkPendingAsync(NarrowedClientId);
            await MarkPendingAsync(SecondNarrowedClientId);

            // The claim set is renamed, only in case so the exact comparison is what tells the names apart, after the provider was sent the old name
            await Connection!.ExecuteAsync(
                """
                UPDATE dmscs.Application SET ClaimSetName = @ClaimSetName WHERE Id = @ApplicationId;
                """,
                new { ClaimSetName = RenamedClaimSetName, ApplicationId = ScopedApplicationId }
            );

            _staleClear = await ApiClientRepository.ClearApiClientIdentityProviderSyncPending(
                NarrowedClientId,
                ScopedClaimSetName
            );
            _staleCompletion = await ApiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = SecondNarrowedClientId,
                    ApplicationId = ScopedApplicationId,
                    Name = "Completed Client",
                    IsApproved = true,
                    CompletesIdentityProviderSync = true,
                    SentClaimSetName = ScopedClaimSetName,
                }
            );
            _pendingAfterStaleWrites = await PendingClientsAsync();

            _currentClear = await ApiClientRepository.ClearApiClientIdentityProviderSyncPending(
                NarrowedClientId,
                RenamedClaimSetName
            );
        }

        [Test]
        public void It_refuses_the_clear_of_the_old_name() =>
            _staleClear.Should().BeOfType<ApiClientSyncPendingResult.FailureClaimSetChanged>();

        [Test]
        public void It_saves_the_completing_client_update() =>
            _staleCompletion.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public void It_keeps_both_clients_pending_since_their_original_time() =>
            _pendingAfterStaleWrites
                .Should()
                .BeEquivalentTo(
                    new Dictionary<int, DateTime>
                    {
                        [NarrowedClientId] = PendingSince,
                        [SecondNarrowedClientId] = PendingSince,
                    }
                );

        [Test]
        public async Task It_clears_the_client_sent_the_current_name()
        {
            _currentClear.Should().BeOfType<ApiClientSyncPendingResult.Success>();
            (await PendingClientsAsync()).Keys.Should().Equal(SecondNarrowedClientId);
        }
    }

    /// <summary>
    /// A clear of a client synchronized with the old claim set name, started while a claim set rename
    /// that has already marked the client is uncommitted: the clear waits for the rename and then
    /// reads the new name, so the rename's marker survives.
    /// </summary>
    [TestFixture]
    public class Given_a_clear_started_while_a_claim_set_rename_is_uncommitted
        : ApplicationSaveRepositoryTests
    {
        private static readonly DateTime _renamedAt = new(2030, 6, 7, 8, 9, 10, DateTimeKind.Utc);

        private ApiClientSyncPendingResult _clearResult = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());

            await using SqlConnection renamer = await OpenConnectionAsync();
            await using SqlTransaction transaction = (SqlTransaction)await renamer.BeginTransactionAsync();
            short renamerSession = await renamer.ExecuteScalarAsync<short>(
                "SELECT @@SPID;",
                transaction: transaction
            );

            // The statements a claim set rename runs: the Application first, then its clients
            await renamer.ExecuteAsync(
                """
                UPDATE dmscs.Application SET ClaimSetName = @ClaimSetName WHERE Id = @ApplicationId;
                """,
                new { ClaimSetName = "Renamed Claim set", ApplicationId = ScopedApplicationId },
                transaction
            );
            await renamer.ExecuteAsync(
                """
                UPDATE dmscs.ApiClient
                SET IdentityProviderSyncPendingSince = COALESCE(IdentityProviderSyncPendingSince, @RenamedAt)
                WHERE ApplicationId = @ApplicationId;
                """,
                new { RenamedAt = _renamedAt, ApplicationId = ScopedApplicationId },
                transaction
            );

            Task<ApiClientSyncPendingResult> clear =
                ApiClientRepository.ClearApiClientIdentityProviderSyncPending(
                    NarrowedClientId,
                    ScopedClaimSetName
                );
            await WaitUntilBlockedByAsync(renamer, transaction, renamerSession, clear);
            await transaction.CommitAsync();
            _clearResult = await clear;
        }

        [Test]
        public void It_refuses_the_clear() =>
            _clearResult.Should().BeOfType<ApiClientSyncPendingResult.FailureClaimSetChanged>();

        [Test]
        public async Task It_keeps_the_rename_marker() =>
            (await PendingClientsAsync())[NarrowedClientId].Should().Be(_renamedAt);
    }

    /// <summary>
    /// A client that is not pending moved into an Application whose claim set rename committed after
    /// the provider was sent the old name. The rename marked only the Application's clients at the
    /// time, so the move itself must leave the client pending.
    /// </summary>
    [TestFixture]
    public class Given_a_client_moved_into_an_Application_renamed_after_its_claim_set_name_was_sent
        : ApplicationSaveRepositoryTests
    {
        private ApiClientUpdateResult _moveResult = null!;
        private DateTime _moveStartedAt;
        private DateTime _moveFinishedAt;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());

            // The statements a claim set rename runs: the Application first, then its clients
            DynamicParameters parameters = new(
                new { ClaimSetName = "Renamed Claim set", ApplicationId = ScopedApplicationId }
            );
            parameters.Add("PendingSince", PendingSince, DbType.DateTime2);
            await Connection!.ExecuteAsync(
                """
                UPDATE dmscs.Application SET ClaimSetName = @ClaimSetName WHERE Id = @ApplicationId;
                UPDATE dmscs.ApiClient
                SET IdentityProviderSyncPendingSince = COALESCE(IdentityProviderSyncPendingSince, @PendingSince)
                WHERE ApplicationId = @ApplicationId;
                """,
                parameters
            );

            _moveStartedAt = DateTime.UtcNow;
            _moveResult = await ApiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = UnrelatedClientId,
                    ApplicationId = ScopedApplicationId,
                    Name = "Moved Client",
                    IsApproved = true,
                    CompletesIdentityProviderSync = true,
                    SentClaimSetName = ScopedClaimSetName,
                }
            );
            _moveFinishedAt = DateTime.UtcNow;
        }

        [Test]
        public void It_saves_the_move() => _moveResult.Should().BeOfType<ApiClientUpdateResult.Success>();

        // SqlClient binds the save's DateTime as legacy datetime, which rounds to 1/300 second
        [Test]
        public async Task It_marks_the_moved_client_pending_at_the_move() =>
            (await PendingClientsAsync())[UnrelatedClientId]
                .Should()
                .BeOnOrAfter(_moveStartedAt.AddMilliseconds(-4))
                .And.BeOnOrBefore(_moveFinishedAt.AddMilliseconds(4));
    }

    /// <summary>
    /// A client that is not pending moved into an Application, sent its claim set name before a
    /// rename of it that is uncommitted when the move is saved. The rename never sees the client on
    /// the Application, so the move waits for it and then marks the client.
    /// </summary>
    [TestFixture]
    public class Given_a_client_move_started_while_a_claim_set_rename_is_uncommitted
        : ApplicationSaveRepositoryTests
    {
        private ApiClientUpdateResult _moveResult = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());

            await using SqlConnection renamer = await OpenConnectionAsync();
            await using SqlTransaction transaction = (SqlTransaction)await renamer.BeginTransactionAsync();
            short renamerSession = await renamer.ExecuteScalarAsync<short>(
                "SELECT @@SPID;",
                transaction: transaction
            );

            // The statements a claim set rename runs: the Application first, then its clients
            DynamicParameters parameters = new(
                new { ClaimSetName = "Renamed Claim set", ApplicationId = ScopedApplicationId }
            );
            parameters.Add("PendingSince", PendingSince, DbType.DateTime2);
            await renamer.ExecuteAsync(
                """
                UPDATE dmscs.Application SET ClaimSetName = @ClaimSetName WHERE Id = @ApplicationId;
                UPDATE dmscs.ApiClient
                SET IdentityProviderSyncPendingSince = COALESCE(IdentityProviderSyncPendingSince, @PendingSince)
                WHERE ApplicationId = @ApplicationId;
                """,
                parameters,
                transaction
            );

            Task<ApiClientUpdateResult> move = ApiClientRepository.UpdateApiClient(
                new ApiClientUpdateCommand
                {
                    Id = UnrelatedClientId,
                    ApplicationId = ScopedApplicationId,
                    Name = "Moved Client",
                    IsApproved = true,
                    CompletesIdentityProviderSync = true,
                    SentClaimSetName = ScopedClaimSetName,
                }
            );
            await WaitUntilBlockedByAsync(renamer, transaction, renamerSession, move);
            await transaction.CommitAsync();
            _moveResult = await move;
        }

        [Test]
        public void It_saves_the_move() => _moveResult.Should().BeOfType<ApiClientUpdateResult.Success>();

        [Test]
        public async Task It_marks_the_moved_client_pending() =>
            (await PendingClientsAsync()).Should().ContainKey(UnrelatedClientId);
    }

    [TestFixture]
    public class Given_a_save_of_a_missing_Application : ApplicationSaveRepositoryTests
    {
        [SetUp]
        public async Task Setup() => await SeedAsync(new TenantContextProvider());

        [Test]
        public async Task It_does_not_find_it() => (
                await ApplicationRepository.SaveApplication(
                    new ApplicationUpdateCommand
                    {
                        Id = int.MaxValue,
                        ApplicationName = "Missing",
                        VendorId = VendorId,
                        ClaimSetName = ScopedClaimSetName,
                    }
                )
            ).Should().BeOfType<ApplicationSaveResult.FailureNotExists>();
    }

    [TestFixture]
    public class Given_the_sync_state_read_after_a_save : ApplicationSaveRepositoryTests
    {
        private ApplicationSyncState _saved = null!;
        private ApplicationSyncState _read = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());
            await MarkPendingAsync(SecondNarrowedClientId);
            _saved = ShouldSucceed(await SaveScopedApplicationAsync([1001, 1003, 1004]));
            _read = (await ApplicationRepository.GetApplicationSyncState(ScopedApplicationId))
                .Should()
                .BeOfType<ApplicationSyncStateResult.Success>()
                .Subject.State;
        }

        [Test]
        public void It_reads_what_the_save_returned() =>
            _read.Should().BeEquivalentTo(_saved, options => options.WithStrictOrdering());

        [Test]
        public void It_reads_every_pending_client() =>
            _read
                .PendingClients.Select(client => client.Id)
                .Should()
                .Equal(InheritingClientId, NarrowedClientId, SecondNarrowedClientId);
    }

    /// <summary>
    /// A client added to the Application by a transaction still open when the save starts: the save
    /// waits for it rather than deciding the affected clients without it.
    /// </summary>
    [TestFixture]
    public class Given_a_save_started_while_a_client_is_being_added : ApplicationSaveRepositoryTests
    {
        private int _addedClientId;
        private ApplicationSyncState _state = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());

            await using SqlConnection adder = await OpenConnectionAsync();
            await using SqlTransaction transaction = (SqlTransaction)await adder.BeginTransactionAsync();
            short adderSession = await adder.ExecuteScalarAsync<short>(
                "SELECT @@SPID;",
                transaction: transaction
            );

            // A client write holds a shared lock on its Application, as the API client repository's does
            await adder.ExecuteAsync(
                "SELECT Id FROM dmscs.Application WITH (INDEX(PK_Application), HOLDLOCK, ROWLOCK) WHERE Id = @ApplicationId;",
                new { ApplicationId = ScopedApplicationId },
                transaction
            );
            _addedClientId = await adder.ExecuteScalarAsync<int>(
                """
                INSERT INTO dmscs.ApiClient (ApplicationId, ClientId, ClientUuid, Name, IsApproved)
                OUTPUT inserted.Id
                VALUES (@ApplicationId, @ClientId, @ClientUuid, 'Added Client', 1);
                """,
                new
                {
                    ApplicationId = ScopedApplicationId,
                    ClientId = Guid.NewGuid().ToString(),
                    ClientUuid = Guid.NewGuid(),
                },
                transaction
            );

            Task<ApplicationSaveResult> save = SaveScopedApplicationAsync(
                [1001, 1002, 1003],
                applicationName: "Renamed Application"
            );
            await WaitUntilBlockedByAsync(adder, transaction, adderSession, save);
            await transaction.CommitAsync();
            _state = ShouldSucceed(await save);
        }

        [Test]
        public void It_marks_and_returns_the_added_client_too() =>
            _state
                .PendingClients.Select(client => client.Id)
                .Should()
                .Equal(InheritingClientId, NarrowedClientId, SecondNarrowedClientId, _addedClientId);
    }

    /// <summary>
    /// A save waiting on the Application row while another transaction adds the education
    /// organization it requests: it reads the list only once it holds the row, so it keeps the added
    /// id rather than inserting it a second time, and sees no change to mark clients for.
    /// </summary>
    [TestFixture]
    public class Given_a_save_waiting_behind_a_writer_that_adds_the_requested_education_organization
        : ApplicationSaveRepositoryTests
    {
        private ApplicationSaveResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            await SeedAsync(new TenantContextProvider());

            await using SqlConnection writer = await OpenConnectionAsync();
            await using SqlTransaction transaction = (SqlTransaction)await writer.BeginTransactionAsync();
            short writerSession = await writer.ExecuteScalarAsync<short>(
                "SELECT @@SPID;",
                transaction: transaction
            );
            await writer.ExecuteAsync(
                "SELECT Id FROM dmscs.Application WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id;",
                new { Id = ScopedApplicationId },
                transaction
            );

            Task<ApplicationSaveResult> update = SaveScopedApplicationAsync([1001, 1002, 1003, 1004]);
            await WaitUntilBlockedByAsync(writer, transaction, writerSession, update);
            await writer.ExecuteAsync(
                """
                INSERT INTO dmscs.ApplicationEducationOrganization (ApplicationId, EducationOrganizationId)
                VALUES (@Id, 1004);
                """,
                new { Id = ScopedApplicationId },
                transaction
            );
            await transaction.CommitAsync();
            _result = await update;
        }

        [Test]
        public void It_succeeds_with_no_pending_client() =>
            ShouldSucceed(_result).PendingClients.Should().BeEmpty();

        [Test]
        public async Task It_marks_no_client() => (await PendingClientsAsync()).Should().BeEmpty();

        [Test]
        public async Task It_leaves_exactly_the_requested_list() =>
            (await ApplicationRowsAsync())
                .Where(row => row.ApplicationId == ScopedApplicationId)
                .Select(row => row.EducationOrganizationId)
                .Should()
                .Equal(1001, 1002, 1003, 1004);
    }
}
