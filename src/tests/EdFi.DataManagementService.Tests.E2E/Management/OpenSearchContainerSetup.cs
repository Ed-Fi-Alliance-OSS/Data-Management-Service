// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace EdFi.DataManagementService.Tests.E2E.Management;

public class OpenSearchContainerSetup : ContainerSetupBase
{
    public static IContainer? DbContainer;
    public static IContainer? DmsApiContainer;
    public static IContainer? ZooKeeperContainer;
    public static IContainer? KafkaContainer;
    public static IContainer? KafkaSourceContainer;
    public static IContainer? KafkaSinkContainer;
    public static IContainer? OpenSearchContainer;
    public static INetwork? network;

    public override async Task StartContainers()
    {
        network = new NetworkBuilder().Build();

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

            DbContainer = DatabaseContainer(loggerFactory, network);

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

            DmsApiContainer = ApiContainer(
                "opensearch",
                loggerFactory,
                network,
                "http://dms-opensearch:9200"
            );

            await network.CreateAsync().ConfigureAwait(false);
            await Task.WhenAll(
                    ZooKeeperContainer.StartAsync(),
                    KafkaContainer.StartAsync(),
                    KafkaSourceContainer.StartAsync(),
                    KafkaSinkContainer.StartAsync(),
                    DbContainer.StartAsync(),
                    OpenSearchContainer.StartAsync(),
                    DmsApiContainer.StartAsync()
                )
                .ConfigureAwait(false);

            //while (OpenSearchContainer.State != TestcontainersStates.Running)
            //{
            //    await Task.Delay(1000);
            //}

            await Task.Delay(7000);

            var sourceConfigPath = "OpenSearchFiles/postgresql_connector.json";
            await InjectConnectorConfiguration(
                "http://localhost:8083/",
                sourceConfigPath,
                "postgresql-source"
            );

            var sinkConfigPath = "OpenSearchFiles/opensearch_connector.json";
            await InjectConnectorConfiguration("http://localhost:8084/", sinkConfigPath, "opensearch-sink");

            await Task.Delay(5000);
        }
        catch (Exception ex)
        {
            var logger = loggerFactory.CreateLogger("OpenSearchContainers");
            logger.LogError(ex.Message);
        }

        async Task InjectConnectorConfiguration(string url, string configFilePath, string connectorName)
        {
            //while (!await IsKafkaRunning(url))
            //{
            //    await Task.Delay(1000);
            //}

            var assemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var _client = new HttpClient();
            _client.BaseAddress = new Uri(url);
            var connectorConfig = Path.Combine(assemblyLocation!, configFilePath);
            string content = await File.ReadAllTextAsync(connectorConfig);
            var stringContent = new StringContent(content, Encoding.UTF8, "application/json");
            var response = await _client.PostAsync("connectors", stringContent);

            //while (!await IsKafkaRunning($"{url}connectors/{connectorName}"))
            //{
            //    await Task.Delay(1000);
            //}
        }

        //async Task<bool> IsKafkaRunning(string url)
        //{
        //    var _client = new HttpClient();
        //    var response = await _client.GetAsync(url);
        //    return ((int)response.StatusCode >= 200) && ((int)response.StatusCode <= 299);
        //}
    }

    public override async Task ApiLogs(TestLogger logger)
    {
        var logs = await DmsApiContainer!.GetLogsAsync();
        logger.log.Information($"{Environment.NewLine}API stdout logs:{Environment.NewLine}{logs.Stdout}");

        if (!string.IsNullOrEmpty(logs.Stderr))
        {
            logger.log.Error($"{Environment.NewLine}API stderr logs:{Environment.NewLine}{logs.Stderr}");
        }
    }

    public override async Task<string> ApiUrl()
    {
        return await ValidateApiContainer(DmsApiContainer!);
    }

    public override async Task ResetData()
    {
        await ResetOpenSearch();
        await ResetDatabase();
    }

    private async Task ResetOpenSearch()
    {
        OpenSearchClient openSearchClient = new();
        var indices = openSearchClient.Cat.Indices();

        foreach (var index in indices.Records.Where(x => x.Index.Contains("ed-fi")))
        {
            await openSearchClient.Indices.DeleteAsync(index.Index);
        }
    }
}
