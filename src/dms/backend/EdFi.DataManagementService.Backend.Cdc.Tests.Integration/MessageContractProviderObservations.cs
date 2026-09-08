// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal static class MessageContractProviderObservations
{
    public static async Task CapturePhaseAsync(
        CdcConnectorTemplatePinnedImageFixture fixture,
        CdcConnectorTemplateRequest request,
        string phase,
        Dictionary<string, MessageContractKafkaScan> publicScans,
        Dictionary<string, MessageContractKafkaScan> progressScans,
        CancellationToken token
    )
    {
        await CaptureAsync(publicScans, request.PublicTopicName);
        await CaptureAsync(progressScans, request.ProgressTopicName);
        async Task CaptureAsync(Dictionary<string, MessageContractKafkaScan> scans, string topic)
        {
            var bounds = await fixture.CaptureKafkaBoundariesAsync(topic, token);
            // Each topic resumes at its own previous completed end, matched by partition.
            if (scans.Count > 0)
            {
                var previous = scans.Last().Value.CompletedBoundaries;
                bounds = bounds
                    .Select(b =>
                        b with
                        {
                            StartOffset = previous.Single(p => p.Partition == b.Partition).EndOffset,
                        }
                    )
                    .ToArray();
            }
            MessageContractKafkaScan scan = await fixture.ConsumeThroughAsync(bounds, token);
            scans.Add(phase, scan);
            string kind = topic == request.PublicTopicName ? "public" : "progress";
            await TestContext.Out.WriteLineAsync(
                $"{phase} {kind}: {scan.Records.Count} records; "
                    + string.Join(", ", bounds.Select(b => $"p{b.Partition}=[{b.StartOffset},{b.EndOffset})"))
            );
        }
    }

    public static async Task RetainEvidenceAsync<TFence>(
        string evidenceDirectory,
        string providerDisplayName,
        IReadOnlyList<TFence> fences,
        IReadOnlyList<JsonElement> source,
        IReadOnlyDictionary<string, MessageContractKafkaScan> publicScans,
        IReadOnlyDictionary<string, MessageContractKafkaScan> progressScans,
        CancellationToken token
    )
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "TestResults",
            evidenceDirectory
        );
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"observations-{Guid.NewGuid():N}.json");
        var evidence = new
        {
            ConnectImage = Environment.GetEnvironmentVariable(MessageContractRunner.ImageVariable)
                ?? string.Empty,
            Fences = fences,
            Source = source,
            Scans = new[]
            {
                (Kind: "public", Scans: publicScans),
                (Kind: "progress", Scans: progressScans),
            }.SelectMany(group =>
                group.Scans.Select(phase => new
                {
                    group.Kind,
                    Phase = phase.Key,
                    Bounds = phase.Value.CompletedBoundaries.Select(b => new
                    {
                        b.Partition,
                        b.StartOffset,
                        b.EndOffset,
                    }),
                    Records = phase.Value.Records.Select(r => new
                    {
                        r.Partition,
                        r.Offset,
                        KeyBytes = r.Key.Bytes.Length,
                        ValueBytes = r.Value.Bytes.Length,
                        KafkaNull = r.Value.IsNull,
                    }),
                })
            ),
        };
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }),
            token
        );
        TestContext.AddTestAttachment(
            path,
            $"Bounded {providerDisplayName} source schemas and broker observation summaries; payloads omitted"
        );
    }
}
