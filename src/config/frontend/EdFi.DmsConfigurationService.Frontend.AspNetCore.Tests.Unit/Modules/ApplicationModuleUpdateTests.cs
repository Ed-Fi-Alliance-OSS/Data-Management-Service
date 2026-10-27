// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// PUT /v3/applications/{id} (DMS-1642): the Application is saved first, then each client the save
/// left pending is updated in the identity provider, in id order, and its marker cleared once the
/// provider confirms. The repository decides which clients are pending; these tests drive it through
/// fakes.
/// </summary>
public partial class ApplicationModuleTests
{
    private static StringContent UpdateContent(
        string applicationName = "Renamed Application",
        string claimSetName = "OtherClaimSet",
        long[]? educationOrganizationIds = null,
        int[]? profileIds = null
    ) =>
        new(
            $$"""
            {
                "id": 1,
                "applicationName": "{{applicationName}}",
                "claimSetName": "{{claimSetName}}",
                "vendorId": 1,
                "educationOrganizationIds": [{{string.Join(
                ", ",
                educationOrganizationIds ?? [255901, 255902]
            )}}],
                "profileIds": [{{string.Join(", ", profileIds ?? [])}}]
            }
            """,
            Encoding.UTF8,
            "application/json"
        );

    private void AssertNoProviderUpdate() =>
        A.CallTo(() =>
                _clientRepository.UpdateClientAsync(
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<string>.Ignored,
                    A<int[]?>.Ignored,
                    A<bool>.Ignored,
                    A<string>.Ignored
                )
            )
            .MustNotHaveHappened();

    /// <summary>
    /// Three pending clients with distinct scopes, data stores and approval. The database is
    /// simulated: a confirmed clear removes a client from the pending set that the sync-state read
    /// returns, and each fixture can make a provider update or a clear misbehave.
    /// </summary>
    public abstract class UpdateWorkflowTestBase : ApplicationModuleTests
    {
        protected sealed record ProviderUpdate(
            string ClientUuid,
            string DisplayName,
            string ClaimSetName,
            string EducationOrganizationIds,
            int[]? DataStoreIds,
            bool IsApproved,
            string Role
        );

        protected static readonly ApplicationSyncPendingClient First = new(
            3,
            "key-3",
            Guid.NewGuid(),
            true,
            [1, 2],
            [255901, 255902],
            new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        );
        protected static readonly ApplicationSyncPendingClient Second = new(
            5,
            "key-5",
            Guid.NewGuid(),
            false,
            [],
            [],
            new DateTime(2030, 1, 2, 3, 4, 6, DateTimeKind.Utc)
        );
        protected static readonly ApplicationSyncPendingClient Third = new(
            8,
            "key-8",
            Guid.NewGuid(),
            true,
            [2],
            [255902],
            new DateTime(2029, 12, 31, 0, 0, 0, DateTimeKind.Utc)
        );

        protected static ApplicationSyncState SavedState(params ApplicationSyncPendingClient[] pending) =>
            new("Renamed Application", 1, "OtherClaimSet", [255901, 255902], [], pending);

        private readonly object _gate = new();
        private readonly Dictionary<int, ApplicationSyncPendingClient> _stillPending = [];

        protected List<ProviderUpdate> ProviderUpdates { get; } = [];
        protected List<int> Clears { get; } = [];
        protected List<string> CallOrder { get; } = [];
        protected Dictionary<Guid, Func<ClientUpdateResult>> ProviderOutcomes { get; } = [];
        protected Dictionary<int, Func<ApiClientSyncPendingResult>> ClearOutcomes { get; } = [];
        private protected RecordingLogger<ApplicationModule> ModuleLogger { get; private set; } = new();
        protected List<ApplicationUpdateCommand> SavedCommands { get; } = [];
        protected List<DateTime?> ClearedTo { get; } = [];
        protected HttpResponseMessage Response { get; private set; } = null!;
        protected List<bool> LockReleasedAtProviderCall { get; } = [];
        private RecordingLockManager _recordingLockManager = null!;

        protected bool LockReleased => _recordingLockManager.Handle.Disposed;
        protected IReadOnlyList<int> LockedApplicationIds => _recordingLockManager.AcquiredApplicationIds;

