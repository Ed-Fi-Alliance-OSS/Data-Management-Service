// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DmsConfigurationService.Backend;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// PUT /v3/apiClients/{id} completes a client's pending identity provider synchronization
/// (DMS-1642): the save clears the marker only after the provider confirmed the update, and an
/// ambiguous save counts as committed only when the marker is gone too.
/// </summary>
public partial class ApiClientModuleTests
{
    /// <summary>A client pending since an earlier Application update, sent an unchanged request.</summary>
    public abstract class PendingClientUpdateTestBase : UpdateUnderLockTestBase
    {
        protected static readonly DateTime PendingSince = new(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        protected List<ApiClientUpdateCommand> SavedCommands { get; } = [];
        protected List<string> ProviderUpdates { get; } = [];

        [SetUp]
        public void SetUpPendingClient()
        {
            SavedCommands.Clear();
            ProviderUpdates.Clear();

            A.CallTo(() => _apiClientRepository.GetApiClientEducationOrganizationState(A<int>.Ignored))
                .Returns(
                    new ApiClientEducationOrganizationStateResult.Success(
                        new ApiClientEducationOrganizationState(1, true, [1], [1], PendingSince)
                    )
                );

            // The provider updates the client in place
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
                .Invokes(call => ProviderUpdates.Add(call.GetArgument<string>(0)!))
                .ReturnsLazily(() => new ClientUpdateResult.Success(_existingUuid));

            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Invokes(call => SavedCommands.Add(call.GetArgument<ApiClientUpdateCommand>(0)!))
                .Returns(new ApiClientUpdateResult.Success());
        }

        /// <summary>The client exactly as stored, with the given pending time.</summary>
        protected ApiClientResolutionState StoredState(DateTime? pendingSince) =>
            new(1, "Test", true, "test-client", _existingUuid, [1], true, [1], pendingSince);

        protected Task ActUnchangedUpdateAsync() => ActUpdateAsync(name: "Test");

        protected void AssertNoMarkerWrite() =>
            A.CallTo(() =>
                    _apiClientRepository.SetApiClientIdentityProviderSyncPending(
                        A<int>.Ignored,
                        A<DateTime?>.Ignored
                    )
                )
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_an_unchanged_update_of_a_pending_client : PendingClientUpdateTestBase
    {
        [SetUp]
        public async Task Act() => await ActUnchangedUpdateAsync();

        [Test]
        public void It_returns_no_content() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_sends_the_client_to_the_provider_before_saving() =>
            ProviderUpdates.Should().Equal(_existingUuid.ToString());

        [Test]
        public void It_saves_once_with_the_synchronization_completed() =>
            SavedCommands.Should().ContainSingle().Which.CompletesIdentityProviderSync.Should().BeTrue();
    }

    [TestFixture]
    public class Given_an_update_of_a_pending_client_rejected_by_the_provider : PendingClientUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
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
                .Returns(new ClientUpdateResult.FailureIdentityProvider(new IdentityProviderError("down")));
            await ActUnchangedUpdateAsync();
        }

        [Test]
        public void It_returns_bad_gateway() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        [Test]
        public void It_neither_saves_nor_touches_the_marker()
        {
            SavedCommands.Should().BeEmpty();
            AssertNoMarkerWrite();
        }
    }

    /// <summary>
    /// The save of an unchanged request throws. The stored client matches the request either way,
    /// so only the marker tells whether the save committed.
    /// </summary>
    public abstract class AmbiguousUnchangedUpdateTestBase : PendingClientUpdateTestBase
    {
        [SetUp]
        public void SetUpThrowingSave() =>
            A.CallTo(() => _apiClientRepository.UpdateApiClient(A<ApiClientUpdateCommand>.Ignored))
                .Invokes(call => SavedCommands.Add(call.GetArgument<ApiClientUpdateCommand>(0)!))
                .Throws(new InvalidOperationException("SENTINEL_AMBIGUOUS_SAVE"));
    }

    [TestFixture]
    public class Given_an_ambiguous_unchanged_update_that_committed_the_completion
        : AmbiguousUnchangedUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<int>.Ignored))
                .Returns(new ApiClientResolutionResult.Success(StoredState(null)));
            await ActUnchangedUpdateAsync();
        }

        [Test]
        public void It_returns_no_content() =>
            _updateResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        [Test]
        public void It_does_not_roll_the_provider_back() => ProviderUpdates.Should().HaveCount(1);
    }

    [TestFixture]
    public class Given_an_ambiguous_unchanged_update_whose_client_is_still_pending
        : AmbiguousUnchangedUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<int>.Ignored))
                .Returns(new ApiClientResolutionResult.Success(StoredState(PendingSince)));
            await ActUnchangedUpdateAsync();
        }

        [Test]
        public async Task It_does_not_report_success() =>
            await AssertSanitizedInternalServerError(_updateResponse, "SENTINEL_AMBIGUOUS_SAVE");

        [Test]
        public void It_treats_the_save_as_rolled_back_and_restores_the_provider() =>
            ProviderUpdates.Should().Equal(_existingUuid.ToString(), _existingUuid.ToString());

        [Test]
        public void It_writes_no_marker() => AssertNoMarkerWrite();
    }

    [TestFixture]
    public class Given_an_ambiguous_unchanged_update_whose_outcome_cannot_be_read
        : AmbiguousUnchangedUpdateTestBase
    {
        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _apiClientRepository.GetApiClientResolutionState(A<int>.Ignored))
                .Returns(new ApiClientResolutionResult.FailureUnknown("SENTINEL_RESOLUTION"));
            await ActUnchangedUpdateAsync();
        }

        [Test]
        public async Task It_does_not_report_success() =>
            await AssertSanitizedInternalServerError(_updateResponse, "SENTINEL_RESOLUTION");

        [Test]
        public void It_neither_rolls_the_provider_back_nor_writes_a_marker()
        {
            ProviderUpdates.Should().HaveCount(1);
            AssertNoMarkerWrite();
        }
    }

    [TestFixture]
    public class Given_a_credential_reset_of_a_pending_client : PendingClientUpdateTestBase
    {
        private HttpResponseMessage _resetResponse = null!;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _identityProviderRepository.ResetCredentialsAsync(A<string>.Ignored))
                .Returns(new ClientResetResult.Success("rotated-secret"));
            using var client = SetUpClient();
            _resetResponse = await client.PutAsync(
                "/v3/apiClients/1/reset-credential",
                new StringContent("{}", Encoding.UTF8, "application/json")
            );
        }

        [TearDown]
        public void DisposeReset() => _resetResponse?.Dispose();

        [Test]
        public void It_returns_ok() => _resetResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        [Test]
        public void It_changes_only_the_secret()
        {
            ProviderUpdates.Should().BeEmpty();
            SavedCommands.Should().BeEmpty();
            AssertNoMarkerWrite();
        }
    }
}
