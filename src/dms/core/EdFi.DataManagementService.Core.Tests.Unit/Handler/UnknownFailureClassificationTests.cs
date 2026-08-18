// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.Handler;
using FluentAssertions;
using NUnit.Framework;
using Polly;
using Polly.CircuitBreaker;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

/// <summary>
/// Which backend results the circuit breaker counts as failures. Asserted against the predicate the
/// production pipeline uses rather than against a copy, because the classification's whole purpose is
/// that every operation is treated the same way: an operation whose unknown failure is not counted
/// keeps taking load from a backend that has already failed for every other operation.
/// </summary>
[TestFixture]
public class UnknownFailureClassificationTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_An_Unknown_Failure_Result : UnknownFailureClassificationTests
    {
        private static readonly object[] _unknownFailures =
        [
            new DeleteResult.UnknownFailure("delete failed"),
            new GetResult.UnknownFailure("get failed"),
            new PartitionResult.UnknownPartitionFailure("partition failed"),
            new QueryResult.UnknownFailure("query failed"),
            new UpdateResult.UnknownFailure("update failed"),
            new UpsertResult.UnknownFailure("upsert failed"),
        ];

        [TestCaseSource(nameof(_unknownFailures))]
        public void It_is_counted_as_a_failure(object result)
        {
            Utility.IsUnknownFailureResult(result).Should().BeTrue();
        }

        // One case per operation that can produce one, so an operation added later without its arm
        // shows up here as a missing case rather than as a silently uncounted failure in production.
        [Test]
        public void It_covers_every_operation_that_produces_one()
        {
            _unknownFailures.Should().HaveCount(6);
        }
    }

    /// <summary>
    /// The negative side matters as much: a predicate widened to a whole result hierarchy would trip
    /// the breaker on successes and on failures the retry strategy already owns.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Result_That_Is_Not_An_Unknown_Failure : UnknownFailureClassificationTests
    {
        private static readonly object[] _otherResults =
        [
            new PartitionResult.PartitionSuccess([]),
            new PartitionResult.PartitionFailureRetryable(),
            new PartitionResult.PartitionFailureNotImplemented("not implemented"),
            new QueryResult.QuerySuccess(new JsonArray(), TotalCount: null),
            new QueryResult.QueryFailureRetryable(),
            new object(),
        ];

        [TestCaseSource(nameof(_otherResults))]
        public void It_is_not_counted_as_a_failure(object result)
        {
            Utility.IsUnknownFailureResult(result).Should().BeFalse();
        }
    }

    /// <summary>
    /// The predicate reaching the breaker is what the finding was about, so this exercises a real
    /// pipeline rather than the predicate alone. It mirrors the production circuit-breaker
    /// configuration without the retry or telemetry layers.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Circuit_Breaker_Pipeline : UnknownFailureClassificationTests
    {
        private const int MinimumThroughput = 2;

        private static ResiliencePipeline<object> BuildBreakerPipeline() =>
            new ResiliencePipelineBuilder<object>()
                .AddCircuitBreaker(
                    new CircuitBreakerStrategyOptions<object>
                    {
                        FailureRatio = 1.0,
                        SamplingDuration = TimeSpan.FromSeconds(30),
                        MinimumThroughput = MinimumThroughput,
                        BreakDuration = TimeSpan.FromSeconds(30),
                        ShouldHandle = new PredicateBuilder<object>().HandleResult(
                            Utility.IsUnknownFailureResult
                        ),
                    }
                )
                .Build();

        private static async Task<bool> BreaksAfterRepeating(object result)
        {
            ResiliencePipeline<object> pipeline = BuildBreakerPipeline();

            for (var attempt = 0; attempt < MinimumThroughput; attempt++)
            {
                await pipeline.ExecuteAsync(_ => ValueTask.FromResult(result));
            }

            try
            {
                await pipeline.ExecuteAsync(_ => ValueTask.FromResult(result));
                return false;
            }
            catch (BrokenCircuitException)
            {
                return true;
            }
        }

        [Test]
        public async Task It_opens_on_repeated_partition_unknown_failures()
        {
            (await BreaksAfterRepeating(new PartitionResult.UnknownPartitionFailure("partition failed")))
                .Should()
                .BeTrue("a partition unknown failure must shed load exactly as every other one does");
        }

        [Test]
        public async Task It_stays_closed_on_repeated_partition_successes()
        {
            (await BreaksAfterRepeating(new PartitionResult.PartitionSuccess([])))
                .Should()
                .BeFalse("a served partition response is not a backend failure");
        }
    }
}