        /// <summary>Thrown by the save instead of returning <see cref="SaveResult"/> when set.</summary>
        protected Exception? SaveException { get; set; }

        /// <summary>The marker clear of these clients reports a failure although it took effect.</summary>
        protected HashSet<int> ClearsTakingEffectDespiteFailure { get; } = [];

        protected ApplicationSaveResult SaveResult { get; set; } =
            new ApplicationSaveResult.Success(SavedState(First, Second, Third));

        /// <summary>Overrides the simulated sync-state read when set.</summary>
        protected Func<ApplicationSyncStateResult>? SyncStateRead { get; set; }

        // NUnit reuses the fixture instance for each test, and runs this before the fixture's own setup
        [SetUp]
        public void ResetWorkflowState()
        {
            lock (_gate)
            {
                _stillPending.Clear();
            }

            ProviderUpdates.Clear();
            Clears.Clear();
            CallOrder.Clear();
            ProviderOutcomes.Clear();
            ClearOutcomes.Clear();
            ClearsTakingEffectDespiteFailure.Clear();
            SavedCommands.Clear();
            ClearedTo.Clear();
            LockReleasedAtProviderCall.Clear();
            ModuleLogger = new();
            SaveResult = new ApplicationSaveResult.Success(SavedState(First, Second, Third));
            SaveException = null;
            SyncStateRead = null;
        }

        protected async Task ActAsync(StringContent? content = null)
        {
            if (SaveException is null && SaveResult is ApplicationSaveResult.Success saved)
            {
                Seed(saved.State.PendingClients);
            }

            _recordingLockManager = new RecordingLockManager(() => CallOrder.Add("lock"));
            _lockManager = _recordingLockManager;

            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .ReturnsLazily(call =>
                {
                    CallOrder.Add("save");
                    SavedCommands.Add(call.GetArgument<ApplicationUpdateCommand>(0)!);
                    if (SaveException is { } exception)
                    {
                        throw exception;
                    }

                    return Task.FromResult(SaveResult);
                });

            A.CallTo(() => _applicationRepository.GetApplicationSyncState(A<int>.Ignored))
                .ReturnsLazily(() =>
                {
                    CallOrder.Add("read");
                    if (SyncStateRead is { } read)
                    {
                        return Task.FromResult(read());
                    }

                    lock (_gate)
                    {
                        return Task.FromResult<ApplicationSyncStateResult>(
                            new ApplicationSyncStateResult.Success(
                                SavedState([.. _stillPending.Values.OrderBy(client => client.Id)])
                            )
                        );
                    }
                });

            A.CallTo(() =>
                    _clientRepository.UpdateClientAsync(
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<string>.Ignored,
                        A<int[]?>.Ignored,
                        A<bool>.Ignored,
                        A<string>.Ignored
                    )
                )
                .ReturnsLazily(call =>
                {
                    string clientUuid = call.GetArgument<string>(0)!;
                    CallOrder.Add($"provider:{clientUuid}");
                    LockReleasedAtProviderCall.Add(LockReleased);
                    ProviderUpdates.Add(
                        new(
                            clientUuid,
                            call.GetArgument<string>(1)!,
                            call.GetArgument<string>(2)!,
                            call.GetArgument<string>(3)!,
                            call.GetArgument<int[]?>(4),
                            call.GetArgument<bool>(5),
                            call.GetArgument<string>(6)!
                        )
                    );
                    return Task.FromResult(
                        ProviderOutcomes.TryGetValue(Guid.Parse(clientUuid), out var outcome)
                            ? outcome()
                            : new ClientUpdateResult.Success(Guid.Parse(clientUuid))
                    );
                });

            A.CallTo(() =>
                    _apiClientRepository.SetApiClientIdentityProviderSyncPending(
                        A<int>.Ignored,
                        A<DateTime?>.Ignored
                    )
                )
                .ReturnsLazily(call =>
                {
                    int apiClientId = call.GetArgument<int>(0);
                    ClearedTo.Add(call.GetArgument<DateTime?>(1));
                    CallOrder.Add($"clear:{apiClientId}");
                    Clears.Add(apiClientId);
                    if (ClearOutcomes.TryGetValue(apiClientId, out var outcome))
                    {
                        if (ClearsTakingEffectDespiteFailure.Contains(apiClientId))
                        {
                            Forget(apiClientId);
                        }

                        return Task.FromResult(outcome());
                    }

                    Forget(apiClientId);
                    return Task.FromResult<ApiClientSyncPendingResult>(
                        new ApiClientSyncPendingResult.Success()
                    );
                });

            using var client = SetUpClient(configureServices: services =>
                services.AddSingleton<ILogger<ApplicationModule>>(ModuleLogger)
            );
            Response = await client.PutAsync("/v3/applications/1", content ?? UpdateContent());
        }

