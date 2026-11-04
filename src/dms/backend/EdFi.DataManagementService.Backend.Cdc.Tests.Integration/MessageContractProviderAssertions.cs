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

    public static void AssertSchema(JsonElement schema, string type, string name, int version)
    {
        schema.GetProperty("type").GetString().Should().Be(type);
        schema.GetProperty("name").GetString().Should().Be(name);
        schema.GetProperty("version").GetInt32().Should().Be(version);
        schema.GetProperty("optional").GetBoolean().Should().BeFalse();
    }
}
