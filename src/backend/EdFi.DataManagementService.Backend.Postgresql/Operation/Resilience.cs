// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using Npgsql;
using Polly;
using Polly.Retry;
#pragma warning disable S125

namespace EdFi.DataManagementService.Backend.Postgresql.Operation
{
    internal static class Resilience
    {
        public static ResiliencePipeline GetTransientRetryPipeline()
        {
            return new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    ShouldHandle = new PredicateBuilder().Handle<PostgresException>(e => e.IsTransient),
                    BackoffType = DelayBackoffType.Exponential,
                    MaxRetryAttempts = 4,
                    Delay = TimeSpan.FromMilliseconds(500),
                    OnRetry = OnRetry
                })
                .Build();
        }

        private static ValueTask OnRetry(OnRetryArguments<object> onRetryArguments)
        {
            Debug.WriteLine("You are here");
            return ValueTask.CompletedTask;
        }
    }
}
