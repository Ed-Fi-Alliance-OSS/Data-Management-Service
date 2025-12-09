// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EdFi.InstanceManagement.Tests.E2E.Infrastructure;

/// <summary>
/// Cleans up PostgreSQL replication slots and publications left by Debezium connectors.
/// This is critical to prevent conflicts when creating new connectors.
/// </summary>
public class PostgresReplicationCleanup(ILogger<PostgresReplicationCleanup> logger)
{
    /// <summary>
    /// Clean up replication slot and publication for an instance.
    /// Must be called AFTER the Debezium connector is deleted but BEFORE creating a new one.
    /// </summary>
    public async Task CleanupReplicationResourcesAsync(
        int instanceId,
        string databaseName,
        CancellationToken cancellationToken = default
    )
    {
        var slotName = $"debezium_instance_{instanceId}";
        var publicationName = $"to_debezium_instance_{instanceId}";

        logger.LogInformation(
            "Cleaning up PostgreSQL replication resources for instance {InstanceId}",
            instanceId
        );

        // Connect to the specific database (not postgres) to clean up
        var connectionString =
            $"Host={TestConstants.PostgresHost};Port={TestConstants.PostgresPort};"
            + $"Username={TestConstants.PostgresUser};Password={TestConstants.PostgresPassword};"
            + $"Database={databaseName}";

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Drop replication slot if exists
        await DropReplicationSlotAsync(connection, slotName, cancellationToken);

        // Drop publication if exists
        await DropPublicationAsync(connection, publicationName, cancellationToken);

        logger.LogInformation(
            "PostgreSQL replication cleanup completed for instance {InstanceId}",
            instanceId
        );
    }

    private async Task DropReplicationSlotAsync(
        NpgsqlConnection connection,
        string slotName,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // Check if slot exists
            await using var checkCmd = new NpgsqlCommand(
                "SELECT slot_name FROM pg_replication_slots WHERE slot_name = @slotName",
                connection
            );
            checkCmd.Parameters.AddWithValue("slotName", slotName);

            var exists = await checkCmd.ExecuteScalarAsync(cancellationToken);
            if (exists != null)
            {
                // Drop the slot
                await using var dropCmd = new NpgsqlCommand(
                    $"SELECT pg_drop_replication_slot('{slotName}')",
                    connection
                );
                await dropCmd.ExecuteNonQueryAsync(cancellationToken);
                logger.LogInformation(
                    "Dropped replication slot: {SlotName}",
                    LogSanitizer.Sanitize(slotName)
                );
            }
            else
            {
                logger.LogDebug(
                    "Replication slot does not exist: {SlotName}",
                    LogSanitizer.Sanitize(slotName)
                );
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "55006") // Object in use
        {
            logger.LogWarning(
                "Replication slot {SlotName} is still active, waiting for connector to stop",
                LogSanitizer.Sanitize(slotName)
            );
            // Wait and retry
            await Task.Delay(2000, cancellationToken);
            await DropReplicationSlotAsync(connection, slotName, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Failed to drop replication slot {SlotName}: {Message}",
                LogSanitizer.Sanitize(slotName),
                LogSanitizer.Sanitize(ex.Message)
            );
        }
    }

    private async Task DropPublicationAsync(
        NpgsqlConnection connection,
        string publicationName,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var cmd = new NpgsqlCommand(
                $"DROP PUBLICATION IF EXISTS {publicationName}",
                connection
            );
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            logger.LogDebug("Dropped publication: {PublicationName}", LogSanitizer.Sanitize(publicationName));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Failed to drop publication {PublicationName}: {Message}",
                LogSanitizer.Sanitize(publicationName),
                LogSanitizer.Sanitize(ex.Message)
            );
        }
    }
}
