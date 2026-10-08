// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal static class MessageContractProviderRows
{
    public static (
        IReadOnlyList<MessageContractProviderRow> Rows,
        IReadOnlyList<MessageContractProviderRow> Updates
    ) Load(string provider, string sourceLastModifiedAt)
    {
        List<MessageContractProviderRow> rows = [];
        List<MessageContractProviderRow> updates = [];
        MessageContractFixture[] shared = MessageContractFixtureCatalog
            .LoadAll(AppContext.BaseDirectory)
            .Where(f =>
                f.SourceRecord.GetProperty("provider").GetString() == provider
                && f.SourceRecord.GetProperty("operation").GetString() == "c"
            )
            .DistinctBy(f => f.MaterializedCase)
            .ToArray();
        shared.Should().HaveCount(4);
        string root = MessageContractFixtureCatalog.ResolveFixtureRoot(AppContext.BaseDirectory);
        using JsonDocument vectors = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "cdc/message-contract/partition-vectors.json"))
        );
        JsonElement[] keys = vectors.RootElement.GetProperty("vectors").EnumerateArray().ToArray();
        for (int index = 0; index < shared.Length; index++)
        {
            JsonNode cache = JsonNode.Parse(shared[index].CacheRow.GetRawText())!;
            JsonNode expected = JsonNode.Parse(shared[index].ExpectedEnvelope.GetRawText())!;
            string uuid = keys[index].GetProperty("uuid").GetString()!;
            int partition = keys[index]
                .GetProperty("partitions")
                .EnumerateArray()
                .Single(p => p.GetProperty("count").GetInt32() == 7)
                .GetProperty("expected")
                .GetInt32();
            cache["documentUuid"] = uuid;
            cache["documentJson"]!["id"] = uuid;
            expected["documentUuid"] = uuid;
            expected["document"]!["id"] = uuid;
            rows.Add(
                new(
                    JsonSerializer.SerializeToElement(cache),
                    JsonSerializer.SerializeToElement(expected),
                    partition
                )
            );
            long version = cache["contentVersion"]!.GetValue<long>() + 1000;
            cache["contentVersion"] = version;
            cache["streamEtag"] = $"opaque-live-{version}";
            cache["lastModifiedAt"] = sourceLastModifiedAt;
            cache["documentJson"]!["_lastModifiedDate"] = "2026-08-01T23:59:59Z";
            expected["contentVersion"] = version;
            expected["lastModifiedAt"] = "2026-08-01T23:59:59Z";
            expected["document"]!["_lastModifiedDate"] = "2026-08-01T23:59:59Z";
            expected["document"]!["_etag"] = $"opaque-live-{version}";
            updates.Add(
                new(
                    JsonSerializer.SerializeToElement(cache),
                    JsonSerializer.SerializeToElement(expected),
                    partition
                )
            );
        }
        return (rows, updates);
    }
}

internal sealed record MessageContractProviderRow(JsonElement CacheRow, JsonElement Expected, int Partition)
{
    public long DocumentId => CacheRow.GetProperty("documentId").GetInt64();
    public string Uuid => CacheRow.GetProperty("documentUuid").GetString()!;

    public override string ToString() => $"Provider fixture row in partition {Partition}";
}
