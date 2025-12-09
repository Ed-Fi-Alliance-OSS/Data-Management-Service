// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.Extensions.Logging;

namespace EdFi.InstanceManagement.Tests.E2E.Infrastructure;

/// <summary>
/// Orchestrates the complete lifecycle of DMS instance infrastructure including:
/// - Creating Kafka topics
/// - Creating Debezium connectors
/// - Cleanup of all resources
/// </summary>
public class InstanceInfrastructureManager : IDisposable
{
    private readonly KafkaAdminClient _kafkaAdmin;
    private readonly DebeziumConnectorClient _debeziumClient;
    private readonly PostgresReplicationCleanup _postgresCleanup;
    private readonly ILogger<InstanceInfrastructureManager> _logger;
    private readonly List<(int InstanceId, string DatabaseName)> _createdInstances = [];
    private bool _disposed;

    public InstanceInfrastructureManager(
        KafkaAdminClient kafkaAdmin,
        DebeziumConnectorClient debeziumClient,
        PostgresReplicationCleanup postgresCleanup,
        ILogger<InstanceInfrastructureManager> logger
    )
    {
        _kafkaAdmin = kafkaAdmin;
        _debeziumClient = debeziumClient;
        _postgresCleanup = postgresCleanup;
        _logger = logger;
    }

    /// <summary>
    /// Create complete infrastructure for an instance:
    /// 1. Create Kafka topic
    /// 2. Clean up any stale replication resources
    /// 3. Create Debezium connector
    /// 4. Wait for connector to be ready
    /// </summary>
    public async Task SetupInstanceInfrastructureAsync(
        int instanceId,
        string databaseName,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation(
            "Setting up infrastructure for instance {InstanceId} with database {Database}",
            instanceId,
            LogSanitizer.Sanitize(databaseName)
        );

        try
        {
            // 1. Create Kafka topic first
            await _kafkaAdmin.CreateTopicAsync(instanceId, cancellationToken);

            // 2. Clean up any stale PostgreSQL replication resources
            await _postgresCleanup.CleanupReplicationResourcesAsync(
                instanceId,
                databaseName,
                cancellationToken
            );

            // 3. Create Debezium connector
            await _debeziumClient.CreateConnectorAsync(instanceId, databaseName, cancellationToken);

            // 4. Wait for topic to be fully ready
            await _kafkaAdmin.WaitForTopicReadyAsync(instanceId, TimeSpan.FromSeconds(10), cancellationToken);

            // Track for cleanup
            _createdInstances.Add((instanceId, databaseName));

            _logger.LogInformation("Infrastructure setup complete for instance {InstanceId}", instanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to setup infrastructure for instance {InstanceId}", instanceId);
            throw;
        }
    }

    /// <summary>
    /// Tear down infrastructure for an instance:
    /// 1. Delete Debezium connector
    /// 2. Delete Kafka topic
    /// 3. Clean up PostgreSQL replication resources
    /// </summary>
    public async Task TeardownInstanceInfrastructureAsync(
        int instanceId,
        string databaseName,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Tearing down infrastructure for instance {InstanceId}", instanceId);

        // Order matters: connector first, then topic, then postgres cleanup

        // 1. Delete Debezium connector
        await _debeziumClient.DeleteConnectorAsync(instanceId, cancellationToken);

        // 2. Give connector time to release replication slot
        await Task.Delay(2000, cancellationToken);

        // 3. Clean up PostgreSQL replication resources
        await _postgresCleanup.CleanupReplicationResourcesAsync(instanceId, databaseName, cancellationToken);

        // 4. Delete Kafka topic
        await _kafkaAdmin.DeleteTopicAsync(instanceId, cancellationToken);

        // Remove from tracking
        _createdInstances.RemoveAll(i => i.InstanceId == instanceId);

        _logger.LogInformation("Infrastructure teardown complete for instance {InstanceId}", instanceId);
    }

    /// <summary>
    /// Clean up all instances created during this session.
    /// Called by cleanup hooks.
    /// </summary>
    public async Task TeardownAllAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Tearing down all {Count} instance infrastructure(s)",
            _createdInstances.Count
        );

        // Copy list to avoid modification during iteration
        var instances = _createdInstances.ToList();

        foreach (var (instanceId, databaseName) in instances)
        {
            try
            {
                await TeardownInstanceInfrastructureAsync(instanceId, databaseName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to teardown infrastructure for instance {InstanceId}",
                    instanceId
                );
            }
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _debeziumClient.Dispose();
            }
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
