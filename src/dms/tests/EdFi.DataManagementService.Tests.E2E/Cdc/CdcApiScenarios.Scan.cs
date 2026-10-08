// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Cdc.Tests.Integration;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal sealed partial class CdcApiScenarios
{
    // One instance per scenario retains both topics' positions across its checkpoints. Public
    // envelope/reducer assertions stay with the scenario, including consume-before-release.
    private sealed class ScenarioScan(CdcApiScenarios owner, string scenarioId)
    {
        private readonly CdcBinding _binding = owner._context.Request.Binding;
        private readonly string _progressTopic = CdcArtifactNameGenerator
            .RecoverFromBinding(owner._context.Request.Binding)
            .Inventory!.ProgressTopicName;
        private IReadOnlyList<MessageContractKafkaBoundary> _positions = [];

        public async Task<IReadOnlyList<MessageContractPartitionBounds>> InitializeAsync(
            CancellationToken token
        )
        {
            _positions = await CaptureAsync(token);
            return _positions
                .Where(b => b.Topic == _binding.TopicName)
                .Select(b => new MessageContractPartitionBounds(b.Partition, b.EndOffset, b.EndOffset))
                .ToArray();
        }

        public async Task<MessageContractKafkaScan> ScanAsync(string checkpoint, CancellationToken token)
        {
            string label = scenarioId + ":" + checkpoint;
            await owner.WriteDiagnosticAsync($"{label}:provider-fence-started");
            if (_binding.Provider == CdcProvider.Postgresql)
            {
                await owner._context.Fences.FencePostgresqlSourceAsync(label, token);
            }
            else
            {
                await owner._context.Fences.FenceSqlServerSourceAsync(label, token);
            }
            await owner.WriteDiagnosticAsync($"{label}:provider-fence-completed");
            // A failed/cancelled fence cannot advance positions or capture end offsets. Preserve
            // completed scan positions through compacted gaps; quiet/heartbeat receipt is no fence.
            var ends = await CaptureAsync(token);
            var bounds = ends.Select(end =>
                    end with
                    {
                        StartOffset = _positions
                            .Single(p => p.Topic == end.Topic && p.Partition == end.Partition)
                            .EndOffset,
                    }
                )
                .ToArray();
            var scan = await owner._context.Kafka.ConsumeThroughAsync(bounds, token);
            foreach (var record in scan.Records.Where(r => r.Topic == _progressTopic))
            {
                MessageContractProgressAssertions.AssertHeartbeat(_binding, record);
            }
            await owner._context.AssertTopicInventoryAsync(token);
            _positions = scan.CompletedBoundaries;
            foreach (var bound in _positions)
            {
                await owner.WriteDiagnosticAsync(
                    $"{label}: {(bound.Topic == _binding.TopicName ? "public" : "progress")} partition={bound.Partition} start={bound.StartOffset} end={bound.EndOffset}"
                );
            }
            return new(
                scan.Records.Where(r => r.Topic == _binding.TopicName).ToArray(),
                scan.CompletedBoundaries.Where(b => b.Topic == _binding.TopicName).ToArray()
            );
        }

        private async Task<IReadOnlyList<MessageContractKafkaBoundary>> CaptureAsync(
            CancellationToken token
        ) =>
            [
                .. await owner._context.Kafka.CaptureKafkaBoundariesAsync(_binding.TopicName, token),
                .. await owner._context.Kafka.CaptureKafkaBoundariesAsync(_progressTopic, token),
            ];
    }
}
