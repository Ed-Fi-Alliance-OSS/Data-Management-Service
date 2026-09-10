// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcProviderAdmissionFixture;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, Category = "MssqlIntegration")]
[Category(CdcControllerCategories.ManagedLifecycle)]
[Category("DatabaseIntegration")]
[Category("CdcAuthorizationDisabledLocal")]
[NonParallelizable]
public sealed class Given_Cdc_Retirement_Offset_Store(CdcProvider provider)
{
    [Test]
    public async Task It_retires_after_provider_creation_before_registration_without_changing_shared_storage()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(8));
        var token = timeout.Token;
        await using var fixture = await CdcProviderAdmissionFixture.StartAsync(provider, token);
        Observed(await fixture.Controllers.Activation.ActivateAsync(fixture.Request, fixture.Runtime, token));
        Observed(await fixture.Controllers.ProviderSetup.SetupAsync(fixture.Request, fixture.Runtime, token));
        var config = Client(fixture);
        var request = fixture.Request;
        string topic = request.WorkerPolicy.OffsetStorageTopic.Value;
        await CdcRetirementOffsetStoreProbe.WriteAsync(
            config,
            topic,
            "[\"unrelated-connector\",{}]",
            "{}",
            token
        );
        var before = await CdcRetirementOffsetStoreProbe.ReadAsync(config, topic, token);
        before.Should().ContainSingle();
        (await fixture.Infrastructure.Connect.ReadConfigurationAsync(request, token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Absent);
        (await fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(request, token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        var result = await Retirement(fixture).RetireAsync(request, request.Binding.Generation, true, token);
        result.Succeeded.Should().BeTrue(JsonSerializer.Serialize(result.Diagnostics));
        (await CdcRetirementOffsetStoreProbe.ReadAsync(config, topic, token)).Should().BeEquivalentTo(before);
        (await fixture.Infrastructure.Bindings.ExactMatchBindingAsync(request.Binding, token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.BindingMissing);
        (await fixture.Infrastructure.Connect.ReadConfigurationAsync(request, token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Absent);
    }

    [Test]
    public async Task It_rejects_actual_pinned_worker_offsets_until_their_exact_keys_are_tombstoned()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(8));
        var token = timeout.Token;
        await using var fixture = await CdcProviderAdmissionFixture.StartAsync(provider, token);
        await fixture.RegisterAsync(token);
        var request = fixture.Request;
        Observed(await fixture.Infrastructure.Connect.StopAsync(request, token));
        (await fixture.Infrastructure.Connect.DeleteAsync(request, token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Absent);
        var config = Client(fixture);
        string topic = request.WorkerPolicy.OffsetStorageTopic.Value;
        var actual = await CdcRetirementOffsetStoreProbe.ReadAsync(config, topic, token);
        var keys = actual
            .Where(record => record.Value is not null)
            .Select(record => record.Key)
            .Distinct()
            .ToArray();
        keys.Should().NotBeEmpty();
        foreach (string key in keys)
        {
            using var parsed = JsonDocument.Parse(key);
            (
                parsed.RootElement.ValueKind == JsonValueKind.Array
                && parsed.RootElement.GetArrayLength() == 2
                && parsed.RootElement[0].GetString() == request.Binding.ConnectorName
                && parsed.RootElement[1].ValueKind == JsonValueKind.Object
            )
                .Should()
                .BeTrue("the qualified worker must emit the supported namespace key format");
        }
        var retirement = Retirement(fixture);
        var rejected = await retirement.RetireAsync(request, request.Binding.Generation, true, token);
        rejected.Succeeded.Should().BeFalse();
        (await fixture.Infrastructure.Bindings.ExactMatchBindingAsync(request.Binding, token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
        (await CdcRetirementOffsetStoreProbe.ReadAsync(config, topic, token)).Should().BeEquivalentTo(actual);
        foreach (string key in keys)
        {
            await CdcRetirementOffsetStoreProbe.WriteAsync(config, topic, key, null!, token);
        }
        var tombstoned = await CdcRetirementOffsetStoreProbe.ReadAsync(config, topic, token);
        var result = await retirement.RetireAsync(request, request.Binding.Generation, true, token);
        result.Succeeded.Should().BeTrue(JsonSerializer.Serialize(result.Diagnostics));
        (await CdcRetirementOffsetStoreProbe.ReadAsync(config, topic, token))
            .Should()
            .BeEquivalentTo(tombstoned);
    }

    private CdcBindingRetirement Retirement(CdcProviderAdmissionFixture fixture) =>
        fixture.Controllers.Retirement(
            fixture.Kafka,
            new CdcProviderArtifactCleanupAdapter(
                provider == CdcProvider.Postgresql ? NpgsqlFactory.Instance : SqlClientFactory.Instance,
                fixture.ConnectionString
            )
        );

    private static ClientConfig Client(CdcProviderAdmissionFixture fixture) =>
        new()
        {
            BootstrapServers = fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers,
            AllowAutoCreateTopics = false,
        };
}

/// <summary>Private fixture-only storage access. Raw keys never enter qualification attachments.</summary>
internal static class CdcRetirementOffsetStoreProbe
{
    internal static async Task WriteAsync(
        ClientConfig client,
        string topic,
        string key,
        string value,
        CancellationToken token
    )
    {
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig(client) { Acks = Acks.All }
        )
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();
        await producer.ProduceAsync(topic, new() { Key = key, Value = value }, token);
    }

    internal sealed record Record(int Partition, long Offset, string Key, string Value);

    internal static Task<Record[]> ReadAsync(ClientConfig client, string topic, CancellationToken token) =>
        Task.Run(
            () =>
            {
                using var admin = new AdminClientBuilder(new AdminClientConfig(client))
                    .SetLogHandler((_, _) => { })
                    .SetErrorHandler((_, _) => { })
                    .Build();
                using var consumer = new ConsumerBuilder<string, string>(
                    new ConsumerConfig(client)
                    {
                        GroupId = "retirement-fixture",
                        EnableAutoCommit = false,
                        EnableAutoOffsetStore = false,
                        EnablePartitionEof = true,
                        AutoOffsetReset = AutoOffsetReset.Error,
                    }
                ).SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { }).Build();
                var partitions = admin
                    .GetMetadata(topic, TimeSpan.FromSeconds(10))
                    .Topics.Single()
                    .Partitions.Select(p => new TopicPartition(topic, p.PartitionId))
                    .ToArray();
                var bounds = partitions.ToDictionary(
                    p => p,
                    p => consumer.QueryWatermarkOffsets(p, TimeSpan.FromSeconds(10))
                );
                consumer.Assign(bounds.Select(p => new TopicPartitionOffset(p.Key, p.Value.Low)));
                HashSet<TopicPartition> pending = [.. partitions];
                List<Record> records = [];
                while (pending.Count > 0)
                {
                    var record = consumer.Consume(token);
                    if (record.Offset >= bounds[record.TopicPartition].High)
                    {
                        pending.Remove(record.TopicPartition);
                        consumer.Pause([record.TopicPartition]);
                    }
                    else if (!record.IsPartitionEOF)
                    {
                        records.Add(
                            new(
                                record.Partition.Value,
                                record.Offset.Value,
                                record.Message.Key,
                                record.Message.Value
                            )
                        );
                    }
                }
                return records.ToArray();
            },
            token
        );
}
