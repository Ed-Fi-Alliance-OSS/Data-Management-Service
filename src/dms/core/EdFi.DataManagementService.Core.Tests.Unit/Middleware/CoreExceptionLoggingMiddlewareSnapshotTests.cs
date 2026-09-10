// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// Seams 4 through 7 translate here, which is the one site that covers every read path below the two
/// validation middlewares - GET-by-id, GET-many, descriptors, and the change-query routes alike -
/// because nothing in the backend intercepts the exception on its way up.
/// </summary>
[TestFixture]
[Parallelizable]
public class CoreExceptionLoggingMiddlewareSnapshotTests
{
    private const string ConnectionString = "Server=snapshot;Database=edfi;Password=hunter2";

    /// <summary>
    /// What an engine would have composed: the provider type and its own error code, and nothing else.
    /// Distinct from anything in the connection string, so a log assertion that the string is absent
    /// still means something when this is present.
    /// </summary>
    private const string FailureDescription = "TimeoutException(-2)";

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    /// <summary>
    /// A provider exception of the shape the seam guard classifies. Its message quotes the connection
    /// string back, which is exactly what must never reach a log.
    /// </summary>
    private static TimeoutException ProviderFailure() => new($"connection timed out for {ConnectionString}");

    private static async Task<(RequestInfo RequestInfo, CapturingLogger Logger)> ExecuteWith(Exception thrown)
    {
        RequestInfo requestInfo = No.RequestInfo("traceId");
        CapturingLogger logger = new();
        var middleware = new CoreExceptionLoggingMiddleware(logger, null);

        await middleware.Execute(requestInfo, () => throw thrown);

        return (requestInfo, logger);
    }

    private static Task<(RequestInfo RequestInfo, CapturingLogger Logger)> ExecuteWith(
        EffectiveTargetKind kind
    ) => ExecuteWith(new DatabaseConnectionUnavailableException(kind, FailureDescription, ProviderFailure()));

    [Test]
    public async Task It_answers_snapshot_not_found_for_a_snapshot()
    {
        var (requestInfo, _) = await ExecuteWith(EffectiveTargetKind.Snapshot);

        requestInfo.FrontendResponse.ShouldBeSnapshotNotFound("traceId");
    }

    /// <summary>
    /// The one arm in this middleware that logs and captures nothing.
    /// RequestResponseLoggingMiddleware hands CaughtException to LogError with its full message chain,
    /// and the chain here ends in the provider exception that quotes the connection string.
    /// </summary>
    [Test]
    public async Task It_leaves_the_caught_exception_unset()
    {
        var (requestInfo, _) = await ExecuteWith(EffectiveTargetKind.Snapshot);

        requestInfo.CaughtException.Should().BeNull();
    }

    [Test]
    public async Task It_logs_the_failure_and_the_target_kind()
    {
        var (_, logger) = await ExecuteWith(EffectiveTargetKind.Snapshot);

        logger
            .Entries.Should()
            .Contain(entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.Contains("Database connection unavailable")
                && entry.Message.Contains(nameof(TimeoutException))
                && entry.Message.Contains("for Snapshot target")
            );
    }

    [Test]
    public async Task It_never_logs_connection_material()
    {
        var (_, logger) = await ExecuteWith(EffectiveTargetKind.Snapshot);

        logger.Entries.Should().NotContain(entry => entry.Message.Contains("Password=hunter2"));
        logger
            .Entries.Should()
            .OnlyContain(
                entry => entry.Exception == null,
                "the wrapper's inner provider exception quotes the connection string in its message"
            );
    }

    /// <summary>
    /// Only a snapshot's response depends on the failure being a connection failure. A primary or a
    /// read replica keeps the generic 500 the unhandled path has always produced - not the
    /// custom-view system-error 500, which carries a different body and content type.
    /// </summary>
    /// <remarks>
    /// Unreachable in production: the acquisition guard constructs this wrapper only for a snapshot.
    /// Asserted anyway, because the arm exists precisely so that widening the guard cannot silently
    /// route a wrapper into the generic catch - see <see cref="It_captures_no_wrapper_of_any_kind" />.
    /// </remarks>
    [TestCase(EffectiveTargetKind.Primary)]
    [TestCase(EffectiveTargetKind.ReadReplica)]
    public async Task It_keeps_the_existing_500_for_a_non_snapshot(EffectiveTargetKind kind)
    {
        var (requestInfo, _) = await ExecuteWith(kind);

        requestInfo.FrontendResponse.StatusCode.Should().Be(500);
        requestInfo.FrontendResponse.ContentType.Should().Be("application/json");

        JsonObject body = requestInfo.FrontendResponse.Body!.AsObject();
        body.Select(property => property.Key).Should().BeEquivalentTo("message", "traceId");
    }

    /// <summary>
    /// No kind's wrapper is ever assigned to the request's caught-exception field, which
    /// RequestResponseLoggingMiddleware hands to LogError with its full message chain - and the chain
    /// ends in the provider exception that quotes the connection string. The snapshot arm has always
    /// held this; the other kinds hold it because they are caught by the same arm rather than falling
    /// through to the generic one.
    /// </summary>
    [TestCase(EffectiveTargetKind.Primary)]
    [TestCase(EffectiveTargetKind.ReadReplica)]
    [TestCase(EffectiveTargetKind.Snapshot)]
    public async Task It_captures_no_wrapper_of_any_kind(EffectiveTargetKind kind)
    {
        var (requestInfo, logger) = await ExecuteWith(kind);

        requestInfo.CaughtException.Should().BeNull();
        logger.Entries.Should().NotContain(entry => entry.Message.Contains("Password=hunter2"));
        logger.Entries.Should().OnlyContain(entry => entry.Exception == null);
    }

    /// <summary>
    /// The engine's description, not a bare type name: the provider's own error code is what separates
    /// a wrong password from an absent catalog from an unreachable host, and it is the only diagnostic
    /// beyond the type that may accompany the target kind into a log.
    /// </summary>
    [Test]
    public async Task It_logs_the_engine_failure_description()
    {
        var (_, logger) = await ExecuteWith(EffectiveTargetKind.Snapshot);

        logger.Entries.Should().Contain(entry => entry.Message.Contains(FailureDescription));
    }

    /// <summary>
    /// The snapshot arm sits after the cancellation rethrow and must not have displaced it: a client
    /// that walked away is not answered with a body at all, snapshot or otherwise.
    /// </summary>
    [Test]
    public async Task It_still_propagates_request_cancellation()
    {
        using CancellationTokenSource cancellationSource = new();
        await cancellationSource.CancelAsync();

        RequestInfo requestInfo = No.RequestInfo("traceId");
        requestInfo.RequestCancellationToken = cancellationSource.Token;
        CapturingLogger logger = new();
        var middleware = new CoreExceptionLoggingMiddleware(logger, null);

        Func<Task> act = async () =>
            await middleware.Execute(
                requestInfo,
                () => throw new OperationCanceledException(cancellationSource.Token)
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
        requestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
    }
}
