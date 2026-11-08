// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
public sealed class Given_RepresentationRestampKafkaRecord
{
    private CdcStateRecord _record = null!;

    [SetUp]
    public void Setup()
    {
        // Kafka's default formatter emits key/value only; the command selects exactly one topic.
        _record = RepresentationRestampCdcStateFixture.ParseRecord(
            "selected-topic",
            "document-key\t{\"description\":\"line one\\nline two\\tvalue\",\"contentVersion\":42}\r"
        );
    }

    [Test]
    public void It_uses_the_selected_topic_and_preserves_the_document_key()
    {
        _record.Topic.Should().Be("selected-topic");
        _record.Key.Should().Be("document-key");
    }

    [Test]
    public void It_preserves_the_json_state_after_parsing_the_kafka_key_value_output()
    {
        _record.Value.GetProperty("description").GetString().Should().Be("line one\nline two\tvalue");
        _record.Value.GetProperty("contentVersion").GetInt64().Should().Be(42);
    }
}
