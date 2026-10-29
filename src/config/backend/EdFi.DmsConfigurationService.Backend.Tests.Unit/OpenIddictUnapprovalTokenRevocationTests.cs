// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DmsConfigurationService.Backend.Keycloak;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.TestHelpers;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// DMS-1493 D-3: the self-contained un-approval participant locks the application row a token store
/// waits on, then revokes the client's tokens, on the connection and transaction the Configuration
/// Service hands it. It never commits or rolls back: the caller's transaction decides both.
/// </summary>
public class OpenIddictUnapprovalTokenRevocationTests
{
    private const string RevocationLog = "outstanding tokens for client";
    private const string AbsentApplicationLog = "No identity provider application exists for client";

    public abstract class ParticipantTestBase
    {
        protected readonly Guid _clientUuid = Guid.NewGuid();
        protected IOpenIddictDataRepository _dataRepository = null!;
        protected ILogger<OpenIddictUnapprovalTokenRevocation> _logger = null!;
        protected IDbConnection _connection = null!;
        protected IDbTransaction _transaction = null!;
        protected OpenIddictUnapprovalTokenRevocation _participant = null!;

        [SetUp]
        public void SetUpParticipant()
        {
            _dataRepository = A.Fake<IOpenIddictDataRepository>();
            _logger = A.Fake<ILogger<OpenIddictUnapprovalTokenRevocation>>();
            _connection = A.Fake<IDbConnection>();
            _transaction = A.Fake<IDbTransaction>();
            _participant = new OpenIddictUnapprovalTokenRevocation(_dataRepository, _logger);
        }

        [TearDown]
        public void DisposeDatabaseFakes()
        {
            _transaction.Dispose();
            _connection.Dispose();
        }

        protected void ArrangeLock(bool applicationExists) =>
            A.CallTo(() => _dataRepository.LockApplicationAsync(_clientUuid, _connection, _transaction))
                .Returns(applicationExists);

        protected void ArrangeRevocation(int revokedCount) =>
            A.CallTo(() =>
                    _dataRepository.RevokeTokensForApplicationAsync(_clientUuid, _connection, _transaction)
                )
                .Returns(revokedCount);

        protected Task<int> ActAsync() =>
            _participant.RevokeOutstandingTokensAsync(_clientUuid, _connection, _transaction);

        protected void AssertLockThenRevoke() =>
            A.CallTo(() => _dataRepository.LockApplicationAsync(_clientUuid, _connection, _transaction))
                .MustHaveHappenedOnceExactly()
                .Then(
                    A.CallTo(() =>
                            _dataRepository.RevokeTokensForApplicationAsync(
                                _clientUuid,
                                _connection,
                                _transaction
                            )
                        )
                        .MustHaveHappenedOnceExactly()
                );

        protected void AssertTransactionLeftToTheCaller()
        {
            A.CallTo(() => _transaction.Commit()).MustNotHaveHappened();
            A.CallTo(() => _transaction.Rollback()).MustNotHaveHappened();
            A.CallTo(() => _transaction.Dispose()).MustNotHaveHappened();
        }

        /// <summary>
        /// The count message says the revocation is pending in the caller's transaction, not done:
        /// the caller may still roll it back (DMS-1493 S3.3-F1).
        /// </summary>
        protected void AssertRevocationLogged(int revokedCount) =>
            _logger
                .LoggedEntries()
                .Should()
                .ContainSingle(entry => entry.Message.Contains(RevocationLog))
                .Which.Should()
                .Match<(LogLevel Level, string Message, Exception? Exception)>(entry =>
                    entry.Level == LogLevel.Information
                    && entry.Message
                        == $"Marked {revokedCount} outstanding tokens for client {_clientUuid} revoked in the pending un-approval transaction; they stay valid if it rolls back"
                );
    }

    /// <summary>
    /// The application row exists: the participant locks it, then revokes on the same connection and
    /// transaction, and reports the count. Fails if the revocation runs first, runs outside the
    /// caller's transaction, or the participant ends the transaction itself.
    /// </summary>
    [TestFixture]
    public class Given_a_participant_for_an_existing_application : ParticipantTestBase
    {
        private int _result;

        [SetUp]
        public async Task Act()
        {
            ArrangeLock(true);
            ArrangeRevocation(3);

            _result = await ActAsync();
        }

        [Test]
        public void It_returns_the_revoked_count() => _result.Should().Be(3);

        [Test]
        public void It_locks_the_application_then_revokes_in_the_callers_transaction() =>
            AssertLockThenRevoke();

        [Test]
        public void It_leaves_commit_and_rollback_to_the_caller() => AssertTransactionLeftToTheCaller();

        [Test]
        public void It_logs_the_revocation_count_with_the_client_uuid() => AssertRevocationLogged(3);

        [Test]
        public void It_logs_no_absent_application() =>
            _logger.VerifyNoLog(LogLevel.Information, AbsentApplicationLog);
    }

