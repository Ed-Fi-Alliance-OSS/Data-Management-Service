// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.Tests.E2E.Management;

/// <summary>
/// Represents a Kafka message captured during E2E testing, containing all relevant metadata
/// and content from the message for test assertions.
/// </summary>
public record KafkaTestMessage
{
    /// <summary>
    /// The Kafka topic name this message was received from
    /// </summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>
    /// The message key, typically contains the resource identifier for DMS document messages
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// The raw string value of the message payload
    /// </summary>
    public string Value { get; init; } = string.Empty;

    /// <summary>
    /// The message value parsed as JSON if it's valid JSON, otherwise null
    /// </summary>
    public JsonNode? ValueAsJson { get; init; }

    /// <summary>
    /// The UTC timestamp when the message was produced to Kafka
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// The Kafka partition number this message was received from
    /// </summary>
    public int Partition { get; init; }

    /// <summary>
    /// The offset of this message within its partition
    /// </summary>
    public long Offset { get; init; }
}
