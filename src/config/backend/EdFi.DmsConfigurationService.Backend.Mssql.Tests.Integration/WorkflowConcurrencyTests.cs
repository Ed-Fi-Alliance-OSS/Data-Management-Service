// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Net;
using System.Text;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RepositoryApiClient = EdFi.DmsConfigurationService.Backend.Repositories.ApiClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Proves at the HTTP workflow level that the real database lock manager serializes the
/// Application and ApiClient workflows that share an application aggregate, through
/// compensation and through the repository commit. The frontend pipeline runs against a
/// shared mutable client state served by fakes, so a paused workflow's later effects are
/// observable to the workflow it blocks.
/// </summary>
public abstract class WorkflowConcurrencyTestBase : DatabaseTestBase
{
    private sealed class ClientState
    {
        public int ApplicationId { get; set; }
        public string ClientId { get; init; } = "";
        public Guid ClientUuid { get; set; }
    }

    private protected sealed record ProviderUpdate(string TargetedUuid, Guid IssuedUuid);

    /// <summary>
    /// One namespace-claim call the Vendor workflow made: which identity-provider client it
    /// addressed, which client the provider reported back, and whether it carried the requested
    /// prefixes or put the stored ones back.
    /// </summary>
    private protected sealed record NamespaceUpdate(string TargetedUuid, Guid IssuedUuid, string Prefixes);

    /// <summary>
    /// Wraps the real lock manager so the first workflow holds its first lock until the
    /// gate opens. Both moves then overlap inside lock acquisition before either owns its
    /// second lock, which is the window where an unordered acquisition deadlocks.
    /// </summary>
    private sealed class GatedLockManager(IApplicationLockManager inner) : IApplicationLockManager
    {
        private int _acquisitions;
        private readonly TaskCompletionSource _gateOpened = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public TaskCompletionSource FirstAcquisitionHeld { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondAcquisitionEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondAcquisitionSucceeded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _gateOpened.TrySetResult();

        public async Task<ApplicationLockResult> AcquireAsync(
            int applicationId,
            CancellationToken cancellationToken
        )
        {
            int acquisition = Interlocked.Increment(ref _acquisitions);
            if (acquisition == 2)
            {
                SecondAcquisitionEntered.TrySetResult();
            }

            ApplicationLockResult result = await inner.AcquireAsync(applicationId, cancellationToken);
            if (acquisition == 1 && result is ApplicationLockResult.Acquired)
            {
                FirstAcquisitionHeld.TrySetResult();
                await _gateOpened.Task;
            }

            if (acquisition == 2 && result is ApplicationLockResult.Acquired)
            {
                SecondAcquisitionSucceeded.TrySetResult();
            }

            return result;
        }
    }

    private readonly object _stateLock = new();
    private Dictionary<int, ClientState> _clients = [];

    private IApplicationRepository _applicationRepository = null!;
    private IApiClientRepository _apiClientRepository = null!;
    private IVendorRepository _vendorRepository = null!;
    private IDataStoreRepository _dataStoreRepository = null!;
    private IProfileRepository _profileRepository = null!;
    private IIdentityProviderRepository _identityProviderRepository = null!;
    private WebApplicationFactory<Program>? _factory;
    private GatedLockManager? _acquisitionGate;

    private TaskCompletionSource _providerCallStarted = new();
    private TaskCompletionSource _providerCallReleased = new();
    private TaskCompletionSource _repositoryUpdateStarted = new();
    private TaskCompletionSource _repositoryUpdateReleased = new();
    private int _pausedProviderCall;
    private bool _pauseFirstApiClientRepositoryUpdate;
    private ApplicationUpdateResult _applicationUpdateResult = new ApplicationUpdateResult.Success();

    private TaskCompletionSource _namespaceCallStarted = new();
    private TaskCompletionSource _namespaceCallReleased = new();
    private TaskCompletionSource _vendorRepositoryUpdateStarted = new();
    private TaskCompletionSource _vendorRepositoryUpdateReleased = new();
    private int _pausedNamespaceCall;
    private int _failingNamespaceCall;
    private bool _pauseVendorRepositoryUpdate;

    private int _providerCalls;
    private int _namespaceCalls;
    private int _apiClientRepositoryUpdates;
    private int _apiClientReads;
    private int _applicationApiClientsReads;
    private int _vendorStateReads;
    private List<ProviderUpdate> _providerUpdates = [];
    private List<NamespaceUpdate> _namespaceUpdates = [];
    private List<Task<HttpResponseMessage>> _outstandingRequests = [];

    [TearDown]
    public async Task ReleaseWorkflowBarriersAndRequests()
    {
        // A failed Act must not leave a paused request holding its aggregate locks while
        // the database teardown runs, so every barrier opens and every request drains
        // before control leaves this fixture.
        _providerCallReleased.TrySetResult();
        _repositoryUpdateReleased.TrySetResult();
        _namespaceCallReleased.TrySetResult();
        _vendorRepositoryUpdateReleased.TrySetResult();
        _acquisitionGate?.Open();

        foreach (Task<HttpResponseMessage> request in _outstandingRequests)
        {
            try
            {
                (await request.WaitAsync(TimeSpan.FromSeconds(60))).Dispose();
            }
            catch (Exception)
            {
                // The request's failure is the test outcome; teardown only drains it.
            }
        }

        _outstandingRequests.Clear();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }
    }

