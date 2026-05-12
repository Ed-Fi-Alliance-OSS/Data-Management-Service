// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Handler;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Polly;
using Polly.Retry;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
public class DeadlockRetryPolicyTests
{
    private static readonly Lock _retryPipelineBuildLock = new();

    /// <summary>
    /// Builds a resilience pipeline that mirrors the retry configuration
    /// used in DmsCoreServiceExtensions, without the circuit breaker or telemetry layers.
    /// When maxRetryAttempts is 0, the retry strategy is skipped (matching production behavior).
    /// </summary>
    private static ResiliencePipeline<object> BuildPipeline(
        int maxRetryAttempts,
        int baseDelayMs = 1,
        bool useJitter = false
    )
    {
        lock (_retryPipelineBuildLock)
        {
            var builder = new ResiliencePipelineBuilder<object>();

            if (maxRetryAttempts > 0)
            {
                builder.AddRetry(
                    new RetryStrategyOptions<object>
                    {
                        BackoffType = DelayBackoffType.Exponential,
                        MaxRetryAttempts = maxRetryAttempts,
                        Delay = TimeSpan.FromMilliseconds(baseDelayMs),
                        UseJitter = useJitter,
                        ShouldHandle = new PredicateBuilder<object>().HandleResult(Utility.IsRetryableResult),
                    }
                );
            }

            return builder.Build();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Upsert_WriteConflict_Then_Success : DeadlockRetryPolicyTests
    {
        private object? _result;
        private int _callCount;

        [SetUp]
        public async Task Setup()
        {
            _callCount = 0;
            var pipeline = BuildPipeline(maxRetryAttempts: 3);

            _result = await pipeline.ExecuteAsync(async _ =>
            {
                _callCount++;
                await Task.CompletedTask;
                return _callCount < 3
                    ? (object)new UpsertResult.UpsertFailureWriteConflict()
                    : new UpsertResult.InsertSuccess(new DocumentUuid(Guid.NewGuid()));
            });
        }

        [Test]
        public void It_retries_and_returns_success()
        {
            _result.Should().BeOfType<UpsertResult.InsertSuccess>();
        }

        [Test]
        public void It_called_the_callback_exactly_three_times()
        {
            _callCount.Should().Be(3);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Upsert_WriteConflict_All_Retries : DeadlockRetryPolicyTests
    {
        private object? _result;
        private int _callCount;

        [SetUp]
        public async Task Setup()
        {
            _callCount = 0;
            var pipeline = BuildPipeline(maxRetryAttempts: 3);

            _result = await pipeline.ExecuteAsync(async _ =>
            {
                _callCount++;
                await Task.CompletedTask;
                return (object)new UpsertResult.UpsertFailureWriteConflict();
            });
        }

        [Test]
        public void It_exhausts_retries_and_returns_WriteConflict()
        {
            _result.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();
        }

        [Test]
        public void It_called_the_callback_four_times()
        {
            _callCount.Should().Be(4);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Non_Retryable_Failure : DeadlockRetryPolicyTests
    {
        private object? _result;
        private int _callCount;

        [SetUp]
        public async Task Setup()
        {
            _callCount = 0;
            var pipeline = BuildPipeline(maxRetryAttempts: 3);

            _result = await pipeline.ExecuteAsync(async _ =>
            {
                _callCount++;
                await Task.CompletedTask;
                return (object)
                    new UpsertResult.UpsertFailureIdentityConflict(new ResourceName("TestResource"), []);
            });
        }

        [Test]
        public void It_does_not_retry()
        {
            _result.Should().BeOfType<UpsertResult.UpsertFailureIdentityConflict>();
        }

        [Test]
        public void It_called_the_callback_exactly_once()
        {
            _callCount.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Upsert_Etag_Mismatch_Failure : DeadlockRetryPolicyTests
    {
        private object? _result;
        private int _callCount;

        [SetUp]
        public async Task Setup()
        {
            _callCount = 0;
            var pipeline = BuildPipeline(maxRetryAttempts: 3);

            _result = await pipeline.ExecuteAsync(async _ =>
            {
                _callCount++;
                await Task.CompletedTask;
                return (object)new UpsertResult.UpsertFailureETagMisMatch();
            });
        }

        [Test]
        public void It_does_not_retry()
        {
            _result.Should().BeOfType<UpsertResult.UpsertFailureETagMisMatch>();
        }

        [Test]
        public void It_called_the_callback_exactly_once()
        {
            _callCount.Should().Be(1);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Configurable_Max_Retry_Attempts : DeadlockRetryPolicyTests
    {
        private int _callCount;

        [SetUp]
        public async Task Setup()
        {
            _callCount = 0;
            var pipeline = BuildPipeline(maxRetryAttempts: 1);

            await pipeline.ExecuteAsync(async _ =>
            {
                _callCount++;
                await Task.CompletedTask;
                return (object)new UpsertResult.UpsertFailureWriteConflict();
            });
        }

        [Test]
        public void It_respects_configured_max()
        {
            _callCount.Should().Be(2);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Update_WriteConflict_Then_Success : DeadlockRetryPolicyTests
    {
        private object? _result;
        private int _callCount;

        [SetUp]
        public async Task Setup()
        {
            _callCount = 0;
            var pipeline = BuildPipeline(maxRetryAttempts: 3);

            _result = await pipeline.ExecuteAsync(async _ =>
            {
                _callCount++;
                await Task.CompletedTask;
                return _callCount < 3
                    ? (object)new UpdateResult.UpdateFailureWriteConflict()
                    : new UpdateResult.UpdateSuccess(new DocumentUuid(Guid.NewGuid()));
            });
        }

        [Test]
        public void It_retries_update_and_returns_success()
        {
            _result.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public void It_called_the_callback_exactly_three_times()
        {
            _callCount.Should().Be(3);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Delete_WriteConflict_Then_Success : DeadlockRetryPolicyTests
    {
        private object? _result;
        private int _callCount;

        [SetUp]
        public async Task Setup()
        {
            _callCount = 0;
            var pipeline = BuildPipeline(maxRetryAttempts: 3);

            _result = await pipeline.ExecuteAsync(async _ =>
            {
                _callCount++;
                await Task.CompletedTask;
                return _callCount < 3
                    ? (object)new DeleteResult.DeleteFailureWriteConflict()
                    : new DeleteResult.DeleteSuccess();
            });
        }

        [Test]
        public void It_retries_delete_and_returns_success()
        {
            _result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        }

        [Test]
        public void It_called_the_callback_exactly_three_times()
        {
            _callCount.Should().Be(3);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Invalid_MaxRetryAttempts : DeadlockRetryPolicyTests
    {
        [Test]
        public void It_throws_on_negative_MaxRetryAttempts()
        {
            var settings = new DeadlockRetrySettings { MaxRetryAttempts = -1 };

            var act = () => DmsCoreServiceExtensions.ValidateDeadlockRetrySettings(settings);

            act.Should().Throw<InvalidOperationException>().WithMessage("*MaxRetryAttempts*");
        }

        [Test]
        public void It_throws_on_zero_BaseDelayMilliseconds()
        {
            var settings = new DeadlockRetrySettings { BaseDelayMilliseconds = 0 };

            var act = () => DmsCoreServiceExtensions.ValidateDeadlockRetrySettings(settings);

            act.Should().Throw<InvalidOperationException>().WithMessage("*BaseDelayMilliseconds*");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Zero_MaxRetryAttempts : DeadlockRetryPolicyTests
    {
        private object? _result;
        private int _callCount;

        [SetUp]
        public async Task Setup()
        {
            _callCount = 0;
            var pipeline = BuildPipeline(maxRetryAttempts: 0);

            _result = await pipeline.ExecuteAsync(async _ =>
            {
                _callCount++;
                await Task.CompletedTask;
                return (object)new UpsertResult.UpsertFailureWriteConflict();
            });
        }

        [Test]
        public void It_executes_once_without_retrying()
        {
            _callCount.Should().Be(1);
        }

        [Test]
        public void It_returns_the_failure_result_directly()
        {
            _result.Should().BeOfType<UpsertResult.UpsertFailureWriteConflict>();
        }
    }

    // --- Handler-level log verification tests ---

    /// <summary>
    /// A simple ILogger that captures log entries for test verification.
    /// </summary>
    private class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

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
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Builds a non-generic ResiliencePipeline (as used by handlers) with retry configured.
    /// </summary>
    private static ResiliencePipeline BuildHandlerPipeline(int maxRetryAttempts)
    {
        lock (_retryPipelineBuildLock)
        {
            var builder = new ResiliencePipelineBuilder();

            if (maxRetryAttempts > 0)
            {
                builder.AddRetry(
                    new RetryStrategyOptions
                    {
                        BackoffType = DelayBackoffType.Exponential,
                        MaxRetryAttempts = maxRetryAttempts,
                        Delay = TimeSpan.FromMilliseconds(1),
                        UseJitter = false,
                        ShouldHandle = new PredicateBuilder().HandleResult(Utility.IsRetryableResult),
                    }
                );
            }

            return builder.Build();
        }
    }

    private static (IPipelineStep handler, IServiceProvider serviceProvider) CreateGetByIdHandler(
        IDocumentStoreRepository repository,
        ILogger logger,
        int maxRetryAttempts = 3
    )
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDocumentStoreRepository))).Returns(repository);

        var handler = new GetByIdHandler(
            logger,
            BuildHandlerPipeline(maxRetryAttempts),
            new NoAuthorizationServiceFactory()
        );

        return (handler, serviceProvider);
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Handler_Retries_Exhausted_Logs_Error : DeadlockRetryPolicyTests
    {
        private class AlwaysRetryableRepository : NotImplementedDocumentStoreRepository
        {
            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                return Task.FromResult<GetResult>(new GetResult.GetFailureRetryable());
            }
        }

        private CapturingLogger _logger = null!;
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new CapturingLogger();
            _requestInfo = No.RequestInfo("test-trace-id");
            var (handler, serviceProvider) = CreateGetByIdHandler(new AlwaysRetryableRepository(), _logger);
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await handler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_logs_error_when_retries_exhausted()
        {
            _logger
                .Entries.Should()
                .Contain(e =>
                    e.Level == LogLevel.Error
                    && e.Message.Contains("All deadlock retry attempts exhausted")
                    && e.Message.Contains("get")
                );
        }

        [Test]
        public void It_includes_attempt_count_in_log()
        {
            _logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("4"));
        }

        [Test]
        public void It_includes_trace_id_in_log()
        {
            _logger
                .Entries.Should()
                .Contain(e => e.Level == LogLevel.Error && e.Message.Contains("test-trace-id"));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Handler_Success_After_Retries_Logs_Warning : DeadlockRetryPolicyTests
    {
        private class RetryThenSuccessRepository : NotImplementedDocumentStoreRepository
        {
            private int _callCount;

            public override Task<GetResult> GetDocumentById(IGetRequest getRequest)
            {
                _callCount++;
                if (_callCount < 3)
                {
                    return Task.FromResult<GetResult>(new GetResult.GetFailureRetryable());
                }
                return Task.FromResult<GetResult>(
                    new GetResult.GetSuccess(
                        No.DocumentUuid,
                        new JsonObject(),
                        DateTime.Now,
                        getRequest.TraceId.Value
                    )
                );
            }
        }

        private CapturingLogger _logger = null!;
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new CapturingLogger();
            _requestInfo = No.RequestInfo("test-trace-id");
            var (handler, serviceProvider) = CreateGetByIdHandler(new RetryThenSuccessRepository(), _logger);
            _requestInfo.ScopedServiceProvider = serviceProvider;
            await handler.Execute(_requestInfo, NullNext);
        }

        [Test]
        public void It_logs_warning_on_success_after_retries()
        {
            _logger
                .Entries.Should()
                .Contain(e =>
                    e.Level == LogLevel.Warning
                    && e.Message.Contains("Deadlock resolved after")
                    && e.Message.Contains("retries for get")
                );
        }

        [Test]
        public void It_includes_retry_count_in_log()
        {
            _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("2"));
        }

        [Test]
        public void It_returns_success_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(200);
        }

        [Test]
        public void It_does_not_log_error()
        {
            _logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_OnRetry_Callback_Logs_Structured_Fields : DeadlockRetryPolicyTests
    {
        private CapturingLogger _retryLogger = null!;

        [SetUp]
        public async Task Setup()
        {
            _retryLogger = new CapturingLogger();
            int maxRetryAttempts = 3;

            // Uses the production OnRetry handler to catch regressions without depending on
            // Polly option validation behavior in the strategy builder.
            var onRetryHandler = Utility.CreateOnRetryHandler(_retryLogger, maxRetryAttempts);
            var context = ResilienceContextPool.Shared.Get();

            try
            {
                var retryOutcome = Outcome.FromResult<object>(new UpsertResult.UpsertFailureWriteConflict());

                await onRetryHandler(
                    new OnRetryArguments<object>(
                        context,
                        retryOutcome,
                        attemptNumber: 1,
                        retryDelay: TimeSpan.FromMilliseconds(1),
                        duration: TimeSpan.Zero
                    )
                );

                await onRetryHandler(
                    new OnRetryArguments<object>(
                        context,
                        retryOutcome,
                        attemptNumber: 2,
                        retryDelay: TimeSpan.FromMilliseconds(1),
                        duration: TimeSpan.Zero
                    )
                );
            }
            finally
            {
                ResilienceContextPool.Shared.Return(context);
            }
        }

        [Test]
        public void It_logs_warning_for_each_retry()
        {
            _retryLogger.Entries.Where(e => e.Level == LogLevel.Warning).Should().HaveCount(2);
        }

        [Test]
        public void It_includes_attempt_number()
        {
            _retryLogger
                .Entries.Should()
                .Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("Deadlock retry attempt"));
        }

        [Test]
        public void It_includes_max_attempts()
        {
            _retryLogger.Entries.Should().Contain(e => e.Message.Contains("/3"));
        }

        [Test]
        public void It_includes_operation_type()
        {
            _retryLogger.Entries.Should().Contain(e => e.Message.Contains("UpsertFailureWriteConflict"));
        }

        [Test]
        public void It_includes_operation_name()
        {
            _retryLogger.Entries.Should().Contain(e => e.Message.Contains("unknown"));
        }
    }
}
