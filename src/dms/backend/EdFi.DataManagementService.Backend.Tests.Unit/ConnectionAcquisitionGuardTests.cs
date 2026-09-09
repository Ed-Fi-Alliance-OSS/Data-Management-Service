// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// The one connection-acquisition boundary every read-path seam runs inside. What it classifies, what
/// it deliberately does not, and what it writes to a log.
/// </summary>
[TestFixture]
[Parallelizable]
public class ConnectionAcquisitionGuardTests
{
    /// <summary>
    /// A connection string shaped like a real one, so a test can assert no part of it reaches the log.
    /// </summary>
    private const string SecretConnectionString =
        "Server=snapshot,1433;Database=edfi;User Id=sa;Password=abcdefgh1!;";

    private sealed class StubDbException(string message) : DbException(message);

    /// <summary>
    /// Accepts everything. Used where the property under test must hold independently of how an engine
    /// classifies - a cancellation and a disposal are excluded by their own arms, above the classifier,
    /// and a test that relied on the classifier rejecting them would pass even if those arms were gone.
    /// </summary>
    private static readonly Predicate<Exception> _acceptAnything = static _ => true;

    /// <summary>
    /// Stands in for an engine's own description. Type-only, because the engine-specific codes are the
    /// engines' own tests to make; what this fixture pins is that whatever the engine returns is what
    /// reaches the log and the wrapper.
    /// </summary>
    private static readonly Func<Exception, string> _describeByType = static exception =>
        exception.GetType().Name;

    private static Task<T> GuardAsync<T>(
        Func<Task<T>> acquireAsync,
        EffectiveTargetKind targetKind,
        Predicate<Exception>? isExpectedFailure = null,
        Func<Exception, string>? describeFailure = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default
    ) =>
        ConnectionAcquisition.GuardAsync(
            acquireAsync,
            targetKind,
            isExpectedFailure ?? _acceptAnything,
            describeFailure ?? _describeByType,
            logger ?? NullLogger.Instance,
            cancellationToken
        );

    [Test]
    public async Task It_returns_what_the_acquisition_produced()
    {
        object acquired = new();

        object returned = await GuardAsync(() => Task.FromResult(acquired), EffectiveTargetKind.Snapshot);

        returned.Should().BeSameAs(acquired);
    }

    [Test]
    public async Task It_wraps_an_expected_failure_on_a_snapshot_target()
    {
        StubDbException failure = new("connection refused");

        Func<Task> acquire = () =>
            GuardAsync<object>(() => Throw<object>(failure), EffectiveTargetKind.Snapshot);

        var thrown = await acquire.Should().ThrowAsync<DatabaseConnectionUnavailableException>();

        thrown.Which.TargetKind.Should().Be(EffectiveTargetKind.Snapshot);
        thrown
            .Which.InnerException.Should()
            .BeSameAs(failure, "the provider exception is retained for diagnostics");
        thrown
            .Which.Message.Should()
            .NotContain(
                "refused",
                "the wrapper's own message names the target kind and nothing the provider put in its own"
            );
    }

    /// <summary>
    /// The condition that keeps two contracts still: the SQL Server write-failure mapper catches
    /// DbException from session creation, and the custom-view read wrappers catch it too. Both would
    /// stop firing if a primary or read-replica failure were wrapped, because the wrapper is
    /// deliberately not a DbException.
    /// </summary>
    [TestCase(EffectiveTargetKind.Primary)]
    [TestCase(EffectiveTargetKind.ReadReplica)]
    public async Task It_propagates_the_same_failure_unwrapped_on_every_other_kind(
        EffectiveTargetKind targetKind
    )
    {
        StubDbException failure = new("connection refused");

        Func<Task> acquire = () => GuardAsync<object>(() => Throw<object>(failure), targetKind);

        var thrown = await acquire.Should().ThrowAsync<DbException>();

        thrown.Which.Should().BeSameAs(failure);
        thrown.Which.Should().NotBeOfType<DatabaseConnectionUnavailableException>();
    }

