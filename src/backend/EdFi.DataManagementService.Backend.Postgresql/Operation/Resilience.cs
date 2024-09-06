// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Npgsql;
using Polly;
using Polly.Retry;

namespace EdFi.DataManagementService.Backend.Postgresql.Operation;

internal static class Resilience
{
    private static readonly Lazy<ResiliencePipeline> _postgresExceptionRetryPipeline =
        new(() =>
        {
            return new ResiliencePipelineBuilder()
                .AddRetry(
                    new RetryStrategyOptions
                    {
                        ShouldHandle = new PredicateBuilder().Handle<PostgresException>(e => e.IsTransient),
                        BackoffType = DelayBackoffType.Exponential,
                        MaxRetryAttempts = 4,
                        Delay = TimeSpan.FromMilliseconds(500),
                    }
                )
                .AddRetry(
                    new RetryStrategyOptions
                    {
                        ShouldHandle = new PredicateBuilder().Handle<PostgresException>(pe =>
                            !pe.IsTransient
                            && pe.SqlState != PostgresErrorCodes.ForeignKeyViolation
                            && pe.SqlState != PostgresErrorCodes.UniqueViolation
                        ),
                        BackoffType = DelayBackoffType.Exponential,
                        MaxRetryAttempts = 2,
                        Delay = TimeSpan.FromMilliseconds(1000),
                    }
                )
                .Build();
        });

    public static ResiliencePipeline GetPostgresExceptionRetryPipeline()
    {
        return _postgresExceptionRetryPipeline.Value;
    }
}