    /// <summary>
    /// Creates fresh fakes for one scenario run, serving reads and writes from a shared
    /// mutable client state so one workflow's committed effects are visible to the next.
    /// The provider mutation numbered by <see cref="_pausedProviderCall"/> (or the first
    /// repository ApiClient update when configured) pauses until the test releases it.
    /// </summary>
    private protected void SetUpWorkflowFakes(params (int ApiClientId, int ApplicationId)[] clients)
    {
        _clients = clients.ToDictionary(
            client => client.ApiClientId,
            client => new ClientState
            {
                ApplicationId = client.ApplicationId,
                ClientId = $"client-{client.ApiClientId}",
                ClientUuid = Guid.NewGuid(),
            }
        );

        _applicationRepository = A.Fake<IApplicationRepository>();
        _apiClientRepository = A.Fake<IApiClientRepository>();
        _vendorRepository = A.Fake<IVendorRepository>();
        _dataStoreRepository = A.Fake<IDataStoreRepository>();
        _profileRepository = A.Fake<IProfileRepository>();
        _identityProviderRepository = A.Fake<IIdentityProviderRepository>();
        _acquisitionGate = null;
        _providerCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _providerCallReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _repositoryUpdateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _repositoryUpdateReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _namespaceCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _namespaceCallReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _vendorRepositoryUpdateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _vendorRepositoryUpdateReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pausedProviderCall = 0;
        _pausedNamespaceCall = 0;
        _failingNamespaceCall = 0;
        _pauseVendorRepositoryUpdate = false;
        _pauseFirstApiClientRepositoryUpdate = false;
        _applicationUpdateResult = new ApplicationUpdateResult.Success();
        _providerCalls = 0;
        _namespaceCalls = 0;
        _apiClientRepositoryUpdates = 0;
        _apiClientReads = 0;
        _applicationApiClientsReads = 0;
        _vendorStateReads = 0;
        _providerUpdates = [];
        _namespaceUpdates = [];
        _outstandingRequests = [];

        A.CallTo(() => _vendorRepository.GetVendor(A<int>.Ignored))
            .Returns(
                new VendorGetResult.Success(
                    new VendorResponse
                    {
                        Id = 1,
                        Company = "Test Vendor",
                        ContactName = "Test Contact",
                        ContactEmailAddress = "test@test.test",
                        NamespacePrefixes = "uri://test",
                    }
                )
            );

        A.CallTo(() => _dataStoreRepository.GetExistingDataStoreIds(A<int[]>.Ignored))
            .ReturnsLazily(call =>
            {
                int[] ids = call.GetArgument<int[]>(0) ?? [];
                return Task.FromResult<DataStoreIdsExistResult>(
                    new DataStoreIdsExistResult.Success([.. ids])
                );
            });

        A.CallTo(() => _applicationRepository.GetApplication(A<int>.Ignored))
            .ReturnsLazily(call =>
                Task.FromResult<ApplicationGetResult>(
                    new ApplicationGetResult.Success(
                        new ApplicationResponse
                        {
                            Id = call.GetArgument<int>(0),
                            ApplicationName = "Test Application",
                            ClaimSetName = "TestClaimSet",
                            VendorId = 1,
                            EducationOrganizationIds = [1],
                            DataStoreIds = [1],
                        }
                    )
                )
            );

        A.CallTo(() => _applicationRepository.GetApplicationApiClients(A<int>.Ignored))
            .ReturnsLazily(call =>
            {
                Interlocked.Increment(ref _applicationApiClientsReads);
                int applicationId = call.GetArgument<int>(0);
                lock (_stateLock)
                {
                    RepositoryApiClient[] applicationClients =
                    [
                        .. _clients
                            .Values.Where(client => client.ApplicationId == applicationId)
                            .Select(client => new RepositoryApiClient(
                                client.ClientId,
                                client.ClientUuid,
                                true
                            )),
                    ];
                    return Task.FromResult<ApplicationApiClientsResult>(
                        new ApplicationApiClientsResult.Success(applicationClients)
                    );
                }
            });

        A.CallTo(() => _applicationRepository.GetApplicationUpdateState(A<int>.Ignored, A<string>.Ignored))
            .ReturnsLazily(call =>
            {
                int applicationId = call.GetArgument<int>(0);
                string clientId = call.GetArgument<string>(1)!;
                lock (_stateLock)
                {
                    ClientState? state = _clients.Values.FirstOrDefault(client =>
                        client.ApplicationId == applicationId && client.ClientId == clientId
                    );
                    return Task.FromResult<ApplicationUpdateStateResult>(
                        state is null
                            ? new ApplicationUpdateStateResult.FailureNotExists()
                            : new ApplicationUpdateStateResult.Success(
                                new ApplicationUpdateState(
                                    "Test Application",
                                    1,
                                    "TestClaimSet",
                                    [1],
                                    [],
                                    state.ClientId,
                                    state.ClientUuid,
                                    true,
                                    [1]
                                )
                            )
                    );
                }
            });

        A.CallTo(() =>
                _applicationRepository.SyncApplicationApiClientUuid(
                    A<int>.Ignored,
                    A<string>.Ignored,
                    A<Guid>.Ignored,
                    A<Guid>.Ignored
                )
            )
            .ReturnsLazily(call =>
            {
                int applicationId = call.GetArgument<int>(0);
                string clientId = call.GetArgument<string>(1)!;
                Guid expectedUuid = call.GetArgument<Guid>(2);
                Guid newUuid = call.GetArgument<Guid>(3);
                lock (_stateLock)
                {
                    ClientState? state = _clients.Values.FirstOrDefault(client =>
                        client.ApplicationId == applicationId
                        && client.ClientId == clientId
                        && client.ClientUuid == expectedUuid
                    );
                    if (state is null)
                    {
                        return Task.FromResult<ApiClientUuidSyncResult>(
                            new ApiClientUuidSyncResult.FailureUnknown(
                                "The stored state does not match the expected UUID."
                            )
                        );
                    }

                    state.ClientUuid = newUuid;
                    return Task.FromResult<ApiClientUuidSyncResult>(new ApiClientUuidSyncResult.Success());
                }
            });

        A.CallTo(() =>
                _applicationRepository.UpdateApplication(
                    A<ApplicationUpdateCommand>.Ignored,
                    A<ApiClientCommand>.Ignored
                )
            )
            .ReturnsLazily(call =>
            {
                ApplicationUpdateCommand command = call.GetArgument<ApplicationUpdateCommand>(0)!;
                ApiClientCommand apiClientCommand = call.GetArgument<ApiClientCommand>(1)!;
                if (_applicationUpdateResult is not ApplicationUpdateResult.Success)
                {
                    return Task.FromResult(_applicationUpdateResult);
                }

                lock (_stateLock)
                {
                    ClientState? state = _clients.Values.FirstOrDefault(client =>
                        client.ApplicationId == command.Id && client.ClientId == apiClientCommand.ClientId
                    );
                    if (state is not null)
                    {
                        state.ClientUuid = apiClientCommand.ClientUuid;
                    }
                }

                return Task.FromResult<ApplicationUpdateResult>(new ApplicationUpdateResult.Success());
            });

        A.CallTo(() => _apiClientRepository.GetApiClientById(A<int>.Ignored))
            .ReturnsLazily(call =>
            {
                Interlocked.Increment(ref _apiClientReads);
                int apiClientId = call.GetArgument<int>(0);
                lock (_stateLock)
                {
                    ClientState state = _clients[apiClientId];
                    return Task.FromResult<ApiClientGetResult>(
                        new ApiClientGetResult.Success(
                            new ApiClientResponse
                            {
                                Id = apiClientId,
                                ApplicationId = state.ApplicationId,
                                ClientId = state.ClientId,
                                ClientUuid = state.ClientUuid,
                                Name = $"Client {apiClientId}",
                                IsApproved = true,
                                DataStoreIds = [1],
                            }
                        )
                    );
                }
            });

        A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
            .ReturnsLazily(call =>
                ApplyApiClientRepositoryUpdateAsync(call.GetArgument<ApiClientUpdateCommand>(0)!)
            );

        A.CallTo(() =>
                _identityProviderRepository.UpdateClientAsync(
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<int[]?>.Ignored,
                    A<bool>.Ignored,
                    A<string>.Ignored
                )
            )
            .ReturnsLazily(call => RecordProviderUpdateAsync(call.GetArgument<string>(0)!));

        A.CallTo(() =>
                _identityProviderRepository.UpdateClientNamespaceClaimAsync(
                    A<string>.Ignored,
                    A<string>.Ignored
                )
            )
            .ReturnsLazily(call =>
                RecordNamespaceUpdateAsync(call.GetArgument<string>(0)!, call.GetArgument<string>(1)!)
            );

        A.CallTo(() => _vendorRepository.GetVendorUpdateState(A<int>.Ignored))
            .ReturnsLazily(_ =>
            {
                Interlocked.Increment(ref _vendorStateReads);
                lock (_stateLock)
                {
                    VendorApiClient[] vendorClients =
                    [
                        .. _clients
                            .OrderBy(entry => entry.Value.ApplicationId)
                            .ThenBy(entry => entry.Key)
                            .Select(entry => new VendorApiClient(
                                entry.Key,
                                entry.Value.ClientId,
                                entry.Value.ClientUuid,
                                entry.Value.ApplicationId
                            )),
                    ];
                    return Task.FromResult<VendorUpdateStateResult>(
                        new VendorUpdateStateResult.Success(
                            new VendorUpdateState(
                                "Test Vendor",
                                "Test Contact",
                                "test@test.test",
                                StoredNamespacePrefixes,
                                vendorClients
                            )
                        )
                    );
                }
            });

        A.CallTo(() => _vendorRepository.UpdateVendor(A<VendorUpdateCommand>.Ignored))
            .ReturnsLazily(_ => ApplyVendorRepositoryUpdateAsync());

        A.CallTo(() =>
                _apiClientRepository.SyncApiClientUuid(A<int>.Ignored, A<Guid>.Ignored, A<Guid>.Ignored)
            )
            .ReturnsLazily(call =>
                SyncApiClientUuid(
                    call.GetArgument<int>(0),
                    call.GetArgument<Guid>(1),
                    call.GetArgument<Guid>(2)
                )
            );
    }