        protected void Seed(IEnumerable<ApplicationSyncPendingClient> pending)
        {
            lock (_gate)
            {
                foreach (ApplicationSyncPendingClient client in pending)
                {
                    _stillPending[client.Id] = client;
                }
            }
        }

        private void Forget(int apiClientId)
        {
            lock (_gate)
            {
                _stillPending.Remove(apiClientId);
            }
        }

        [TearDown]
        public void DisposeResponse() => Response?.Dispose();

        protected static ProviderUpdate ExpectedUpdate(ApplicationSyncPendingClient client) =>
            new(
                client.ClientUuid.ToString(),
                "Renamed Application",
                "OtherClaimSet",
                string.Join(",", client.EducationOrganizationIds),
                client.DataStoreIds,
                client.IsApproved,
                "dms-client"
            );

        protected string[] Warnings =>
            [.. ModuleLogger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message)];

        protected string AllLogText => string.Join("\n", ModuleLogger.Entries.Select(e => e.Message));

        protected async Task ShouldBePartialSyncAsync(params int[] apiClientIds)
        {
            Response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            Response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
            JsonNode actual = JsonNode.Parse(await Response.Content.ReadAsStringAsync())!;
            string detail =
                apiClientIds.Length == 1
                    ? "The Application change is saved, but 1 API client is still pending synchronization with the identity provider. Send the same request again to retry it."
                    : $"The Application change is saved, but {apiClientIds.Length} API clients are still pending synchronization with the identity provider. Send the same request again to retry them.";
            JsonNode expected = new JsonObject
            {
                ["detail"] = detail,
                ["type"] = "urn:ed-fi:management-api:bad-gateway:partial-sync",
                ["title"] = "Bad Gateway",
                ["status"] = 502,
                ["correlationId"] = actual["correlationId"]!.GetValue<string>(),
                ["validationErrors"] = new JsonObject(),
                ["errors"] = new JsonArray(),
                ["apiClientIds"] = new JsonArray([.. apiClientIds.Select(id => JsonValue.Create(id))]),
            };
            JsonNode.DeepEquals(actual, expected).Should().BeTrue(actual.ToJsonString());
        }
    }

    [TestFixture]
    public class Given_an_application_update_whose_pending_clients_all_synchronize : UpdateWorkflowTestBase
    {
        [SetUp]
        public async Task Setup() => await ActAsync();

        [Test]
        public void It_returns_no_content() => Response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_sends_each_client_the_saved_name_and_claim_set_with_its_own_scope_data_stores_and_approval() =>
            ProviderUpdates
                .Should()
                .BeEquivalentTo(
                    [ExpectedUpdate(First), ExpectedUpdate(Second), ExpectedUpdate(Third)],
                    options => options.WithStrictOrdering()
                );

        [Test]
        public void It_saves_first_then_updates_and_clears_each_client_in_id_order() =>
            CallOrder
                .Should()
                .Equal(
                    "lock",
                    "save",
                    $"provider:{First.ClientUuid}",
                    "clear:3",
                    $"provider:{Second.ClientUuid}",
                    "clear:5",
                    $"provider:{Third.ClientUuid}",
                    "clear:8"
                );

        [Test]
        public void It_saves_the_request_once() =>
            SavedCommands.Should().ContainSingle().Which.ApplicationName.Should().Be("Renamed Application");

        [Test]
        public void It_clears_each_marker_to_null() =>
            ClearedTo.Should().HaveCount(3).And.AllSatisfy(value => value.Should().BeNull());

        [Test]
        public void It_holds_the_lock_through_every_provider_update_and_then_releases_it()
        {
            LockReleasedAtProviderCall.Should().Equal(false, false, false);
            LockReleased.Should().BeTrue();
            LockedApplicationIds.Should().Equal(1);
        }

        [Test]
        public void It_logs_no_warning() => Warnings.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_application_update_that_leaves_no_client_pending : UpdateWorkflowTestBase
    {
        [SetUp]
        public async Task Setup()
        {
            SaveResult = new ApplicationSaveResult.Success(SavedState());
            await ActAsync();
        }

        [Test]
        public void It_returns_no_content() => Response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_makes_no_provider_call_and_clears_nothing()
        {
            ProviderUpdates.Should().BeEmpty();
            Clears.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_an_application_update_whose_second_client_is_rejected_by_the_provider
        : UpdateWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_PROVIDER_REJECTION";

        [SetUp]
        public async Task Setup()
        {
            ProviderOutcomes[Second.ClientUuid] = () =>
                new ClientUpdateResult.FailureIdentityProvider(new IdentityProviderError(Sentinel));
            await ActAsync();
        }

        [Test]
        public async Task It_returns_the_partial_sync_contract_for_that_client() =>
            await ShouldBePartialSyncAsync(5);

        [Test]
        public async Task It_does_not_return_the_provider_text() =>
            (await Response.Content.ReadAsStringAsync()).Should().NotContain(Sentinel);

        [Test]
        public void It_still_updates_and_clears_the_later_client()
        {
            ProviderUpdates
                .Select(update => update.ClientUuid)
                .Should()
                .Equal(
                    First.ClientUuid.ToString(),
                    Second.ClientUuid.ToString(),
                    Third.ClientUuid.ToString()
                );
            Clears.Should().Equal(3, 8);
        }

        [Test]
        public void It_warns_with_the_numeric_ids() =>
            Warnings
                .Should()
                .Contain(message => message.Contains("API client 5 of Application 1"))
                .And.Contain(message =>
                    message.Contains("API clients are still pending an identity provider update (1): 5")
                );
    }

    [TestFixture]
    public class Given_an_application_update_whose_first_provider_update_throws : UpdateWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_PROVIDER_THROW";

        [SetUp]
        public async Task Setup()
        {
            ProviderOutcomes[First.ClientUuid] = () => throw new InvalidOperationException(Sentinel);
            await ActAsync();
        }

        [Test]
        public async Task It_returns_the_partial_sync_contract_for_that_client() =>
            await ShouldBePartialSyncAsync(3);

        [Test]
        public void It_still_updates_and_clears_the_later_clients() => Clears.Should().Equal(5, 8);

        [Test]
        public void It_logs_the_exception_type_but_not_its_message()
        {
            AllLogText.Should().Contain(nameof(InvalidOperationException)).And.NotContain(Sentinel);
            ModuleLogger.Entries.Should().AllSatisfy(entry => entry.Exception.Should().BeNull());
        }
    }

    [TestFixture]
    public class Given_an_application_update_whose_provider_reports_other_clients : UpdateWorkflowTestBase
    {
        private readonly Guid _reportedUuid = Guid.NewGuid();

        [SetUp]
        public async Task Setup()
        {
            ProviderOutcomes[Third.ClientUuid] = () => new ClientUpdateResult.Success(_reportedUuid);
            ProviderOutcomes[First.ClientUuid] = () =>
                new ClientUpdateResult.FailureNotFound("missing provider client");
            await ActAsync();
        }

        [Test]
        public async Task It_keeps_both_clients_pending() => await ShouldBePartialSyncAsync(3, 8);

        [Test]
        public void It_clears_only_the_confirmed_client() => Clears.Should().Equal(5);

        [Test]
        public void It_never_addresses_the_reported_client()
        {
            ProviderUpdates.Should().NotContain(update => update.ClientUuid == _reportedUuid.ToString());
            A.CallTo(() => _clientRepository.DeleteClientAsync(A<string>.Ignored)).MustNotHaveHappened();
        }

        [Test]
        public void It_warns_that_the_stored_client_is_kept() =>
            Warnings
                .Should()
                .Contain(message =>
                    message.Contains(_reportedUuid.ToString()) && message.Contains("API client 8")
                );
    }

    [TestFixture]
    public class Given_an_application_update_whose_marker_clear_fails : UpdateWorkflowTestBase
    {
        [SetUp]
        public async Task Setup()
        {
            ClearOutcomes[Second.Id] = () => new ApiClientSyncPendingResult.FailureUnknown("clear failed");
            await ActAsync();
        }

        [Test]
        public async Task It_reports_the_client_the_read_back_still_shows_pending() =>
            await ShouldBePartialSyncAsync(5);

        [Test]
        public async Task It_reports_the_provider_updated_client_as_pending_synchronization_not_as_a_provider_failure()
        {
            ProviderUpdates
                .Should()
                .ContainSingle(update => update.ClientUuid == Second.ClientUuid.ToString());
            string detail = JsonNode.Parse(await Response.Content.ReadAsStringAsync())![
                "detail"
            ]!.GetValue<string>();
            detail
                .Should()
                .Be(
                    "The Application change is saved, but 1 API client is still pending synchronization with the identity provider. Send the same request again to retry it."
                );
            detail.Should().NotContain("could not be updated");
        }

        [Test]
        public void It_reads_the_markers_back_after_the_last_client() => CallOrder[^1].Should().Be("read");
    }

    [TestFixture]
    public class Given_an_application_update_whose_failed_marker_clear_took_effect : UpdateWorkflowTestBase
    {
        [SetUp]
        public async Task Setup()
        {
            ClearOutcomes[First.Id] = () => throw new TimeoutException("SENTINEL_CLEAR_THROW");
            ClearsTakingEffectDespiteFailure.Add(First.Id);
            await ActAsync();
        }

        [Test]
        public void It_returns_no_content_because_the_read_back_shows_nothing_pending() =>
            Response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_continues_with_the_other_clients() => Clears.Should().Equal(3, 5, 8);
    }

    [TestFixture]
    public class Given_an_application_update_whose_pending_clients_cannot_be_read_back
        : UpdateWorkflowTestBase
    {
        private const string Sentinel = "SENTINEL_READ_BACK";

        [SetUp]
        public async Task Setup()
        {
            ProviderOutcomes[Second.ClientUuid] = () => new ClientUpdateResult.FailureUnknown("down");
            SyncStateRead = () => new ApplicationSyncStateResult.FailureUnknown(Sentinel);
            await ActAsync();
        }

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error_without_a_pending_list() =>
            await AssertSanitizedInternalServerError(Response, Sentinel);
    }

    [TestFixture]
    public class Given_an_application_save_with_an_unknown_outcome_that_committed : UpdateWorkflowTestBase
    {
        [SetUp]
        public async Task Setup()
        {
            SaveResult = new ApplicationSaveResult.FailureCommitOutcomeUnknown("SENTINEL_COMMIT");
            Seed([Third]);
            await ActAsync(UpdateContent(educationOrganizationIds: [255902, 255901, 255902]));
        }

        [Test]
        public void It_returns_no_content() => Response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_brings_the_committed_pending_clients_forward() =>
            ProviderUpdates.Should().BeEquivalentTo([ExpectedUpdate(Third)]);

        [Test]
        public void It_reads_the_committed_state_before_any_provider_call() =>
            CallOrder.Take(3).Should().Equal("lock", "save", "read");
    }

    [TestFixture]
    public class Given_an_application_save_that_throws_and_committed : UpdateWorkflowTestBase
    {
        [SetUp]
        public async Task Setup()
        {
            SaveException = new InvalidOperationException("SENTINEL_SAVE_THROW");
            Seed([First]);
            await ActAsync();
        }

        [Test]
        public void It_returns_no_content() => Response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_brings_the_committed_pending_client_forward() => Clears.Should().Equal(3);

        [Test]
        public void It_does_not_log_the_exception_message() =>
            AllLogText.Should().NotContain("SENTINEL_SAVE_THROW");
    }

    public abstract class UnprovenSaveTestBase : UpdateWorkflowTestBase
    {
        protected const string Sentinel = "SENTINEL_UNPROVEN";

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(Response, Sentinel);

        [Test]
        public void It_makes_no_provider_call_and_clears_nothing()
        {
            ProviderUpdates.Should().BeEmpty();
            Clears.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_an_application_save_with_an_unknown_outcome_whose_state_differs : UnprovenSaveTestBase
    {
        [SetUp]
        public async Task Setup()
        {
            SaveResult = new ApplicationSaveResult.FailureCommitOutcomeUnknown(Sentinel);
            Seed([First]);
            // The committed state carries the previous claim set
            await ActAsync(UpdateContent(claimSetName: "PreviousClaimSet"));
        }
    }

    [TestFixture]
    public class Given_an_application_save_with_an_unknown_outcome_whose_profiles_differ
        : UnprovenSaveTestBase
    {
        [SetUp]
        public async Task Setup()
        {
            SaveResult = new ApplicationSaveResult.FailureCommitOutcomeUnknown(Sentinel);
            Seed([First]);
            await ActAsync(UpdateContent(profileIds: [4]));
        }
    }

    [TestFixture]
    public class Given_an_application_save_with_an_unknown_outcome_whose_state_cannot_be_read
        : UnprovenSaveTestBase
    {
        [SetUp]
        public async Task Setup()
        {
            SaveResult = new ApplicationSaveResult.FailureCommitOutcomeUnknown(Sentinel);
            SyncStateRead = () => new ApplicationSyncStateResult.FailureUnknown(Sentinel);
            await ActAsync();
        }
    }

    [TestFixture]
    public class Given_an_application_save_with_an_unknown_outcome_whose_application_is_gone
        : UnprovenSaveTestBase
    {
        [SetUp]
        public async Task Setup()
        {
            SaveResult = new ApplicationSaveResult.FailureCommitOutcomeUnknown(Sentinel);
            SyncStateRead = () => new ApplicationSyncStateResult.FailureNotExists();
            await ActAsync();
        }
    }

    [TestFixture]
    public class Given_an_application_save_that_rolled_back : UnprovenSaveTestBase
    {
        [SetUp]
        public async Task Setup()
        {
            SaveResult = new ApplicationSaveResult.FailureUnknown(Sentinel);
            await ActAsync();
        }

        [Test]
        public void It_does_not_read_the_state_back() => CallOrder.Should().Equal("lock", "save");
    }

    [TestFixture]
    public class Given_an_application_update_when_the_aggregate_lock_times_out : ApplicationModuleTests
    {
        private List<string> _dependencyCalls = null!;
        private HttpResponseMessage _updateResponse = null!;

        [SetUp]
        public async Task Act()
        {
            _dependencyCalls = [];
            A.CallTo(() => _lockManager.AcquireAsync(A<int>.Ignored, A<CancellationToken>.Ignored))
                .Returns(new ApplicationLockResult.FailureTimeout());
            A.CallTo(_applicationRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_apiClientRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_clientRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_vendorRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_dataStoreRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));
            A.CallTo(_profileRepository).Invokes(call => _dependencyCalls.Add(call.Method.Name));

            using var client = SetUpClient();
            _updateResponse = await client.PutAsync("/v3/applications/1", UpdateContent());
        }

        [TearDown]
        public void DisposeResponse() => _updateResponse?.Dispose();

        [Test]
        public async Task It_returns_the_retriable_conflict_contract() =>
            await AssertLockConflictContract(_updateResponse);

        [Test]
        public void It_calls_no_repository_or_identity_provider_dependency() =>
            _dependencyCalls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_an_application_update_when_the_aggregate_lock_cannot_be_acquired
        : ApplicationModuleTests
    {
        private const string Sentinel = "SENTINEL_LOCK_ACQUIRE_must_not_leak";
        private HttpResponseMessage _updateResponse = null!;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _lockManager.AcquireAsync(A<int>.Ignored, A<CancellationToken>.Ignored))
                .Returns(new ApplicationLockResult.FailureUnknown(Sentinel));

            using var client = SetUpClient();
            _updateResponse = await client.PutAsync("/v3/applications/1", UpdateContent());
        }

        [TearDown]
        public void DisposeResponse() => _updateResponse?.Dispose();

        [Test]
        public async Task It_returns_a_sanitized_internal_server_error() =>
            await AssertSanitizedInternalServerError(_updateResponse, Sentinel);

        [Test]
        public void It_does_not_save_the_application() =>
            A.CallTo(() => _applicationRepository.SaveApplication(A<ApplicationUpdateCommand>.Ignored))
                .MustNotHaveHappened();
    }
}