    /// <summary>
    /// No application row exists, for example after a provider deletion. Token rows have no foreign key
    /// to the application, so the participant still revokes them, after the lock statement, and logs
    /// the absence. Fails if a missing row skips the revocation.
    /// </summary>
    [TestFixture]
    public class Given_a_participant_for_an_absent_application : ParticipantTestBase
    {
        private int _result;

        [SetUp]
        public async Task Act()
        {
            ArrangeLock(false);
            ArrangeRevocation(1);

            _result = await ActAsync();
        }

        [Test]
        public void It_returns_the_revoked_count() => _result.Should().Be(1);

        [Test]
        public void It_still_revokes_after_the_lock_statement() => AssertLockThenRevoke();

        [Test]
        public void It_leaves_commit_and_rollback_to_the_caller() => AssertTransactionLeftToTheCaller();

        [Test]
        public void It_logs_the_absent_application_at_information() =>
            _logger
                .LoggedEntries()
                .Should()
                .ContainSingle(entry =>
                    entry.Level == LogLevel.Information
                    && entry.Message
                        == $"{AbsentApplicationLog} {_clientUuid}; its stored tokens are marked revoked in the pending un-approval transaction regardless"
                );

        [Test]
        public void It_logs_the_revocation_count_with_the_client_uuid() => AssertRevocationLogged(1);
    }

    /// <summary>
    /// The revocation fails. The exception reaches the caller unchanged, so the caller's transaction
    /// rolls back the un-approval with it. Fails if the participant swallows or wraps the failure.
    /// </summary>
    [TestFixture]
    public class Given_a_participant_whose_revocation_throws : ParticipantTestBase
    {
        private readonly InvalidOperationException _failure = new("revocation failed");
        private Exception? _thrown;

        [SetUp]
        public async Task Act()
        {
            ArrangeLock(true);
            A.CallTo(() =>
                    _dataRepository.RevokeTokensForApplicationAsync(_clientUuid, _connection, _transaction)
                )
                .ThrowsAsync(_failure);

            try
            {
                await ActAsync();
            }
            catch (Exception exception)
            {
                _thrown = exception;
            }
        }

        [Test]
        public void It_propagates_the_failure_unchanged() => _thrown.Should().BeSameAs(_failure);

        [Test]
        public void It_leaves_commit_and_rollback_to_the_caller() => AssertTransactionLeftToTheCaller();

        [Test]
        public void It_logs_no_revocation() => _logger.VerifyNoLog(LogLevel.Information, RevocationLog);
    }

    /// <summary>
    /// The lock statement fails. The exception reaches the caller unchanged and nothing is revoked.
    /// Fails if the participant revokes without having taken the lock.
    /// </summary>
    [TestFixture]
    public class Given_a_participant_whose_lock_throws : ParticipantTestBase
    {
        private readonly TimeoutException _failure = new("lock failed");
        private Exception? _thrown;

        [SetUp]
        public async Task Act()
        {
            A.CallTo(() => _dataRepository.LockApplicationAsync(_clientUuid, _connection, _transaction))
                .ThrowsAsync(_failure);

            try
            {
                await ActAsync();
            }
            catch (Exception exception)
            {
                _thrown = exception;
            }
        }

        [Test]
        public void It_propagates_the_failure_unchanged() => _thrown.Should().BeSameAs(_failure);

        [Test]
        public void It_revokes_nothing() =>
            A.CallTo(() =>
                    _dataRepository.RevokeTokensForApplicationAsync(
                        A<Guid>._,
                        A<IDbConnection>._,
                        A<IDbTransaction>._
                    )
                )
                .MustNotHaveHappened();
    }

    /// <summary>
    /// The Keycloak participant: the Configuration Service stores no Keycloak token, so it revokes
    /// nothing and does not touch the caller's connection or transaction.
    /// </summary>
    [TestFixture]
    public class Given_the_keycloak_no_op_participant
    {
        private IDbConnection _connection = null!;
        private IDbTransaction _transaction = null!;
        private int _result;

        [SetUp]
        public async Task Act()
        {
            _connection = A.Fake<IDbConnection>();
            _transaction = A.Fake<IDbTransaction>();

            _result = await new NoOpUnapprovalTokenRevocation().RevokeOutstandingTokensAsync(
                Guid.NewGuid(),
                _connection,
                _transaction
            );
        }

        [TearDown]
        public void DisposeDatabaseFakes()
        {
            _transaction.Dispose();
            _connection.Dispose();
        }

        [Test]
        public void It_revokes_nothing() => _result.Should().Be(0);

        [Test]
        public void It_does_not_use_the_connection_or_the_transaction()
        {
            Fake.GetCalls(_connection).Should().BeEmpty();
            Fake.GetCalls(_transaction).Should().BeEmpty();
        }
    }
}
