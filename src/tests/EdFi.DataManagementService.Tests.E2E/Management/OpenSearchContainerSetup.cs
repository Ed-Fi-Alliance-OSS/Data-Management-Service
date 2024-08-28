// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenSearch.Client;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public class OpenSearchContainerSetup
{
    public static IContainer? DbContainer;
    public static IContainer? ApiContainer;
    public static IContainer? ZooKeeperContainer;
    public static IContainer? KafkaContainer;
    public static IContainer? KafkaSourceContainer;
    public static IContainer? KafkaSinkContainer;
    public static IContainer? OpenSearchContainer;
    public static INetwork? network;

    public static async Task CreateContainers()
    {
        network = new NetworkBuilder().Build();

        // Images need to be previously built
        string apiImageName = "local/edfi-data-management-service";
        string dbImageName = "postgres:16.3-alpine3.20";

        var pgAdminUser = "postgres";
        var pgAdminPassword = "abcdefgh1!";
        var dbContainerName = "dms-postgresql";
        var assemblyLocation = "";

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace).AddDebug().AddConsole();
        });

        try
        {
            ZooKeeperContainer = new ContainerBuilder()
                .WithHostname("dms-zookeeper1")
                .WithImage("debezium/zookeeper:2.7.0.Final")
                .WithPortBinding(2181, 2181)
                .WithPortBinding(2888, 2888)
                .WithPortBinding(3888, 3888)
                .WithNetwork(network)
                .WithNetworkAliases("dms-zookeeper1")
                .Build();

            KafkaContainer = new ContainerBuilder()
                .WithHostname("dms-kafka1")
                .WithImage("debezium/kafka:2.7.0.Final")
                .WithPortBinding(9092, 9092)
                .DependsOn(ZooKeeperContainer)
                .WithNetwork(network)
                .WithNetworkAliases("dms-kafka1")
                .WithEnvironment("ZOOKEEPER_CONNECT", "dms-zookeeper1:2181")
                .WithEnvironment("KAFKA_ADVERTISED_LISTENERS", "PLAINTEXT://dms-kafka1:9092")
                .Build();

            KafkaSourceContainer = new ContainerBuilder()
                .WithHostname("kafka-postgresql-source")
                .WithImage("edfialliance/ed-fi-kafka-connect:pre")
                .WithPortBinding(8083, 8083)
                .DependsOn(KafkaContainer)
                .DependsOn(ZooKeeperContainer)
                .WithNetwork(network)
                .WithNetworkAliases("kafka-postgresql-source")
                .WithEnvironment("BOOTSTRAP_SERVERS", "dms-kafka1:9092")
                .WithEnvironment("GROUP_ID", "1")
                .WithEnvironment("CONFIG_STORAGE_TOPIC", "debezium_source_config")
                .WithEnvironment("OFFSET_STORAGE_TOPIC", "debezium_source_offset")
                .WithEnvironment("STATUS_STORAGE_TOPIC", "debezium_source_status")
                .Build();

            KafkaSinkContainer = new ContainerBuilder()
                .WithHostname("kafka-opensearch-sink")
                .WithImage("edfialliance/ed-fi-kafka-connect:pre")
                .WithPortBinding(8084, 8083)
                .DependsOn(KafkaContainer)
                .DependsOn(ZooKeeperContainer)
                .WithNetwork(network)
                .WithNetworkAliases("kafka-opensearch-sink")
                .WithEnvironment("BOOTSTRAP_SERVERS", "dms-kafka1:9092")
                .WithEnvironment("GROUP_ID", "2")
                .WithEnvironment("CONFIG_STORAGE_TOPIC", "debezium_sink_config")
                .WithEnvironment("OFFSET_STORAGE_TOPIC", "debezium_sink_offset")
                .WithEnvironment("STATUS_STORAGE_TOPIC", "debezium_sink_status")
                .Build();

            assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string filePath = Path.Combine(assemblyLocation!, "OpenSearchFiles/debezium_config.sh");

            DbContainer = new ContainerBuilder()
                .WithHostname(dbContainerName)
                .WithImage(dbImageName)
                .WithPortBinding(5435, 5432)
                .WithNetwork(network)
                .WithNetworkAliases(dbContainerName)
                .WithEnvironment("POSTGRES_USER", pgAdminUser)
                .WithEnvironment("POSTGRES_PASSWORD", pgAdminPassword)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
                .WithBindMount(filePath, "/docker-entrypoint-initdb.d/debezium_config.sh")
                .WithLogger(loggerFactory.CreateLogger("dbContainer"))
                .Build();

            OpenSearchContainer = new ContainerBuilder()
                .WithHostname("dms-opensearch")
                .WithImage("opensearchproject/opensearch:2.16.0")
                .WithNetwork(network)
                .WithPortBinding(9200, 9200)
                .WithPortBinding(9600, 9600)
                .WithNetworkAliases("dms-opensearch")
                .WithEnvironment("OPENSEARCH_INITIAL_ADMIN_PASSWORD", "abcdefgh1!")
                .WithEnvironment("OPENSEARCH_ADMIN_PASSWORD", "abcdefgh1!")
                .WithEnvironment("cluster.name", "opensearch-cluster")
                .WithEnvironment("bootstrap.memory_lock", "true")
                .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
                .WithEnvironment("discovery.type", "single-node")
                .WithEnvironment("DISABLE_INSTALL_DEMO_CONFIG", "true")
                .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
                .Build();

            ApiContainer = new ContainerBuilder()
                .WithImage(apiImageName)
                .WithPortBinding(8080)
                .WithEnvironment("NEED_DATABASE_SETUP", "true")
                .WithEnvironment(
                    "DATABASE_CONNECTION_STRING",
                    "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;"
                )
                .DependsOn(DbContainer)
                .WithEnvironment("POSTGRES_ADMIN_USER", pgAdminUser)
                .WithEnvironment("POSTGRES_ADMIN_PASSWORD", pgAdminPassword)
                .WithEnvironment("POSTGRES_PORT", "5432")
                .WithEnvironment("POSTGRES_HOST", dbContainerName)
                .WithEnvironment("LOG_LEVEL", "Debug")
                .WithEnvironment("OAUTH_TOKEN_ENDPOINT", "http://127.0.0.1:8080/oauth/token")
                .WithEnvironment("BYPASS_STRING_COERCION", "false")
                .WithEnvironment("CORRELATION_ID_HEADER", "correlationid")
                .WithEnvironment("DATABASE_ISOLATION_LEVEL", "RepeatableRead")
                .WithEnvironment("FAILURE_RATIO", "0.1")
                .WithEnvironment("SAMPLING_DURATION_SECONDS", "10")
                .WithEnvironment("MINIMUM_THROUGHPUT", "2000")
                .WithEnvironment("BREAK_DURATION_SECONDS", "30")
                .WithEnvironment("QUERY_HANDLER", "opensearch")
                .WithEnvironment("OPENSEARCH_URL", @"http://dms-opensearch:9200")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080)))
                .WithNetwork(network)
                .WithLogger(loggerFactory.CreateLogger("apiContainer"))
                .Build();

            await network.CreateAsync().ConfigureAwait(false);
            await Task.WhenAll(
                    ZooKeeperContainer.StartAsync(),
                    KafkaContainer.StartAsync(),
                    KafkaSourceContainer.StartAsync(),
                    KafkaSinkContainer.StartAsync(),
                    DbContainer.StartAsync(),
                    OpenSearchContainer.StartAsync(),
                    ApiContainer.StartAsync()
                )
                .ConfigureAwait(false);

            while (OpenSearchContainer.State != TestcontainersStates.Running)
            {
                await Task.Delay(1000);
            }

            await Task.Delay(5000);

            var sourceConfigPath = "OpenSearchFiles/postgresql_connector.json";
            await InjectConnectorConfiguration("http://localhost:8083/", sourceConfigPath);

            var sinkConfigPath = "OpenSearchFiles/opensearch_connector.json";
            await InjectConnectorConfiguration("http://localhost:8084/", sinkConfigPath);
        }
        catch (Exception ex)
        {
            var a = ex.Message;
        }

        await Task.Delay(5000);

        async Task InjectConnectorConfiguration(string url, string configFilePath)
        {
            var _client = new HttpClient();
            _client.BaseAddress = new Uri(url);

            var connectorConfig = Path.Combine(assemblyLocation!, configFilePath);
            string content = await File.ReadAllTextAsync(connectorConfig);
            var stringContent = new StringContent(content, Encoding.UTF8, "application/json");
            var response = await _client.PostAsync("connectors", stringContent);
        }
    }

    public static async Task<string> StartContainers(PlaywrightContext context, TestLogger logger)
    {
        while (ApiContainer!.State != TestcontainersStates.Running)
        {
            await Task.Delay(1000);
        }
        return new UriBuilder(
            Uri.UriSchemeHttp,
            ApiContainer?.Hostname,
            ApiContainer!.GetMappedPublicPort(8080)
        ).ToString();
    }

    public static async Task ResetContainers(PlaywrightContext context, TestLogger logger)
    {
        var logs = await ApiContainer!.GetLogsAsync();
        logger.log.Information($"{Environment.NewLine}API stdout logs:{Environment.NewLine}{logs.Stdout}");

        if (!string.IsNullOrEmpty(logs.Stderr))
        {
            logger.log.Error($"{Environment.NewLine}API stderr logs:{Environment.NewLine}{logs.Stderr}");
        }

        OpenSearchClient openSearchClient = new();
        var indices = openSearchClient.Cat.Indices();

        foreach (var index in indices.Records.Where(x => x.Index.Contains("ed-fi")))
        {
            await openSearchClient.Indices.DeleteAsync(index.Index);
        }

        var connString =
            "host=localhost;port=5435;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice;";
        using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        var deleteRefCmd = new NpgsqlCommand($"DELETE FROM dms.Reference;", conn);
        await deleteRefCmd.ExecuteNonQueryAsync();

        var deleteAliCmd = new NpgsqlCommand($"DELETE FROM dms.Alias;", conn);
        await deleteAliCmd.ExecuteNonQueryAsync();

        var deleteDocCmd = new NpgsqlCommand($"DELETE FROM dms.Document;", conn);
        await deleteDocCmd.ExecuteNonQueryAsync();
    }
}
