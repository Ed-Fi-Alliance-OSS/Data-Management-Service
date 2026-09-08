// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DocumentCacheWriterRetry
{
    private static readonly DocumentCacheProjectionTargetKey TargetKey = new("tenant-a", new DataStoreId(7));

    [TestFixture]
    [Parallelizable]
    public class Given_Transient_Provider_Failures_Then_Success : Given_DocumentCacheWriterRetry
    {
        private readonly List<int> _attemptNumbers = [];
        private CapturingLogger<DocumentCacheWriterRetryAdapter> _logger = null!;
        private DocumentCacheWriterResult _result = null!;
        private int _stateLockCount;
        private int _classificationCount;
        private int _cacheDmlCount;
        private int _acknowledgementCount;
        private int _cacheAheadReclassificationCount;

        [SetUp]
        public async Task Setup()
        {
            _attemptNumbers.Clear();
            _stateLockCount = 0;
            _classificationCount = 0;
            _cacheDmlCount = 0;
            _acknowledgementCount = 0;
            _cacheAheadReclassificationCount = 0;
            _logger = new CapturingLogger<DocumentCacheWriterRetryAdapter>();
            DocumentCacheWriterRetryAdapter sut = CreateSut(maxRetryAttempts: 3, logger: _logger);

            _result = await sut.ExecuteAsync(
                CreateRequest(),
                (context, _) =>
                {
                    _attemptNumbers.Add(context.AttemptNumber);
                    _stateLockCount++;
                    _classificationCount++;
                    _cacheDmlCount++;
                    _acknowledgementCount++;
                    _cacheAheadReclassificationCount++;

                    if (context.AttemptNumber < 3)
                    {
                        throw new FakeTransientDbException();
                    }

                    return Task.FromResult<DocumentCacheWriterResult>(
                        new DocumentCacheWriterResult.CandidateWrittenAcknowledged(
                            DocumentCacheWriterContractTestData.CreateCandidate(),
                            acknowledgedContentVersion: 11
                        )
                    );
                }
            );
        }

        [Test]
        public void It_returns_the_successful_attempt_result()
        {
            _result.Should().BeOfType<DocumentCacheWriterResult.CandidateWrittenAcknowledged>();
        }

        [Test]
        public void It_replays_the_complete_writer_attempt_for_each_retry()
        {
            _attemptNumbers.Should().Equal(1, 2, 3);
            _stateLockCount.Should().Be(3);
            _classificationCount.Should().Be(3);
            _cacheDmlCount.Should().Be(3);
            _acknowledgementCount.Should().Be(3);
            _cacheAheadReclassificationCount.Should().Be(3);
        }

        [Test]
        public void It_logs_bounded_retry_diagnostics()
        {
            string logMessages = _logger.JoinedMessages();

            logMessages.Should().Contain("postgresql");
            logMessages.Should().Contain("tenant-a:7");
            logMessages.Should().Contain(nameof(DocumentCacheWriterPurpose.DurableWorkProjection));
            logMessages.Should().Contain(nameof(DocumentCacheWriterOutcome.CandidateWrittenAcknowledged));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Transient_Provider_Failures_Exhaust_Budget : Given_DocumentCacheWriterRetry
    {
        private const string SensitiveDocumentUuid = "11111111-1111-1111-1111-111111111111";

        private CapturingLogger<DocumentCacheWriterRetryAdapter> _logger = null!;
        private DocumentCacheWriterResult _result = null!;
        private int _attemptCount;

        [SetUp]
        public async Task Setup()
        {
            _attemptCount = 0;
            _logger = new CapturingLogger<DocumentCacheWriterRetryAdapter>();
            DocumentCacheWriterRetryAdapter sut = CreateSut(maxRetryAttempts: 2, logger: _logger);

            _result = await sut.ExecuteAsync(
                CreateRequest(),
                (_, _) =>
                {
                    _attemptCount++;
                    throw new FakeTransientDbException(
                        "DocumentId=998877 DocumentUuid="
                            + SensitiveDocumentUuid
                            + " DocumentJson={\"nameOfInstitution\":\"Lincoln\"} authorization-token "
                            + "ResourceName=School request body"
                    );
                }
            );
        }

        [Test]
        public void It_returns_retry_budget_exhausted()
        {
            _result
                .Should()
                .BeOfType<DocumentCacheWriterResult.RetryBudgetExhausted>()
                .Which.AttemptCount.Should()
                .Be(3);
        }

        [Test]
        public void It_uses_the_configured_retry_budget()
        {
            _attemptCount.Should().Be(3);
        }

        [Test]
        public void It_omits_document_identifiers_payloads_authorization_and_resource_labels_from_logs()
        {
            string logMessages = _logger.JoinedMessages();

            logMessages.Should().NotContain(SensitiveDocumentUuid);
            logMessages.Should().NotContain("DocumentId");
            logMessages.Should().NotContain("DocumentJson");
            logMessages.Should().NotContain("authorization-token");
            logMessages.Should().NotContain("ResourceName");
            logMessages.Should().NotContain("School");
            logMessages.Should().NotContain("request body");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Retries_Disabled : Given_DocumentCacheWriterRetry
    {
        private DocumentCacheWriterResult _result = null!;
        private int _attemptCount;

        [SetUp]
        public async Task Setup()
        {
            _attemptCount = 0;
            DocumentCacheWriterRetryAdapter sut = CreateSut(maxRetryAttempts: 0);

            _result = await sut.ExecuteAsync(
                CreateRequest(),
                (_, _) =>
                {
                    _attemptCount++;
                    throw new FakeTransientDbException();
                }
            );
        }

        [Test]
        public void It_executes_one_attempt()
        {
            _attemptCount.Should().Be(1);
        }

        [Test]
        public void It_surfaces_retry_budget_exhausted()
        {
            _result
                .Should()
                .BeOfType<DocumentCacheWriterResult.RetryBudgetExhausted>()
                .Which.AttemptCount.Should()
                .Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Caller_Cancellation : Given_DocumentCacheWriterRetry
    {
        private DocumentCacheWriterResult _result = null!;
        private int _attemptCount;

        [SetUp]
        public async Task Setup()
        {
            _attemptCount = 0;
            using CancellationTokenSource cancellationTokenSource = new();
            DocumentCacheWriterRetryAdapter sut = CreateSut(maxRetryAttempts: 3);

            _result = await sut.ExecuteAsync(
                CreateRequest(cancellationTokenSource.Token),
                (_, _) =>
                {
                    _attemptCount++;
                    cancellationTokenSource.Cancel();
                    throw new OperationCanceledException(cancellationTokenSource.Token);
                }
            );
        }

        [Test]
        public void It_does_not_retry_after_caller_cancellation()
        {
            _attemptCount.Should().Be(1);
        }

        [Test]
        public void It_returns_caller_aborted_retry()
        {
            _result
                .Should()
                .BeOfType<DocumentCacheWriterResult.CallerAbortedRetry>()
                .Which.AttemptCount.Should()
                .Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Non_Transient_Provider_Failure : Given_DocumentCacheWriterRetry
    {
        private Exception? _exception;
        private int _attemptCount;

        [SetUp]
        public async Task Setup()
        {
            _exception = null;
            _attemptCount = 0;
            DocumentCacheWriterRetryAdapter sut = CreateSut(maxRetryAttempts: 3);

            try
            {
                await sut.ExecuteAsync(
                    CreateRequest(),
                    (_, _) =>
                    {
                        _attemptCount++;
                        throw new FakeNonTransientDbException();
                    }
                );
            }
            catch (Exception exception)
            {
                _exception = exception;
            }
        }

        [Test]
        public void It_does_not_retry()
        {
            _attemptCount.Should().Be(1);
        }

        [Test]
        public void It_preserves_exception_flow()
        {
            _exception.Should().BeOfType<FakeNonTransientDbException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Deterministic_Invariant_Result : Given_DocumentCacheWriterRetry
    {
        private DocumentCacheWriterResult _result = null!;
        private int _attemptCount;

        [SetUp]
        public async Task Setup()
        {
            _attemptCount = 0;
            DocumentCacheWriterRetryAdapter sut = CreateSut(maxRetryAttempts: 3);

            _result = await sut.ExecuteAsync(
                CreateRequest(),
                (_, _) =>
                {
                    _attemptCount++;
                    return Task.FromResult<DocumentCacheWriterResult>(
                        new DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure(
                            DocumentCacheWriterInvariantFailureReason.MatchingVersionDocumentUuidMismatch,
                            currentContentVersion: 11,
                            candidateContentVersion: 11
                        )
                    );
                }
            );
        }

        [Test]
        public void It_does_not_retry()
        {
            _attemptCount.Should().Be(1);
        }

        [Test]
        public void It_returns_the_deterministic_result()
        {
            _result.Should().BeOfType<DocumentCacheWriterResult.DeterministicInvariantOrTargetFailure>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Direct_Fill_Bounded_Result : Given_DocumentCacheWriterRetry
    {
        private RecordingDocumentCacheWriterTelemetry _telemetry = null!;
        private DocumentCacheWriterResult _result = null!;
        private int _attemptCount;

        [SetUp]
        public async Task Setup()
        {
            _attemptCount = 0;
            _telemetry = new RecordingDocumentCacheWriterTelemetry();
            DocumentCacheWriterRetryAdapter sut = CreateSut(maxRetryAttempts: 3, telemetry: _telemetry);

            _result = await sut.ExecuteAsync(
                CreateRequest(purpose: DocumentCacheWriterPurpose.DirectFill),
                (_, _) =>
                {
                    _attemptCount++;
                    return Task.FromResult<DocumentCacheWriterResult>(
                        new DocumentCacheWriterResult.WorkAnomaly(
                            DocumentCacheWriterWorkAnomalyKind.MissingWork,
                            DocumentCacheLifecycleState.Rebuilding,
                            currentSourceContentVersion: 11,
                            workRequiredContentVersion: null
                        )
                    );
                }
            );
        }

        [Test]
        public void It_surfaces_the_typed_direct_fill_result_without_retry_or_swallowing()
        {
            _attemptCount.Should().Be(1);
            _result
                .Should()
                .BeOfType<DocumentCacheWriterResult.WorkAnomaly>()
                .Which.LifecycleState.Should()
                .Be(DocumentCacheLifecycleState.Rebuilding);
        }

        [Test]
        public void It_records_direct_fill_retry_context_with_bounded_outcome_and_lifecycle()
        {
            RecordedRetry retry = _telemetry.Retries.Should().ContainSingle().Which;

            retry.AttemptCount.Should().Be(1);
            retry.Context.Provider.Should().Be(RelationalProviderToken.PostgresqlValue);
            retry.Context.TargetKey.Should().Be("tenant-a:7");
            retry.Context.Purpose.Should().Be(nameof(DocumentCacheWriterPurpose.DirectFill));
            retry.Context.Lifecycle.Should().Be(nameof(DocumentCacheLifecycleState.Rebuilding));
            retry.Context.Outcome.Should().Be(nameof(DocumentCacheWriterOutcome.WorkAnomaly));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Delete_Race_Exhausts_Retry_Budget : Given_DocumentCacheWriterRetry
    {
        private DocumentCacheWriterResult _result = null!;
        private int _attemptCount;

        [SetUp]
        public async Task Setup()
        {
            _attemptCount = 0;
            DocumentCacheWriterRetryAdapter sut = CreateSut(maxRetryAttempts: 2);

            _result = await sut.ExecuteAsync(
                CreateRequest(),
                (_, _) =>
                {
                    _attemptCount++;
                    throw new DocumentCacheWriterRetryableDeleteRaceException();
                }
            );
        }

        [Test]
        public void It_replays_attempts_through_the_budget()
        {
            _attemptCount.Should().Be(3);
        }

        [Test]
        public void It_returns_delete_race_retry_exhausted()
        {
            _result
                .Should()
                .BeOfType<DocumentCacheWriterResult.DeleteRaceRetryExhausted>()
                .Which.AttemptCount.Should()
                .Be(3);
        }
    }

    private static DocumentCacheWriterRetryAdapter CreateSut(
        int maxRetryAttempts,
        CapturingLogger<DocumentCacheWriterRetryAdapter>? logger = null,
        IDocumentCacheWriterTelemetry? telemetry = null
    ) =>
        new(
            new DeadlockRetrySettings
            {
                MaxRetryAttempts = maxRetryAttempts,
                BaseDelayMilliseconds = 1,
                UseJitter = false,
            },
            new FakeRelationalWriteExceptionClassifier(),
            logger ?? new CapturingLogger<DocumentCacheWriterRetryAdapter>(),
            telemetry
        );

    private static DocumentCacheWriterRetryRequest CreateRequest(
        CancellationToken cancellationToken = default,
        DocumentCacheWriterPurpose purpose = DocumentCacheWriterPurpose.DurableWorkProjection
    ) => new(RelationalProviderToken.Postgresql, TargetKey, purpose, cancellationToken);

    private sealed class RecordingDocumentCacheWriterTelemetry : IDocumentCacheWriterTelemetry
    {
        public List<RecordedRetry> Retries { get; } = [];

        public void RecordOutcome(DocumentCacheWriterMetricContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        public void RecordTransactionDuration(DocumentCacheWriterMetricContext context, TimeSpan duration)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        public void RecordCacheDmlDuration(DocumentCacheWriterMetricContext context, TimeSpan duration)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        public void RecordAcknowledgementDuration(DocumentCacheWriterMetricContext context, TimeSpan duration)
        {
            ArgumentNullException.ThrowIfNull(context);
        }

        public void RecordRetry(DocumentCacheWriterMetricContext context, TimeSpan duration, int attemptCount)
        {
            Retries.Add(new RecordedRetry(context, duration, attemptCount));
        }

        public void RecordSameDocumentWait(
            DocumentCacheWriterMetricContext context,
            DocumentCacheWriterContentionParticipant participant,
            DocumentCacheWriterContentionPhase phase,
            TimeSpan duration
        )
        {
            ArgumentNullException.ThrowIfNull(context);
        }
    }

    private sealed record RecordedRetry(
        DocumentCacheWriterMetricContext Context,
        TimeSpan Duration,
        int AttemptCount
    );

    private sealed class FakeRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
    {
        public bool TryClassify(
            DbException exception,
            [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
        )
        {
            ArgumentNullException.ThrowIfNull(exception);
            classification = null;
            return false;
        }

        public bool IsForeignKeyViolation(DbException exception) => false;

        public bool IsUniqueConstraintViolation(DbException exception) => false;

        public bool IsTransientFailure(DbException exception) => exception is FakeTransientDbException;
    }

    private sealed class FakeTransientDbException(string message = "transient") : DbException(message);

    private sealed class FakeNonTransientDbException : DbException { }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = [];

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
            _messages.Add(formatter(state, exception));
        }

        public string JoinedMessages() => string.Join('\n', _messages);
    }
}

internal static class DocumentCacheWriterContractTestData
{
    public static DocumentCacheMaterializationCandidate CreateCandidate() =>
        new(
            documentId: 123,
            new DocumentUuid(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            projectName: "Ed-Fi",
            resourceName: "School",
            resourceVersion: "5.3.0",
            contentVersion: 11,
            lastModifiedAt: DateTimeOffset.Parse(
                "2026-01-01T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture
            ),
            streamEtag: "\"11-fixed-stream\"",
            documentJson: System
                .Text.Json.Nodes.JsonNode.Parse(
                    """
                    {"id":"11111111-1111-1111-1111-111111111111","nameOfInstitution":"Lincoln High"}
                    """
                )!
                .AsObject()
        );
}
