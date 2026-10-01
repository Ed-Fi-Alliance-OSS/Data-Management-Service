// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal enum MessageContractProgressKind
{
    RelationalHeartbeat,
    NativeHeartbeat,
}

internal static class MessageContractProgressAssertions
{
    // Classify retained Kafka values; no source-observer instrumentation is required.
    public static MessageContractProgressKind AssertHeartbeat(
        CoreCdc.CdcBinding binding,
        MessageContractKafkaRecord record
    )
    {
        record
            .Topic.Should()
            .Be(CoreCdc.CdcArtifactNameGenerator.RecoverFromBinding(binding).Inventory!.ProgressTopicName);
        record.Partition.Should().Be(0);
        record.Key.IsNull.Should().BeFalse();
        record.Key.Bytes.Should().Equal(Convert.FromHexString("6364632D70726F6772657373"));
        record.Value.IsNull.Should().BeFalse();
        JsonElement value = JsonSerializer.Deserialize<JsonElement>(record.Value.Bytes);
        value.TryGetProperty("schema", out _).Should().BeFalse();
        value.TryGetProperty("payload", out _).Should().BeFalse();
        if (value.TryGetProperty("source", out JsonElement source))
        {
            source.GetProperty("schema").GetString().Should().Be("dms");
            source.GetProperty("table").GetString().Should().Be("CdcHeartbeat");
            (source.GetProperty("name").GetString() == binding.ConnectorName).Should().BeTrue();
            return MessageContractProgressKind.RelationalHeartbeat;
        }
        else
        {
            value.EnumerateObject().Select(p => p.Name).Should().Equal("ts_ms");
            value.GetProperty("ts_ms").GetInt64().Should().BeGreaterThan(0);
            return MessageContractProgressKind.NativeHeartbeat;
        }
    }
}
