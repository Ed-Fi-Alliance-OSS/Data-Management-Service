// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal static class MessageContractProviderAssertions
{
    public static Task AssertTopicInventoryAsync(
        CdcConnectorTemplatePinnedImageFixture fixture,
        CdcConnectorTemplateRequest request,
        CancellationToken token
    ) =>
        MessageContractRecordAssertions.AssertTopicInventoryAsync(
            fixture.HostKafkaBootstrapServers,
            request,
            token
        );

    public static void AssertUpsert(
        CdcConnectorTemplateRequest request,
        MessageContractKafkaRecord record,
        MessageContractProviderRow row
    ) => MessageContractRecordAssertions.AssertUpsert(request, record, row.Uuid, row.Partition, row.Expected);

    // An update republishes the owner's creation time unchanged while lastModifiedAt advances.
    public static void AssertCreationTimeKeptAcrossUpdates(
        IEnumerable<MessageContractKafkaRecord> published,
        IReadOnlyList<MessageContractProviderRow> rows,
        IReadOnlyList<MessageContractProviderRow> updates
    )
    {
        JsonElement[] envelopes = published
            .Where(record => !record.Value.IsNull)
            .Select(record => JsonSerializer.Deserialize<JsonElement>(record.Value.Bytes))
            .ToArray();
        rows.Should().HaveSameCount(updates);
        for (int index = 0; index < rows.Count; index++)
        {
            JsonElement created = Published(envelopes, rows[index]);
            JsonElement updated = Published(envelopes, updates[index]);
            updated
                .GetProperty("documentCreatedAt")
                .GetString()
                .Should()
                .Be(created.GetProperty("documentCreatedAt").GetString());
            updated
                .GetProperty("lastModifiedAt")
                .GetString()
                .Should()
                .NotBe(created.GetProperty("lastModifiedAt").GetString());
        }
    }

    private static JsonElement Published(JsonElement[] envelopes, MessageContractProviderRow row) =>
        envelopes
            .Where(envelope =>
                envelope.GetProperty("documentUuid").GetString() == row.Uuid
                && envelope.GetProperty("contentVersion").GetInt64()
                    == row.Expected.GetProperty("contentVersion").GetInt64()
            )
            .Should()
            .ContainSingle()
            .Subject;

    public static void AssertSchema(JsonElement schema, string type, string name, int version)
    {
        schema.GetProperty("type").GetString().Should().Be(type);
        schema.GetProperty("name").GetString().Should().Be(name);
        schema.GetProperty("version").GetInt32().Should().Be(version);
        schema.GetProperty("optional").GetBoolean().Should().BeFalse();
    }
}