    /// <summary>
    /// The prefixes the vendor row currently holds, which a rollback puts back, and the ones the
    /// request asks for. Telling them apart is what distinguishes a claim update from its
    /// compensation.
    /// </summary>
    private protected const string StoredNamespacePrefixes = "uri://stored.org";

    private protected const string RequestedNamespacePrefixes = "uri://stored.org,uri://new.org";

    /// <summary>
    /// Mirrors the guarded repository operation: the write lands only when the row still holds
    /// the UUID the caller expected, so a workflow acting on a stale snapshot is refused exactly
    /// as the database would refuse it.
    /// </summary>
    private Task<ApiClientUuidSyncResult> SyncApiClientUuid(
        int apiClientId,
        Guid expectedClientUuid,
        Guid newClientUuid
    )
    {
        lock (_stateLock)
        {
            if (!_clients.TryGetValue(apiClientId, out ClientState? state))
            {
                return Task.FromResult<ApiClientUuidSyncResult>(
                    new ApiClientUuidSyncResult.FailureNotExistsSafeToDelete()
                );
            }

            if (state.ClientUuid == newClientUuid)
            {
                return Task.FromResult<ApiClientUuidSyncResult>(new ApiClientUuidSyncResult.AlreadyApplied());
            }

            if (state.ClientUuid != expectedClientUuid)
            {
                return Task.FromResult<ApiClientUuidSyncResult>(
                    new ApiClientUuidSyncResult.FailureStaleState()
                );
            }

            state.ClientUuid = newClientUuid;
            return Task.FromResult<ApiClientUuidSyncResult>(new ApiClientUuidSyncResult.Success());
        }
    }

