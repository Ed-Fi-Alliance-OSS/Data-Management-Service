// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using Npgsql;
using Polly;
using Polly.Retry;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation
{
    internal static class Resilience
    {
        public static ResiliencePipeline GetPostgresExceptionRetryPipeline()
        {
            return new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<PostgresException>(e => e.IsTransient),
                    BackoffType = DelayBackoffType.Exponential,
                    MaxRetryAttempts = 4,
                    Delay = TimeSpan.FromMilliseconds(500),
                    OnRetry = OnTransientRetry
                })
                .AddRetry(new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<PostgresException>(pe => !pe.IsTransient && pe.SqlState != PostgresErrorCodes.ForeignKeyViolation && pe.SqlState != PostgresErrorCodes.UniqueViolation),
                    BackoffType = DelayBackoffType.Exponential,
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.FromMilliseconds(1000),
                    OnRetry = OnFailuretRetry
                })
                .Build();
        }

        private static ValueTask OnTransientRetry(OnRetryArguments<object> onRetryArguments)
        {
            Debug.WriteLine($"Transient Tetry {onRetryArguments.AttemptNumber} {onRetryArguments.Duration}");
            return ValueTask.CompletedTask;
        }

        private static ValueTask OnFailuretRetry(OnRetryArguments<object> onRetryArguments)
        {
            Debug.WriteLine($"Non-Transient Tetry {onRetryArguments.AttemptNumber} {onRetryArguments.Duration}");
            return ValueTask.CompletedTask;
        }
    }
}
