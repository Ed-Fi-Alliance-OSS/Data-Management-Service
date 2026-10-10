// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Data.SqlClient;
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

/// <summary>
/// Checks that a loaded data store's database accepts connections.
/// </summary>
/// <remarks>
/// The connection string is resolved on every check rather than once at construction. This check is
/// a singleton, and with multi-tenancy the data stores load on demand, so a pod that starts with no
/// tenants would otherwise keep the empty value it saw at its first probe for its whole lifetime.
/// With no data store to probe the result is <see cref="HealthStatus.Degraded"/>, not Healthy: the
/// state is expected at multi-tenant startup, but a cache refresh can also replace a working
/// tenant's data stores with an empty list, and that must stay visible in the report. Degraded maps
/// to HTTP 200, so it does not fail probes.
/// </remarks>
public class DbHealthCheck(
    IConnectionStringProvider connectionStringProvider,
    string providerName,
    ILogger<DbHealthCheck> logger
) : IHealthCheck
{
    internal const string NoDataStoresDescription =
        "No data stores are loaded yet, so there is no database to check.";

    private readonly IConnectionStringProvider _connectionStringProvider =
        connectionStringProvider ?? throw new ArgumentNullException(nameof(connectionStringProvider));
    private readonly ILogger<DbHealthCheck> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    // Validated here rather than on first connection: with no data store loaded the check never
    // reaches CreateConnection, so an unsupported value would otherwise stay hidden until one loads.
    private readonly string _providerName = ValidateProviderName(providerName);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        string? connectionString = _connectionStringProvider.GetHealthCheckConnectionString();

        // Multi-tenant startup with zero tenants is a supported state, not a database fault:
        // data stores load when tenants are created and requests arrive.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return HealthCheckResult.Degraded(NoDataStoresDescription);
        }

        try
        {
            await using var connection = CreateConnection(_providerName, connectionString);
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

    private static string ValidateProviderName(string providerName)
    {
        ArgumentNullException.ThrowIfNull(providerName);
        if (
            !providerName.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
            && !providerName.Equals("mssql", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new ArgumentException(
                $"Unsupported provider: '{providerName}'. Expected 'postgresql' or 'mssql'.",
                nameof(providerName)
            );
        }
        return providerName;
    }

    private static DbConnection CreateConnection(string providerName, string connectionString)
    {
        return providerName.ToLowerInvariant() switch
        {
            "postgresql" => new NpgsqlConnection(connectionString),
            "mssql" => new SqlConnection(connectionString),
            _ => throw new ArgumentException($"Unsupported provider: {providerName}"),
        };
    }
}
