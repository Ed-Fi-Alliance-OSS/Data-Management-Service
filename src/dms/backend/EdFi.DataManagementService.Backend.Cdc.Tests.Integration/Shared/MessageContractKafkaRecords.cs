// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

internal sealed record MessageContractKafkaBoundary(
    string Topic,
    int Partition,
    long StartOffset,
    long EndOffset
);

internal sealed record MessageContractKafkaBytes(bool IsNull, byte[] Bytes)
{
    public static MessageContractKafkaBytes From(byte[] bytes) =>
        bytes is null ? new(true, []) : new(false, bytes.ToArray());

    public override string ToString() => IsNull ? "Kafka null" : $"{Bytes.Length} bytes";
}

internal sealed record MessageContractKafkaHeader(string Key, MessageContractKafkaBytes Value);

internal sealed record MessageContractKafkaRecord(
    string Topic,
    int Partition,
    long Offset,
    MessageContractKafkaBytes Key,
    MessageContractKafkaBytes Value,
    IReadOnlyList<MessageContractKafkaHeader> Headers,
    long BrokerTimestamp
)
{
    public override string ToString() =>
        $"Kafka record: partition {Partition}, offset {Offset}, value {Value}";
}

internal sealed record MessageContractKafkaScan(
    IReadOnlyList<MessageContractKafkaRecord> Records,
    IReadOnlyList<MessageContractKafkaBoundary> CompletedBoundaries
);