    [Test]
    public async Task It_propagates_an_unexpected_exception_on_a_snapshot_target()
    {
        NullReferenceException defect = new();

        Func<Task> acquire = () =>
            GuardAsync<object>(
                () => Throw<object>(defect),
                EffectiveTargetKind.Snapshot,
                isExpectedFailure: PostgresqlOrMssqlWouldReject
            );

        (await acquire.Should().ThrowAsync<NullReferenceException>()).Which.Should().BeSameAs(defect);
    }

    [Test]
    public async Task It_propagates_a_null_argument_failure_on_a_snapshot_target()
    {
        ArgumentNullException defect = NullArgumentFailure();

        Func<Task> acquire = () =>
            GuardAsync<object>(
                () => Throw<object>(defect),
                EffectiveTargetKind.Snapshot,
                isExpectedFailure: PostgresqlOrMssqlWouldReject
            );

        (await acquire.Should().ThrowAsync<ArgumentNullException>()).Which.Should().BeSameAs(defect);
    }

    /// <summary>
    /// Disposal of a data source during shutdown is not an unavailable database. Asserted with a
    /// classifier that accepts everything, so the guarantee rests on the guard's own arm rather than on
    /// an engine predicate happening to reject the type.
    /// </summary>
    [Test]
    public async Task It_propagates_a_disposal_on_a_snapshot_target_whatever_the_classifier_says()
    {
        ObjectDisposedException disposed = new("NpgsqlDataSource");

        Func<Task> acquire = () =>
            GuardAsync<object>(() => Throw<object>(disposed), EffectiveTargetKind.Snapshot);

        (await acquire.Should().ThrowAsync<ObjectDisposedException>()).Which.Should().BeSameAs(disposed);
    }

    /// <summary>
    /// An aborted or timed-out caller is not evidence of a missing snapshot, so it is never wrapped -
    /// again independently of the classifier, and for every kind alike.
    /// </summary>
    [TestCase(EffectiveTargetKind.Primary)]
    [TestCase(EffectiveTargetKind.ReadReplica)]
    [TestCase(EffectiveTargetKind.Snapshot)]
    public async Task It_propagates_a_cancellation_attributable_to_the_supplied_token(
        EffectiveTargetKind targetKind
    )
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        Func<Task> acquire = () =>
            GuardAsync<object>(
                () => Throw<object>(new OperationCanceledException(cancelled.Token)),
                targetKind,
                cancellationToken: cancelled.Token
            );

        // A wrapped cancellation would fail this outright: the wrapper is not an
        // OperationCanceledException, so there is nothing further to assert about what it is not.
        await acquire.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// The engine composes the description; the guard neither invents one nor falls back to the type.
    /// It travels on the wrapper so the translation sites in Core - which cannot see provider types -
    /// log the same thing the acquisition boundary did.
    /// </summary>
    [Test]
    public async Task It_carries_the_engine_description_on_the_wrapper()
    {
        Func<Task> acquire = () =>
            GuardAsync<object>(
                () => Throw<object>(new StubDbException("login failed")),
                EffectiveTargetKind.Snapshot,
                describeFailure: static _ => "SqlException(4060)"
            );

        (await acquire.Should().ThrowAsync<DatabaseConnectionUnavailableException>())
            .Which.FailureDescription.Should()
            .Be("SqlException(4060)");
    }

    [Test]
    public async Task It_logs_the_engine_description_rather_than_the_type()
    {
        CapturingLogger logger = new();

        try
        {
            await GuardAsync<object>(
                () => Throw<object>(new StubDbException($"login failed for '{SecretConnectionString}'")),
                EffectiveTargetKind.Snapshot,
                describeFailure: static _ => "SqlException(4060)",
                logger: logger
            );
        }
        catch (DatabaseConnectionUnavailableException)
        {
            // Expected. What was logged on the way is the behavior under test.
        }

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Message.Should().Contain("SqlException(4060)");
    }

    /// <summary>
    /// The description is asked for only where it is used. A failure the guard does not classify must
    /// not call into the engine at all, which is what keeps a describe implementation from having to
    /// cope with exceptions its classifier already rejected.
    /// </summary>
    [Test]
    public async Task It_asks_for_no_description_on_a_kind_it_does_not_wrap()
    {
        bool described = false;

        try
        {
            await GuardAsync<object>(
                () => Throw<object>(new StubDbException("connection refused")),
                EffectiveTargetKind.ReadReplica,
                describeFailure: _ =>
                {
                    described = true;
                    return "unused";
                }
            );
        }
        catch (DbException)
        {
            // Expected: it propagates.
        }

        described.Should().BeFalse();
    }