    /// <summary>
    /// The namespace-claim update issues a fresh client on every call, so a workflow that failed
    /// to persist what the provider reported would leave the shared state pointing at a client
    /// no later operation could address.
    /// </summary>
    private async Task<ClientUpdateResult> RecordNamespaceUpdateAsync(string targetedUuid, string prefixes)
    {
        int namespaceCall = Interlocked.Increment(ref _namespaceCalls);
        if (namespaceCall == _pausedNamespaceCall)
        {
            _namespaceCallStarted.SetResult();
            await _namespaceCallReleased.Task;
        }

        if (namespaceCall == _failingNamespaceCall)
        {
            return new ClientUpdateResult.FailureUnknown("the provider rejected the claim update");
        }

        var issuedUuid = Guid.NewGuid();
        lock (_stateLock)
        {
            _namespaceUpdates.Add(new NamespaceUpdate(targetedUuid, issuedUuid, prefixes));
        }

        return new ClientUpdateResult.Success(issuedUuid);
    }

    private async Task<VendorUpdateResult> ApplyVendorRepositoryUpdateAsync()
    {
        if (_pauseVendorRepositoryUpdate)
        {
            _vendorRepositoryUpdateStarted.SetResult();
            await _vendorRepositoryUpdateReleased.Task;
        }

        return new VendorUpdateResult.Success();
    }

    private async Task<ApiClientUpdateResult> ApplyApiClientRepositoryUpdateAsync(
        ApiClientUpdateCommand command
    )
    {
        int update = Interlocked.Increment(ref _apiClientRepositoryUpdates);
        if (_pauseFirstApiClientRepositoryUpdate && update == 1)
        {
            _repositoryUpdateStarted.SetResult();
            await _repositoryUpdateReleased.Task;
        }

        lock (_stateLock)
        {
            ClientState state = _clients[command.Id];
            state.ApplicationId = command.ApplicationId;
            if (command.ClientUuid is { } issuedUuid)
            {
                state.ClientUuid = issuedUuid;
            }
        }

        return new ApiClientUpdateResult.Success();
    }

    private async Task<ClientUpdateResult> RecordProviderUpdateAsync(string targetedUuid)
    {
        int providerCall = Interlocked.Increment(ref _providerCalls);
        if (providerCall == _pausedProviderCall)
        {
            _providerCallStarted.SetResult();
            await _providerCallReleased.Task;
        }

        var issuedUuid = Guid.NewGuid();
        lock (_stateLock)
        {
            _providerUpdates.Add(new ProviderUpdate(targetedUuid, issuedUuid));
        }

        return new ClientUpdateResult.Success(issuedUuid);
    }

