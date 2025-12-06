// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net.Http.Json;
using System.Text.Json;
using EdFi.InstanceManagement.Tests.E2E.Configuration;
using Microsoft.Extensions.Logging;

namespace EdFi.InstanceManagement.Tests.E2E.Infrastructure;

/// <summary>
/// Manages Debezium connector lifecycle for E2E tests.
/// Creates and deletes PostgreSQL CDC connectors via Kafka Connect REST API.
/// </summary>
public class DebeziumConnectorClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DebeziumConnectorClient> _logger;
    private bool _disposed;

    public DebeziumConnectorClient(ILogger<DebeziumConnectorClient> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(TestConstants.KafkaConnectUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>
    /// Create a Debezium connector for the specified instance.
    /// </summary>
    public async Task CreateConnectorAsync(
        int instanceId,
        string databaseName,
        CancellationToken cancellationToken = default
    )
    {
        var connectorName = GetConnectorName(instanceId);
        _logger.LogInformation(
            "Creating Debezium connector: {ConnectorName} for database {Database}",
            LogSanitizer.Sanitize(connectorName),
            LogSanitizer.Sanitize(databaseName)
        );

        // Delete existing connector if present (cleanup from previous failed run)
        await DeleteConnectorAsync(instanceId, cancellationToken);

        var connectorConfig = BuildConnectorConfig(instanceId, databaseName);

        var response = await _httpClient.PostAsJsonAsync("", connectorConfig, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Failed to create Debezium connector {LogSanitizer.Sanitize(connectorName)}: {response.StatusCode} - {LogSanitizer.Sanitize(error)}"
            );
        }

        _logger.LogInformation(
            "Debezium connector created: {ConnectorName}",
            LogSanitizer.Sanitize(connectorName)
        );

        // Wait for connector to reach RUNNING state
        await WaitForConnectorReadyAsync(instanceId, TimeSpan.FromSeconds(30), cancellationToken);
    }

    /// <summary>
    /// Delete a Debezium connector for the specified instance.
    /// </summary>
    public async Task DeleteConnectorAsync(int instanceId, CancellationToken cancellationToken = default)
    {
        var connectorName = GetConnectorName(instanceId);
        _logger.LogInformation(
            "Deleting Debezium connector: {ConnectorName}",
            LogSanitizer.Sanitize(connectorName)
        );

        try
        {
            var response = await _httpClient.DeleteAsync(connectorName, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Debezium connector deleted: {ConnectorName}",
                    LogSanitizer.Sanitize(connectorName)
                );
                // Give Kafka Connect time to clean up
                await Task.Delay(2000, cancellationToken);
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug(
                    "Debezium connector not found (already deleted): {ConnectorName}",
                    LogSanitizer.Sanitize(connectorName)
                );
            }
            else
            {
                _logger.LogWarning(
                    "Failed to delete Debezium connector {ConnectorName}: {StatusCode}",
                    LogSanitizer.Sanitize(connectorName),
                    response.StatusCode
                );
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "Error deleting Debezium connector {ConnectorName}: {Message}",
                LogSanitizer.Sanitize(connectorName),
                LogSanitizer.Sanitize(ex.Message)
            );
        }
    }

    /// <summary>
    /// Wait for connector to reach RUNNING state
    /// </summary>
    public async Task WaitForConnectorReadyAsync(
        int instanceId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        var connectorName = GetConnectorName(instanceId);
        var deadline = DateTime.UtcNow + timeout;

        _logger.LogInformation(
            "Waiting for Debezium connector to be ready: {ConnectorName}",
            LogSanitizer.Sanitize(connectorName)
        );

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{connectorName}/status", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var status = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    var connectorState = status.GetProperty("connector").GetProperty("state").GetString();

                    if (connectorState == "RUNNING")
                    {
                        _logger.LogInformation(
                            "Debezium connector is RUNNING: {ConnectorName}",
                            LogSanitizer.Sanitize(connectorName)
                        );
                        return;
                    }

                    if (connectorState == "FAILED")
                    {
                        var trace = status.GetProperty("connector").TryGetProperty("trace", out var t)
                            ? t.GetString()
                            : "No trace available";
                        throw new InvalidOperationException(
                            $"Debezium connector {LogSanitizer.Sanitize(connectorName)} FAILED: {LogSanitizer.Sanitize(trace)}"
                        );
                    }

                    _logger.LogDebug(
                        "Connector {ConnectorName} state: {State}",
                        LogSanitizer.Sanitize(connectorName),
                        LogSanitizer.Sanitize(connectorState)
                    );
                }
            }
            catch (HttpRequestException)
            {
                // Connector not ready yet
            }

            await Task.Delay(1000, cancellationToken);
        }

        throw new TimeoutException(
            $"Debezium connector {LogSanitizer.Sanitize(connectorName)} did not reach RUNNING state within {timeout}"
        );
    }

    public static string GetConnectorName(int instanceId) => $"postgresql-source-instance-{instanceId}";

    private static object BuildConnectorConfig(int instanceId, string databaseName)
    {
        return new
        {
            name = GetConnectorName(instanceId),
            config = new Dictionary<string, object>
            {
                ["connector.class"] = "io.debezium.connector.postgresql.PostgresConnector",
                ["plugin.name"] = "pgoutput",
                ["database.hostname"] = "dms-postgresql",
                ["database.port"] = "5432",
                ["database.user"] = "postgres",
                ["database.password"] = TestConstants.PostgresPassword,
                ["database.dbname"] = databaseName,
                ["publication.name"] = $"to_debezium_instance_{instanceId}",
                ["slot.name"] = $"debezium_instance_{instanceId}",
                ["publication.autocreate.mode"] = "filtered",
                ["snapshot.mode"] = "initial",
                ["snapshot.locking.mode"] = "none",
                ["snapshot.include.collection.list"] =
                    "dms.document,dms.educationorganizationhierarchytermslookup",
                ["schema.include.list"] = "dms",
                ["schema.history.internal.kafka.bootstrap.servers"] = "kafka:9092",
                ["schema.history.internal.kafka.topic"] = $"schema-changes.dms.instance.{instanceId}",
                ["topic.prefix"] = $"edfi.dms.{instanceId}",
                ["table.include.list"] =
                    "dms.document,dms.document_00,dms.document_01,dms.document_02,dms.document_03,dms.document_04,dms.document_05,dms.document_06,dms.document_07,dms.document_08,dms.document_09,dms.document_10,dms.document_11,dms.document_12,dms.document_13,dms.document_14,dms.document_15,dms.educationorganizationhierarchytermslookup",
                ["value.converter"] = "org.apache.kafka.connect.json.JsonConverter",
                ["value.converter.schemas.enable"] = "false",
                ["key.converter"] = "org.apache.kafka.connect.json.JsonConverter",
                ["key.converter.schemas.enable"] = "false",
                ["transforms"] =
                    "unwrap, extractId, extractPlainId, expandDocumentJson, expandHierarchyJson, routeToCorrectTopic, stripPartitionSuffix",
                ["predicates"] = "isDocumentTable, isHierarchyTable",
                ["predicates.isDocumentTable.type"] =
                    "org.apache.kafka.connect.transforms.predicates.TopicNameMatches",
                ["predicates.isDocumentTable.pattern"] = $@"edfi\.dms\.{instanceId}\.document",
                ["predicates.isHierarchyTable.type"] =
                    "org.apache.kafka.connect.transforms.predicates.TopicNameMatches",
                ["predicates.isHierarchyTable.pattern"] =
                    $@"edfi\.dms\.{instanceId}\.educationorganizationhierarchytermslookup",
                ["transforms.unwrap.type"] = "io.debezium.transforms.ExtractNewRecordState",
                ["transforms.unwrap.delete.tombstone.handling.mode"] = "rewrite",
                ["transforms.unwrap.add.fields"] = "documentuuid",
                ["transforms.extractId.type"] = "org.apache.kafka.connect.transforms.ValueToKey",
                ["transforms.extractId.fields"] = "id",
                ["transforms.extractPlainId.type"] = "org.apache.kafka.connect.transforms.ExtractField$Key",
                ["transforms.extractPlainId.field"] = "id",
                ["transforms.expandDocumentJson.type"] = "com.redhat.insights.expandjsonsmt.ExpandJSON$Value",
                ["transforms.expandDocumentJson.sourceFields"] =
                    "edfidoc, securityelements, studentschoolauthorizationedorgids, contactstudentschoolauthorizationedorgids, staffeducationorganizationauthorizationedorgids",
                ["transforms.expandDocumentJson.predicate"] = "isDocumentTable",
                ["transforms.expandHierarchyJson.type"] =
                    "com.redhat.insights.expandjsonsmt.ExpandJSON$Value",
                ["transforms.expandHierarchyJson.sourceFields"] = "hierarchy",
                ["transforms.expandHierarchyJson.predicate"] = "isHierarchyTable",
                ["transforms.routeToCorrectTopic.type"] = "org.apache.kafka.connect.transforms.RegexRouter",
                ["transforms.routeToCorrectTopic.regex"] = @"edfi\.dms\.([0-9]+)\.dms\.(.*)",
                ["transforms.routeToCorrectTopic.replacement"] = "edfi.dms.$1.$2",
                ["transforms.stripPartitionSuffix.type"] = "org.apache.kafka.connect.transforms.RegexRouter",
                ["transforms.stripPartitionSuffix.regex"] =
                    @"edfi\.dms\.([0-9]+)\.(document|educationorganizationhierarchytermslookup)_\d+",
                ["transforms.stripPartitionSuffix.replacement"] = "edfi.dms.$1.$2",
            },
        };
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _httpClient.Dispose();
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