    /// <summary>
    /// The exclusion is for a cancellation the caller asked for, not for the exception type. A provider
    /// that raises one while nothing was cancelled is classified like any other failure, so the arm
    /// cannot become a way to escape classification.
    /// </summary>
    [Test]
    public async Task It_classifies_a_cancellation_the_supplied_token_did_not_ask_for()
    {
        Func<Task> acquire = () =>
            GuardAsync<object>(
                () => Throw<object>(new OperationCanceledException("nothing was cancelled")),
                EffectiveTargetKind.Snapshot,
                cancellationToken: CancellationToken.None
            );

        var thrown = await acquire.Should().ThrowAsync<DatabaseConnectionUnavailableException>();

        thrown.Which.InnerException.Should().BeOfType<OperationCanceledException>();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Wrapped_Failure_Is_Logged : ConnectionAcquisitionGuardTests
    {
        private CapturingLogger _logger = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new CapturingLogger();

            try
            {
                await GuardAsync<object>(
                    () => Throw<object>(new StubDbException($"login failed for '{SecretConnectionString}'")),
                    EffectiveTargetKind.Snapshot,
                    logger: _logger
                );
            }
            catch (DatabaseConnectionUnavailableException)
            {
                // Expected. What was logged on the way is the behavior under test.
            }
        }

        [Test]
        public void It_logs_the_exception_type_and_the_target_kind()
        {
            _logger.Entries.Should().ContainSingle();
            _logger.Entries[0].Message.Should().Contain(nameof(StubDbException));
            _logger.Entries[0].Message.Should().Contain(nameof(EffectiveTargetKind.Snapshot));
        }

        /// <summary>
        /// A provider quotes the offending connection string in its message, so neither the message nor
        /// the exception object may reach a log sink - the S6667 suppression in the guard is exactly
        /// this rule.
        /// </summary>
        [Test]
        public void It_logs_no_part_of_the_connection_string()
        {
            _logger.Entries[0].Message.Should().NotContain("Password");
            _logger.Entries[0].Message.Should().NotContain("snapshot,1433");
            _logger.Entries[0].Message.Should().NotContain("login failed");
        }

        [Test]
        public void It_does_not_hand_the_exception_to_the_log_sink()
        {
            _logger
                .Entries[0]
                .Exception.Should()
                .BeNull("the exception object carries the connection string in its message chain");
        }

        [Test]
        public void It_logs_at_warning()
        {
            _logger.Entries[0].Level.Should().Be(LogLevel.Warning);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Failure_On_A_Kind_The_Guard_Does_Not_Handle : ConnectionAcquisitionGuardTests
    {
        [Test]
        public async Task It_logs_nothing()
        {
            CapturingLogger logger = new();

            try
            {
                await GuardAsync<object>(
                    () => Throw<object>(new StubDbException("connection refused")),
                    EffectiveTargetKind.ReadReplica,
                    logger: logger
                );
            }
            catch (DbException)
            {
                // Expected: it propagates. Whether anything was logged is the point.
            }

            logger
                .Entries.Should()
                .BeEmpty("the guard reports only what it classifies, and it classifies only a snapshot");
        }
    }

    /// <summary>
    /// Stands in for the real engine predicates, both of which reject a defect raised inside the
    /// acquisition boundary. Named rather than inlined so the intent is legible at the call site.
    /// </summary>
    private static bool PostgresqlOrMssqlWouldReject(Exception exception) =>
        exception is not (NullReferenceException or ArgumentNullException);

    private static Task<T> Throw<T>(Exception exception) => Task.FromException<T>(exception);

    /// <summary>
    /// A null-argument failure carrying a parameter name that really exists, which is what the static
    /// analyzer requires of any ArgumentException construction.
    /// </summary>
    private static ArgumentNullException NullArgumentFailure(string? connectionString = null) =>
        new(
            nameof(connectionString),
            $"'{nameof(connectionString)}' must not be {connectionString ?? "null"}."
        );

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
