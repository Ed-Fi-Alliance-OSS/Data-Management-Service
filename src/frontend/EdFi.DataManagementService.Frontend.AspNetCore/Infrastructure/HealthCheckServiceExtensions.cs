// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

public class ApplicationHealthCheck(ILogger<ApplicationHealthCheck> logger) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return Task.FromResult(HealthCheckResult.Healthy("Application is up and running"));
        }
        catch (Exception e)
        {
            logger.LogError(e, e.Message);
            return Task.FromResult(HealthCheckResult.Unhealthy(description: e.Message));
        }
    }
}

public class DbHealthCheck(string connectionString, string providerName, ILogger<DbHealthCheck> logger)
    : IHealthCheck
{
    private readonly ILogger<DbHealthCheck> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    private readonly string _providerName = providerName ?? throw new ArgumentNullException(nameof(providerName));

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using var connection = CreateConnection(_providerName, _connectionString);
            _logger.LogInformation("Attempting to open a connection to the database.");

            await connection.OpenAsync(cancellationToken);

            _logger.LogInformation("Database connection established successfully.");

            return HealthCheckResult.Healthy("Database connection is healthy.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database connection is unhealthy.", ex);
        }
    }

    private static DbConnection CreateConnection(string providerName, string connectionString)
    {
        return providerName.ToLower() switch
        {
            "postgresql" => new NpgsqlConnection(connectionString),
            _ => throw new ArgumentException($"Unsupported provider: {providerName}"),
        };
    }
}