    /// <summary>
    /// Builds the frontend pipeline with the faked dependencies and the real database-backed
    /// lock manager, so the scenarios exercise exactly the lock acquisition the deployed
    /// workflows perform. The generous acquire timeout keeps a paused-holder scenario from
    /// timing out instead of blocking.
    /// </summary>
    private protected HttpClient SetUpWorkflowClient(bool gateFirstAcquisition = false)
    {
        IApplicationLockManager lockManager = new MssqlApplicationLockManager(
            MssqlTestConfiguration.DatabaseOptions,
            Options.Create(new ApplicationLockOptions { AcquireTimeout = TimeSpan.FromSeconds(30) }),
            NullLogger<MssqlApplicationLockManager>.Instance
        );
        if (gateFirstAcquisition)
        {
            _acquisitionGate = new GatedLockManager(lockManager);
            lockManager = _acquisitionGate;
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(collection =>
            {
                collection.AddTestAuthentication();
                collection
                    .AddSingleton(lockManager)
                    .AddTransient((_) => _applicationRepository)
                    .AddTransient((_) => _apiClientRepository)
                    .AddTransient((_) => _vendorRepository)
                    .AddTransient((_) => _dataStoreRepository)
                    .AddTransient((_) => _profileRepository)
                    .AddTransient((_) => _identityProviderRepository);
            });
        });
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        return client;
    }

    private protected Guid CurrentClientUuid(int apiClientId)
    {
        lock (_stateLock)
        {
            return _clients[apiClientId].ClientUuid;
        }
    }

    private protected int CurrentClientApplicationId(int apiClientId)
    {
        lock (_stateLock)
        {
            return _clients[apiClientId].ApplicationId;
        }
    }

    private protected Task<HttpResponseMessage> TrackRequest(Task<HttpResponseMessage> request)
    {
        _outstandingRequests.Add(request);
        return request;
    }

    private protected ProviderUpdate[] ProviderUpdatesSnapshot()
    {
        lock (_stateLock)
        {
            return [.. _providerUpdates];
        }
    }

    private protected int ApiClientReads => Volatile.Read(ref _apiClientReads);

    private protected int VendorStateReads => Volatile.Read(ref _vendorStateReads);

    private protected NamespaceUpdate[] NamespaceUpdatesSnapshot()
    {
        lock (_stateLock)
        {
            return [.. _namespaceUpdates];
        }
    }

    /// <summary>The claim updates that carried the newly requested prefixes.</summary>
    private protected NamespaceUpdate[] NamespaceClaimUpdates() =>
        [.. NamespaceUpdatesSnapshot().Where(update => update.Prefixes == RequestedNamespacePrefixes)];

    /// <summary>The claim updates that put the vendor's stored prefixes back.</summary>
    private protected NamespaceUpdate[] NamespaceClaimRollbacks() =>
        [.. NamespaceUpdatesSnapshot().Where(update => update.Prefixes == StoredNamespacePrefixes)];

    private protected static StringContent VendorBody() =>
        JsonBody(
            $$"""
            {
              "id": 1,
              "company": "Test Vendor",
              "contactName": "Test Contact",
              "contactEmailAddress": "test@test.test",
              "namespacePrefixes": "{{RequestedNamespacePrefixes}}"
            }
            """
        );

    private protected int ApplicationApiClientsReads => Volatile.Read(ref _applicationApiClientsReads);

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [TestFixture]
    public class Given_an_api_client_update_during_a_compensating_application_update
        : WorkflowConcurrencyTestBase
    {
        private static async Task WaitForAsync(Func<bool> condition)
        {
            var waited = Stopwatch.StartNew();
            while (!condition())
            {
                if (waited.Elapsed > TimeSpan.FromSeconds(10))
                {
                    throw new TimeoutException("The expected workflow progress was not observed.");
                }

                await Task.Delay(10);
            }
        }

        private Guid _originalClientUuid;
        private HttpStatusCode _applicationStatus;
        private HttpStatusCode _apiClientStatus;
        private bool _apiClientCompletedWhileCompensationPaused;
        private int _apiClientReadsWhileCompensationPaused;
        private int _apiClientReadsAfterCompletion;
        private ProviderUpdate[] _updates = [];

        [SetUp]
        public async Task Act()
        {
            SetUpWorkflowFakes((5, 10));
            _originalClientUuid = CurrentClientUuid(5);
            // The application repository update fails, and the compensation's provider
            // rollback is the second provider mutation of the run; the workflow pauses
            // there while it still holds the aggregate lock.
            _applicationUpdateResult = new ApplicationUpdateResult.FailureVendorNotFound();
            _pausedProviderCall = 2;
            using var client = SetUpWorkflowClient();

            Task<HttpResponseMessage> updatingApplication = TrackRequest(
                client.PutAsync(
                    "/v3/applications/10",
                    JsonBody(
                        """
                        {
                          "id": 10,
                          "applicationName": "Renamed Application",
                          "claimSetName": "TestClaimSet",
                          "vendorId": 1,
                          "educationOrganizationIds": [1],
                          "dataStoreIds": [1]
                        }
                        """
                    )
                )
            );
            await _providerCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Task<HttpResponseMessage> updatingApiClient = TrackRequest(
                client.PutAsync(
                    "/v3/apiClients/5",
                    JsonBody(
                        """
                        {
                          "id": 5,
                          "applicationId": 10,
                          "name": "Renamed Client",
                          "isApproved": true,
                          "dataStoreIds": [1]
                        }
                        """
                    )
                )
            );

            // The pre-read precedes the lock acquisition, so once it is observed the
            // contender is waiting on the aggregate lock the paused compensation holds.
            await WaitForAsync(() => ApiClientReads >= 1);
            await Task.Delay(300);
            _apiClientCompletedWhileCompensationPaused = updatingApiClient.IsCompleted;
            _apiClientReadsWhileCompensationPaused = ApiClientReads;

            _providerCallReleased.SetResult();
            _applicationStatus = (await updatingApplication).StatusCode;
            _apiClientStatus = (await updatingApiClient).StatusCode;
            _apiClientReadsAfterCompletion = ApiClientReads;
            _updates = ProviderUpdatesSnapshot();
        }

        [Test]
        public void It_holds_the_api_client_update_at_its_pre_read_while_compensation_is_paused()
        {
            _apiClientCompletedWhileCompensationPaused.Should().BeFalse();
            _apiClientReadsWhileCompensationPaused.Should().Be(1);
        }

        [Test]
        public void It_compensates_from_the_exact_original_state()
        {
            _updates.Should().HaveCount(3);
            _updates[0].TargetedUuid.Should().Be(_originalClientUuid.ToString());
            _updates[1].TargetedUuid.Should().Be(_updates[0].IssuedUuid.ToString());
        }

        [Test]
        public void It_rereads_and_targets_the_compensated_client_under_the_lock()
        {
            _apiClientReadsAfterCompletion.Should().Be(2);
            _updates[2].TargetedUuid.Should().Be(_updates[1].IssuedUuid.ToString());
        }

        [Test]
        public void It_returns_the_original_conflict_for_the_failed_application_update() =>
            _applicationStatus.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_completes_the_api_client_update_against_the_compensated_client() =>
            _apiClientStatus.Should().Be(HttpStatusCode.NoContent);
    }

    [TestFixture]
    public class Given_an_application_update_of_the_target_during_an_api_client_move
        : WorkflowConcurrencyTestBase
    {
        private HttpStatusCode _moveStatus;
        private HttpStatusCode _applicationStatus;
        private bool _applicationCompletedWhileMoveCommitPending;
        private int _applicationReadsWhileMoveCommitPending;
        private int _applicationReadsAfterCompletion;
        private int _movedClientParentAfterCommit;
        private ProviderUpdate[] _updates = [];

        [SetUp]
        public async Task Act()
        {
            SetUpWorkflowFakes((7, 60));
            // The move pauses inside its repository commit, still holding the source and
            // target aggregate locks, so the target application's update must block
            // before any of its under-lock reads and then act on the committed move.
            _pauseFirstApiClientRepositoryUpdate = true;
            using var client = SetUpWorkflowClient();

            Task<HttpResponseMessage> movingApiClient = TrackRequest(
                client.PutAsync(
                    "/v3/apiClients/7",
                    JsonBody(
                        """
                        {
                          "id": 7,
                          "applicationId": 61,
                          "name": "Moved Client",
                          "isApproved": true,
                          "dataStoreIds": [1]
                        }
                        """
                    )
                )
            );
            await _repositoryUpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Task<HttpResponseMessage> updatingApplication = TrackRequest(
                client.PutAsync(
                    "/v3/applications/61",
                    JsonBody(
                        """
                        {
                          "id": 61,
                          "applicationName": "Renamed Target",
                          "claimSetName": "TestClaimSet",
                          "vendorId": 1,
                          "educationOrganizationIds": [1],
                          "dataStoreIds": [1]
                        }
                        """
                    )
                )
            );
            await Task.Delay(300);
            _applicationCompletedWhileMoveCommitPending = updatingApplication.IsCompleted;
            _applicationReadsWhileMoveCommitPending = ApplicationApiClientsReads;

            _repositoryUpdateReleased.SetResult();
            _moveStatus = (await movingApiClient).StatusCode;
            _applicationStatus = (await updatingApplication).StatusCode;
            _applicationReadsAfterCompletion = ApplicationApiClientsReads;
            _movedClientParentAfterCommit = CurrentClientApplicationId(7);
            _updates = ProviderUpdatesSnapshot();
        }

        [Test]
        public void It_blocks_the_target_application_update_while_the_move_commit_is_pending()
        {
            _applicationCompletedWhileMoveCommitPending.Should().BeFalse();
            _applicationReadsWhileMoveCommitPending.Should().Be(0);
        }

        [Test]
        public void It_selects_and_targets_the_committed_moved_client()
        {
            _applicationReadsAfterCompletion.Should().Be(1);
            _movedClientParentAfterCommit.Should().Be(61);
            _updates.Should().HaveCount(2);
            _updates[1].TargetedUuid.Should().Be(_updates[0].IssuedUuid.ToString());
        }

        [Test]
        public void It_completes_the_move() => _moveStatus.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_completes_the_target_application_update_after_the_commit() =>
            _applicationStatus.Should().Be(HttpStatusCode.NoContent);
    }

    [TestFixture]
    public class Given_concurrent_inverse_api_client_moves : WorkflowConcurrencyTestBase
    {
        private HttpStatusCode _firstMoveStatus;
        private HttpStatusCode _secondMoveStatus;
        private bool _firstMoveCompletedWhenSecondEnteredAcquisition;

        [SetUp]
        public async Task Act()
        {
            SetUpWorkflowFakes((701, 71), (702, 72));
            using var client = SetUpWorkflowClient(gateFirstAcquisition: true);

            Task<HttpResponseMessage> firstMove = TrackRequest(
                client.PutAsync(
                    "/v3/apiClients/701",
                    JsonBody(
                        """
                        {
                          "id": 701,
                          "applicationId": 72,
                          "name": "First Mover",
                          "isApproved": true,
                          "dataStoreIds": [1]
                        }
                        """
                    )
                )
            );
            await _acquisitionGate!.FirstAcquisitionHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Task<HttpResponseMessage> secondMove = TrackRequest(
                client.PutAsync(
                    "/v3/apiClients/702",
                    JsonBody(
                        """
                        {
                          "id": 702,
                          "applicationId": 71,
                          "name": "Second Mover",
                          "isApproved": true,
                          "dataStoreIds": [1]
                        }
                        """
                    )
                )
            );
            await _acquisitionGate.SecondAcquisitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            _firstMoveCompletedWhenSecondEnteredAcquisition = firstMove.IsCompleted;

            // With ascending acquisition, both moves contend for the same first lock, so
            // the second cannot own anything yet and the wait below simply elapses. An
            // unordered acquisition instead grabs its own source lock here, which the
            // opened gate turns into the deadlock this fixture must reject.
            await Task.WhenAny(_acquisitionGate.SecondAcquisitionSucceeded.Task, Task.Delay(300));
            _acquisitionGate.Open();

            _firstMoveStatus = (await firstMove).StatusCode;
            _secondMoveStatus = (await secondMove).StatusCode;
        }

        [Test]
        public void It_overlaps_lock_acquisition_between_the_moves() =>
            _firstMoveCompletedWhenSecondEnteredAcquisition.Should().BeFalse();

        [Test]
        public void It_completes_the_first_move() => _firstMoveStatus.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_completes_the_second_move_without_deadlock() =>
            _secondMoveStatus.Should().Be(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// Waits for observable workflow progress rather than a fixed delay, so a fixture never
    /// asserts on a request that simply has not started yet.
    /// </summary>
    private protected static async Task WaitUntilAsync(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException("The expected workflow progress was not observed.");
            }

            await Task.Delay(10);
        }
    }

    [TestFixture]
    public class Given_a_vendor_update_during_a_compensating_application_update : WorkflowConcurrencyTestBase
    {
        private HttpStatusCode _applicationStatus;
        private HttpStatusCode _vendorStatus;
        private bool _vendorCompletedWhileCompensationPaused;
        private int _vendorStateReadsWhileCompensationPaused;
        private int _vendorStateReadsAfterCompletion;
        private ProviderUpdate[] _updates = [];
        private NamespaceUpdate[] _claimUpdates = [];

        [SetUp]
        public async Task Act()
        {
            SetUpWorkflowFakes((5, 10), (6, 30));
            // The application repository update fails, and the compensation's provider rollback
            // is the second provider mutation of the run; the workflow pauses there while it
            // still holds the aggregate lock for application 10.
            _applicationUpdateResult = new ApplicationUpdateResult.FailureVendorNotFound();
            _pausedProviderCall = 2;
            using var client = SetUpWorkflowClient();

            Task<HttpResponseMessage> updatingApplication = TrackRequest(
                client.PutAsync(
                    "/v3/applications/10",
                    JsonBody(
                        """
                        {
                          "id": 10,
                          "applicationName": "Renamed Application",
                          "claimSetName": "TestClaimSet",
                          "vendorId": 1,
                          "educationOrganizationIds": [1],
                          "dataStoreIds": [1]
                        }
                        """
                    )
                )
            );
            await _providerCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Task<HttpResponseMessage> updatingVendor = TrackRequest(
                client.PutAsync("/v3/vendors/1", VendorBody())
            );

            // The pre-read precedes the lock acquisition, so once it is observed the vendor
            // update is waiting on the aggregate lock the paused compensation holds.
            await WaitUntilAsync(() => VendorStateReads >= 1);
            await Task.Delay(300);
            _vendorCompletedWhileCompensationPaused = updatingVendor.IsCompleted;
            _vendorStateReadsWhileCompensationPaused = VendorStateReads;

            _providerCallReleased.SetResult();
            _applicationStatus = (await updatingApplication).StatusCode;
            _vendorStatus = (await updatingVendor).StatusCode;
            _vendorStateReadsAfterCompletion = VendorStateReads;
            _updates = ProviderUpdatesSnapshot();
            _claimUpdates = NamespaceClaimUpdates();
        }

        [Test]
        public void It_holds_the_vendor_update_at_its_pre_read_while_compensation_is_paused()
        {
            _vendorCompletedWhileCompensationPaused.Should().BeFalse();
            _vendorStateReadsWhileCompensationPaused.Should().Be(1);
        }

        [Test]
        public void It_rereads_the_vendor_under_the_locks() =>
            _vendorStateReadsAfterCompletion.Should().Be(2);

        [Test]
        public void It_targets_the_client_the_compensation_left_behind() =>
            _claimUpdates[0].TargetedUuid.Should().Be(_updates[1].IssuedUuid.ToString());

        [Test]
        public void It_updates_every_client_of_the_vendor() => _claimUpdates.Should().HaveCount(2);

        [Test]
        public void It_returns_the_original_conflict_for_the_failed_application_update() =>
            _applicationStatus.Should().Be(HttpStatusCode.Conflict);

        [Test]
        public void It_completes_the_vendor_update() => _vendorStatus.Should().Be(HttpStatusCode.NoContent);
    }

    [TestFixture]
    public class Given_an_api_client_update_during_a_vendor_compensation : WorkflowConcurrencyTestBase
    {
        private HttpStatusCode _vendorStatus;
        private HttpStatusCode _apiClientStatus;
        private bool _apiClientCompletedWhileCompensationPaused;
        private int _apiClientReadsWhileCompensationPaused;
        private ProviderUpdate[] _updates = [];
        private NamespaceUpdate[] _rollbacks = [];
        private Guid _firstClientUuidBefore;

        [SetUp]
        public async Task Act()
        {
            SetUpWorkflowFakes((7, 40), (8, 40));
            _firstClientUuidBefore = CurrentClientUuid(7);
            // The second client's claim update fails, so the workflow restores it and then the
            // client it had already changed. It pauses inside that second restoration, still
            // holding the aggregate lock for application 40.
            _failingNamespaceCall = 2;
            _pausedNamespaceCall = 4;
            using var client = SetUpWorkflowClient();

            Task<HttpResponseMessage> updatingVendor = TrackRequest(
                client.PutAsync("/v3/vendors/1", VendorBody())
            );
            await _namespaceCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Task<HttpResponseMessage> updatingApiClient = TrackRequest(
                client.PutAsync(
                    "/v3/apiClients/7",
                    JsonBody(
                        """
                        {
                          "id": 7,
                          "applicationId": 40,
                          "name": "Renamed Client",
                          "isApproved": true,
                          "dataStoreIds": [1]
                        }
                        """
                    )
                )
            );

            await WaitUntilAsync(() => ApiClientReads >= 1);
            await Task.Delay(300);
            _apiClientCompletedWhileCompensationPaused = updatingApiClient.IsCompleted;
            _apiClientReadsWhileCompensationPaused = ApiClientReads;

            _namespaceCallReleased.SetResult();
            _vendorStatus = (await updatingVendor).StatusCode;
            _apiClientStatus = (await updatingApiClient).StatusCode;
            _updates = ProviderUpdatesSnapshot();
            _rollbacks = NamespaceClaimRollbacks();
        }

        [Test]
        public void It_holds_the_api_client_update_at_its_pre_read_while_compensation_is_paused()
        {
            _apiClientCompletedWhileCompensationPaused.Should().BeFalse();
            _apiClientReadsWhileCompensationPaused.Should().Be(1);
        }

        [Test]
        public void It_restores_both_clients_it_touched() => _rollbacks.Should().HaveCount(2);

        /// <summary>
        /// The claim update replaced the client, so restoring the first client has to address
        /// the replacement the workflow persisted, never the client the snapshot named.
        /// </summary>
        [Test]
        public void It_restores_the_first_client_from_the_uuid_it_had_persisted() =>
            _rollbacks
                .Should()
                .NotContain(rollback => rollback.TargetedUuid == _firstClientUuidBefore.ToString());

        [Test]
        public void It_lets_the_api_client_update_target_the_restored_client() =>
            _rollbacks
                .Select(rollback => rollback.IssuedUuid.ToString())
                .Should()
                .Contain(_updates[0].TargetedUuid);

        [Test]
        public void It_fails_the_vendor_update() =>
            _vendorStatus.Should().Be(HttpStatusCode.InternalServerError);

        [Test]
        public void It_completes_the_api_client_update() =>
            _apiClientStatus.Should().Be(HttpStatusCode.NoContent);
    }

    [TestFixture]
    public class Given_a_vendor_update_and_an_inverse_api_client_move : WorkflowConcurrencyTestBase
    {
        private HttpStatusCode _moveStatus;
        private HttpStatusCode _vendorStatus;
        private bool _moveCompletedWhenVendorEnteredAcquisition;
        private Guid[] _storedUuidsAfterCompletion = [];
        private Guid[] _issuedClaimUuids = [];

        [SetUp]
        public async Task Act()
        {
            SetUpWorkflowFakes((701, 71), (702, 72));
            using var client = SetUpWorkflowClient(gateFirstAcquisition: true);

            Task<HttpResponseMessage> movingApiClient = TrackRequest(
                client.PutAsync(
                    "/v3/apiClients/701",
                    JsonBody(
                        """
                        {
                          "id": 701,
                          "applicationId": 72,
                          "name": "Moved Client",
                          "isApproved": true,
                          "dataStoreIds": [1]
                        }
                        """
                    )
                )
            );
            await _acquisitionGate!.FirstAcquisitionHeld.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Task<HttpResponseMessage> updatingVendor = TrackRequest(
                client.PutAsync("/v3/vendors/1", VendorBody())
            );
            await _acquisitionGate.SecondAcquisitionEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            _moveCompletedWhenVendorEnteredAcquisition = movingApiClient.IsCompleted;

            // Both workflows order their locks ascending, so they contend for the same first
            // lock and neither can own one the other needs. An unordered acquisition would
            // deadlock here once the gate opens.
            await Task.WhenAny(_acquisitionGate.SecondAcquisitionSucceeded.Task, Task.Delay(300));
            _acquisitionGate.Open();

            _moveStatus = (await movingApiClient).StatusCode;
            _vendorStatus = (await updatingVendor).StatusCode;
            _storedUuidsAfterCompletion = [CurrentClientUuid(701), CurrentClientUuid(702)];
            _issuedClaimUuids = [.. NamespaceClaimUpdates().Select(update => update.IssuedUuid)];
        }

        [Test]
        public void It_overlaps_lock_acquisition_between_the_two_workflows() =>
            _moveCompletedWhenVendorEnteredAcquisition.Should().BeFalse();

        [Test]
        public void It_completes_the_move() => _moveStatus.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_completes_the_vendor_update_without_deadlock() =>
            _vendorStatus.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_leaves_every_client_addressable_by_its_persisted_uuid() =>
            _storedUuidsAfterCompletion.Should().BeSubsetOf(_issuedClaimUuids);
    }

    [TestFixture]
    public class Given_a_vendor_repository_commit_blocking_an_application_update : WorkflowConcurrencyTestBase
    {
        private HttpStatusCode _vendorStatus;
        private HttpStatusCode _applicationStatus;
        private bool _applicationCompletedWhileCommitPending;
        private int _applicationReadsWhileCommitPending;
        private int _applicationReadsAfterCompletion;
        private ProviderUpdate[] _updates = [];
        private NamespaceUpdate[] _claimUpdates = [];

        [SetUp]
        public async Task Act()
        {
            SetUpWorkflowFakes((9, 50), (11, 60));
            // The vendor update pauses inside its repository commit, still holding the aggregate
            // locks for both applications, so an application update must block before any of its
            // under-lock reads and then act on the committed client UUIDs.
            _pauseVendorRepositoryUpdate = true;
            using var client = SetUpWorkflowClient();

            Task<HttpResponseMessage> updatingVendor = TrackRequest(
                client.PutAsync("/v3/vendors/1", VendorBody())
            );
            await _vendorRepositoryUpdateStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Task<HttpResponseMessage> updatingApplication = TrackRequest(
                client.PutAsync(
                    "/v3/applications/50",
                    JsonBody(
                        """
                        {
                          "id": 50,
                          "applicationName": "Renamed Application",
                          "claimSetName": "TestClaimSet",
                          "vendorId": 1,
                          "educationOrganizationIds": [1],
                          "dataStoreIds": [1]
                        }
                        """
                    )
                )
            );
            await Task.Delay(300);
            _applicationCompletedWhileCommitPending = updatingApplication.IsCompleted;
            _applicationReadsWhileCommitPending = ApplicationApiClientsReads;

            _vendorRepositoryUpdateReleased.SetResult();
            _vendorStatus = (await updatingVendor).StatusCode;
            _applicationStatus = (await updatingApplication).StatusCode;
            _applicationReadsAfterCompletion = ApplicationApiClientsReads;
            _updates = ProviderUpdatesSnapshot();
            _claimUpdates = NamespaceClaimUpdates();
        }

        [Test]
        public void It_blocks_the_application_update_while_the_commit_is_pending()
        {
            _applicationCompletedWhileCommitPending.Should().BeFalse();
            _applicationReadsWhileCommitPending.Should().Be(0);
        }

        [Test]
        public void It_lets_the_application_read_once_the_commit_completes() =>
            _applicationReadsAfterCompletion.Should().Be(1);

        [Test]
        public void It_lets_the_application_update_target_the_uuid_the_vendor_persisted() =>
            _updates[0].TargetedUuid.Should().Be(_claimUpdates[0].IssuedUuid.ToString());

        [Test]
        public void It_completes_the_vendor_update() => _vendorStatus.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_completes_the_application_update() =>
            _applicationStatus.Should().Be(HttpStatusCode.NoContent);
    }
}
