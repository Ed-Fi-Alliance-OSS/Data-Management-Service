// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal static class MessageContractConsumerOrderingData
{
    public static IEnumerable<TestFixtureData> Cases =>
        new[]
        {
            "ordinary-link-bearing-student-school-association",
            "descriptor-school-type",
            "extension-student-school-association",
            "school-address-property-absence",
        }.Select(name =>
        {
            var fixture = new TestFixtureData(name);
            fixture.Properties.Set("ScenarioId", $"MC-CONSUMER-ORDERING-{name.ToUpperInvariant()}");
            return fixture;
        });

    public static MessageContractFixture Fixture(string name) =>
        MessageContractFixtureCatalog
            .LoadAll(TestContext.CurrentContext.TestDirectory)
            .First(f => f.ScenarioId == $"MC-FIX-PG-{name.ToUpperInvariant()}");

    public static byte[] Bytes(JsonElement envelope, long version, string correction = "")
    {
        JsonObject value = JsonNode.Parse(envelope.GetRawText())!.AsObject();
        value["contentVersion"] = version;
        if (correction.Length != 0)
        {
            value["document"]!["correction"] = correction;
            value["document"]!["_etag"] = "opaque-correction-validator";
        }
        return JsonSerializer.SerializeToUtf8Bytes(value);
    }

    public static MessageContractConsumer NewConsumer()
    {
        var consumer = new MessageContractConsumer(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero));
        consumer.Assign([new(0, 10, 20), new(1, 30, 30)]);
        return consumer;
    }
}

[TestFixtureSource(
    typeof(MessageContractConsumerOrderingData),
    nameof(MessageContractConsumerOrderingData.Cases)
)]
[Category("CdcMessageContract")]
public class Given_MessageContractConsumerOrdering(string caseName)
{
    private MessageContractConsumer _consumer = null!;
    private MessageContractFixture _fixture = null!;
    private byte[] _key = [];
    private byte[] _original = [];
    private string _uuid = string.Empty;
    private long _version;
    private MessageContractConsumerApplyResult _first;

    [SetUp]
    public void Setup()
    {
        _fixture = MessageContractConsumerOrderingData.Fixture(caseName);
        _uuid = _fixture.ExpectedEnvelope.GetProperty("documentUuid").GetString()!;
        _key = Encoding.UTF8.GetBytes(_uuid);
        _version = _fixture.ExpectedEnvelope.GetProperty("contentVersion").GetInt64();
        _original = JsonSerializer.SerializeToUtf8Bytes(_fixture.ExpectedEnvelope);
        _consumer = MessageContractConsumerOrderingData.NewConsumer();
        _first = Apply(_original, 10);
    }

    [Test]
    public void It_inserts_the_first_non_null_record() =>
        _first.Should().Be(MessageContractConsumerApplyResult.Inserted);

    [Test]
    public void It_preserves_the_complete_shared_document_and_opaque_etag() =>
        MessageContractJson.ShouldEqual(_consumer.Documents[_uuid].Envelope, _fixture.ExpectedEnvelope);

    [Test]
    public void It_replaces_only_with_a_higher_version()
    {
        byte[] corrected = MessageContractConsumerOrderingData.Bytes(
            _fixture.ExpectedEnvelope,
            _version + 1,
            "corrected"
        );
        Apply(corrected, 11).Should().Be(MessageContractConsumerApplyResult.Replaced);
        _consumer
            .Documents[_uuid]
            .SerializedValue.AsSpan()
            .SequenceEqual(corrected)
            .Should()
            .BeTrue("retained bytes must match (payloads redacted)");
    }

    [Test]
    public void It_ignores_lower_version_corrective_republishing_at_a_later_offset()
    {
        Apply(
                MessageContractConsumerOrderingData.Bytes(
                    _fixture.ExpectedEnvelope,
                    _version - 1,
                    "corrected"
                ),
                19
            )
            .Should()
            .Be(MessageContractConsumerApplyResult.Stale);
        _consumer
            .Documents[_uuid]
            .SerializedValue.AsSpan()
            .SequenceEqual(_original)
            .Should()
            .BeTrue("retained bytes must match (payloads redacted)");
    }

    [Test]
    public void It_ignores_identical_equal_version_duplicates()
    {
        Apply(_original, 11).Should().Be(MessageContractConsumerApplyResult.Duplicate);
        _consumer
            .Documents[_uuid]
            .SerializedValue.AsSpan()
            .SequenceEqual(_original)
            .Should()
            .BeTrue("retained bytes must match (payloads redacted)");
    }

    [Test]
    public void It_reports_equal_version_correction_as_a_producer_violation()
    {
        Apply(MessageContractConsumerOrderingData.Bytes(_fixture.ExpectedEnvelope, _version, "corrected"), 19)
            .Should()
            .Be(MessageContractConsumerApplyResult.ProducerContractViolation);
        _consumer
            .Documents[_uuid]
            .SerializedValue.AsSpan()
            .SequenceEqual(_original)
            .Should()
            .BeTrue("retained bytes must match (payloads redacted)");
    }

    [Test]
    public void It_reports_even_semantically_equivalent_byte_different_duplicates()
    {
        byte[] respelled = Encoding.UTF8.GetBytes(" \n" + Encoding.UTF8.GetString(_original));
        using JsonDocument json = JsonDocument.Parse(respelled);
        MessageContractJson.ShouldEqual(json.RootElement, _fixture.ExpectedEnvelope);
        Apply(respelled, 11).Should().Be(MessageContractConsumerApplyResult.ProducerContractViolation);
        _consumer
            .Documents[_uuid]
            .SerializedValue.AsSpan()
            .SequenceEqual(_original)
            .Should()
            .BeTrue("retained bytes must match (payloads redacted)");
    }

    [Test]
    public void It_applies_a_strictly_higher_correction_after_rejected_lower_and_equal_corrections()
    {
        Apply(
            MessageContractConsumerOrderingData.Bytes(_fixture.ExpectedEnvelope, _version - 1, "corrected"),
            11
        );
        Apply(
            MessageContractConsumerOrderingData.Bytes(_fixture.ExpectedEnvelope, _version, "corrected"),
            12
        );
        byte[] corrected = MessageContractConsumerOrderingData.Bytes(
            _fixture.ExpectedEnvelope,
            _version + 1,
            "corrected"
        );
        Apply(corrected, 13).Should().Be(MessageContractConsumerApplyResult.Replaced);
        _consumer
            .Documents[_uuid]
            .SerializedValue.AsSpan()
            .SequenceEqual(corrected)
            .Should()
            .BeTrue("retained bytes must match (payloads redacted)");
    }

    [Test]
    public void It_deletes_with_keyed_kafka_null_and_no_delete_body()
    {
        Delete(11).Should().Be(MessageContractConsumerApplyResult.Deleted);
        _consumer.Documents.Should().BeEmpty();
    }

    [Test]
    public void It_accepts_a_delete_without_any_previously_seen_upsert()
    {
        _consumer = MessageContractConsumerOrderingData.NewConsumer();
        Delete(10).Should().Be(MessageContractConsumerApplyResult.Deleted);
        _consumer.Documents.Should().BeEmpty();
    }

    [Test]
    public void It_temporarily_restores_a_lower_replayed_upsert_after_delete_then_converges()
    {
        Delete(11);
        Apply(MessageContractConsumerOrderingData.Bytes(_fixture.ExpectedEnvelope, 1), 12)
            .Should()
            .Be(MessageContractConsumerApplyResult.Inserted);
        _consumer.Documents[_uuid].ContentVersion.Should().Be(1);
        Delete(13).Should().Be(MessageContractConsumerApplyResult.Deleted);
        _consumer.Documents.Should().BeEmpty();
    }

    [Test]
    public void It_advances_delivery_checkpoints_for_ignored_records_without_replacing_state()
    {
        Apply(MessageContractConsumerOrderingData.Bytes(_fixture.ExpectedEnvelope, _version - 1), 14);
        Apply(_original, 15);
        Apply(
            MessageContractConsumerOrderingData.Bytes(_fixture.ExpectedEnvelope, _version, "violation"),
            16
        );
        _consumer.CompleteCheckpoint(0, 17);
        _consumer.Checkpoints[0].Should().Be(17);
        _consumer
            .Documents[_uuid]
            .SerializedValue.AsSpan()
            .SequenceEqual(_original)
            .Should()
            .BeTrue("retained bytes must match (payloads redacted)");
    }

    [Test]
    public void It_keeps_state_when_a_delivered_delete_has_not_been_durably_applied()
    {
        _consumer.Stage(new(0, 11, _key, ReadOnlyMemory<byte>.Empty, IsKafkaNull: true));
        _consumer
            .Documents[_uuid]
            .SerializedValue.AsSpan()
            .SequenceEqual(_original)
            .Should()
            .BeTrue("retained bytes must match (payloads redacted)");
        _consumer.DurableNextOffsets[0].Should().Be(11);
    }

    [Test]
    public void It_owns_bytes_independently_of_transport_and_state_reader_buffers()
    {
        byte[] corrected = MessageContractConsumerOrderingData.Bytes(
            _fixture.ExpectedEnvelope,
            _version + 1,
            "corrected"
        );
        byte[] expected = (byte[])corrected.Clone();
        _consumer.Stage(new(0, 11, _key, corrected));
        Array.Fill(corrected, (byte)0);
        _consumer.CompleteApply(0);
        Array.Fill(_consumer.Documents[_uuid].SerializedValue, (byte)0);
        _consumer
            .Documents[_uuid]
            .SerializedValue.AsSpan()
            .SequenceEqual(expected)
            .Should()
            .BeTrue("retained bytes must match (payloads redacted)");
    }

    private MessageContractConsumerApplyResult Apply(byte[] bytes, long offset)
    {
        _consumer.Stage(new(0, offset, _key, bytes));
        return _consumer.CompleteApply(0);
    }

    private MessageContractConsumerApplyResult Delete(long offset)
    {
        _consumer.Stage(new(0, offset, _key, ReadOnlyMemory<byte>.Empty, IsKafkaNull: true));
        return _consumer.CompleteApply(0);
    }
}

[TestFixture]
[Category("CdcMessageContract")]
[Property("ScenarioId", "MC-CONSUMER-ORDERING-INT64")]
public class Given_MessageContractConsumerOrdering_exact_versions
{
    private MessageContractConsumer _consumer = null!;
    private JsonElement _envelope;
    private byte[] _key = [];
    private string _uuid = string.Empty;

    [SetUp]
    public void Setup()
    {
        _consumer = MessageContractConsumerOrderingData.NewConsumer();
        _envelope = MessageContractConsumerOrderingData
            .Fixture("ordinary-link-bearing-student-school-association")
            .ExpectedEnvelope;
        _uuid = _envelope.GetProperty("documentUuid").GetString()!;
        _key = Encoding.UTF8.GetBytes(_uuid);
    }

    [TestCase(long.MinValue, long.MinValue + 1)]
    [TestCase(-1L, 0L)]
    [TestCase(9007199254740992L, 9007199254740993L)]
    [TestCase(long.MaxValue - 1, long.MaxValue)]
    [TestCase(long.MinValue, long.MaxValue)]
    public void It_compares_signed_int64_without_rounding_or_subtraction_overflow(long lower, long higher)
    {
        Apply(lower, 10).Should().Be(MessageContractConsumerApplyResult.Inserted);
        Apply(higher, 11).Should().Be(MessageContractConsumerApplyResult.Replaced);
        Apply(lower, 12).Should().Be(MessageContractConsumerApplyResult.Stale);
        _consumer.Documents[_uuid].ContentVersion.Should().Be(higher);
        _consumer
            .Documents[_uuid]
            .Envelope.GetProperty("document")
            .GetProperty("_etag")
            .GetString()
            .Should()
            .Be(_envelope.GetProperty("document").GetProperty("_etag").GetString());
    }

    [Test]
    public void It_does_not_apply_one_documents_version_to_another_key()
    {
        Apply(long.MaxValue, 10);
        JsonElement other = MessageContractConsumerOrderingData
            .Fixture("descriptor-school-type")
            .ExpectedEnvelope;
        string otherUuid = other.GetProperty("documentUuid").GetString()!;
        _consumer.Stage(
            new(
                0,
                11,
                Encoding.UTF8.GetBytes(otherUuid),
                MessageContractConsumerOrderingData.Bytes(other, long.MinValue)
            )
        );
        _consumer.CompleteApply(0).Should().Be(MessageContractConsumerApplyResult.Inserted);
        _consumer.Documents[otherUuid].ContentVersion.Should().Be(long.MinValue);
        _consumer.Documents[_uuid].ContentVersion.Should().Be(long.MaxValue);
    }

    [Test]
    public void It_preserves_exact_nested_json_numbers_without_reserializing_state()
    {
        JsonElement envelope = MessageContractConsumerOrderingData.Fixture("exact-numbers").ExpectedEnvelope;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        _consumer.Stage(new(0, 10, _key, bytes));
        _consumer.CompleteApply(0);
        MessageContractJson.ShouldEqual(_consumer.Documents[_uuid].Envelope, envelope);
        _consumer
            .Documents[_uuid]
            .SerializedValue.AsSpan()
            .SequenceEqual(bytes)
            .Should()
            .BeTrue("retained bytes must match (payloads redacted)");
    }

    private MessageContractConsumerApplyResult Apply(long version, long offset)
    {
        _consumer.Stage(new(0, offset, _key, MessageContractConsumerOrderingData.Bytes(_envelope, version)));
        return _consumer.CompleteApply(0);
    }
}

[TestFixture]
[Category("CdcMessageContract")]
[Property("ScenarioId", "MC-CONSUMER-ORDERING-DURABILITY")]
public class Given_MessageContractConsumerOrdering_durability
{
    private MessageContractConsumer _consumer = null!;
    private MessageContractConsumerRecord _record = null!;

    [SetUp]
    public void Setup()
    {
        _consumer = MessageContractConsumerOrderingData.NewConsumer();
        JsonElement envelope = MessageContractConsumerOrderingData
            .Fixture("ordinary-link-bearing-student-school-association")
            .ExpectedEnvelope;
        _record = new(
            0,
            10,
            Encoding.UTF8.GetBytes(envelope.GetProperty("documentUuid").GetString()!),
            JsonSerializer.SerializeToUtf8Bytes(envelope)
        );
        _consumer.Stage(_record);
    }

    [Test]
    public void It_keeps_delivered_state_pending_until_durable_completion()
    {
        _consumer.HasPendingApplies.Should().BeTrue();
        _consumer.Documents.Should().BeEmpty();
        _consumer.DurableNextOffsets[0].Should().Be(10);
        _consumer.Checkpoints.Should().BeEmpty();
    }

    [Test]
    public void It_completes_state_before_the_separate_checkpoint()
    {
        _consumer.CompleteApply(0);
        _consumer.Documents.Should().ContainSingle();
        _consumer.DurableNextOffsets[0].Should().Be(11);
        _consumer.Checkpoints.Should().BeEmpty();
        _consumer.CompleteCheckpoint(0, 11);
        _consumer.Checkpoints[0].Should().Be(11);
    }

    [Test]
    public void It_refuses_to_checkpoint_an_unfinished_state_write() =>
        FluentActions
            .Invoking(() => _consumer.CompleteCheckpoint(0, 11))
            .Should()
            .Throw<InvalidOperationException>();

    [Test]
    public void It_refuses_to_scan_past_an_unfinished_state_write() =>
        FluentActions
            .Invoking(() => _consumer.CompleteScan(0, 20))
            .Should()
            .Throw<InvalidOperationException>();

    [Test]
    public void It_keeps_partition_completion_independent_and_accepts_an_empty_partition()
    {
        _consumer.CompleteScan(1, 30);
        _consumer.CompleteCheckpoint(1, 30);
        _consumer.Checkpoints[1].Should().Be(30);
        _consumer.Checkpoints.ContainsKey(0).Should().BeFalse();
        _consumer.HasPendingApplies.Should().BeTrue();
    }

    [Test]
    public void It_exposes_exclusive_end_offsets_without_advancing_durable_progress()
    {
        _consumer.CaptureEndOffsets(new Dictionary<int, long> { [0] = 25, [1] = 30 });
        _consumer.Assignment[0].EndOffset.Should().Be(25);
        _consumer.DurableNextOffsets[0].Should().Be(10);
        _consumer.Checkpoints.Should().BeEmpty();
    }

    [Test]
    public void It_accepts_transport_scan_positions_across_compacted_gaps()
    {
        _consumer.CompleteApply(0);
        _consumer.CompleteScan(0, 20);
        _consumer.CompleteCheckpoint(0, 20);
        _consumer.Checkpoints[0].Should().Be(20);
    }

    [Test]
    public void It_refuses_checkpoint_regression()
    {
        _consumer.CompleteApply(0);
        _consumer.CompleteCheckpoint(0, 11);
        FluentActions
            .Invoking(() => _consumer.CompleteCheckpoint(0, 10))
            .Should()
            .Throw<InvalidOperationException>();
    }

    [Test]
    public void It_exposes_checkpoint_loss_and_a_full_state_discard_hook()
    {
        _consumer.CompleteApply(0);
        _consumer.CompleteCheckpoint(0, 11);
        _consumer.LoseCheckpoints();
        _consumer.CheckpointHealth.Should().Be(MessageContractCheckpointHealth.Missing);
        _consumer.Checkpoints.Should().BeEmpty();
        _consumer.DiscardState();
        _consumer.Documents.Should().BeEmpty();
        _consumer.DurableNextOffsets[0].Should().Be(10);
        _consumer.DurableNextOffsets[1].Should().Be(30);
    }

    [Test]
    public void It_exposes_corruption_and_prevents_further_checkpoint_completion()
    {
        _consumer.CompleteApply(0);
        _consumer.CompleteCheckpoint(0, 11);
        _consumer.CorruptCheckpoints();
        _consumer.CheckpointHealth.Should().Be(MessageContractCheckpointHealth.Corrupt);
        FluentActions
            .Invoking(() => _consumer.CompleteCheckpoint(0, 11))
            .Should()
            .Throw<InvalidOperationException>();
    }

    [Test]
    public void It_exposes_controlled_time_without_completing_pending_work()
    {
        DateTimeOffset before = _consumer.Now;
        _consumer.AdvanceTime(TimeSpan.FromHours(24));
        _consumer.Now.Should().Be(before.AddHours(24));
        _consumer.HasPendingApplies.Should().BeTrue();
        _consumer.Checkpoints.Should().BeEmpty();
    }
}

[TestFixture]
[Category("CdcMessageContract")]
[Property("ScenarioId", "MC-CONSUMER-ORDERING-WIRE-BOUNDARY")]
public class Given_MessageContractConsumerOrdering_invalid_public_records
{
    private MessageContractConsumer _consumer = null!;
    private MessageContractConsumerRecord _record = null!;

    [SetUp]
    public void Setup()
    {
        _consumer = MessageContractConsumerOrderingData.NewConsumer();
        JsonElement envelope = MessageContractConsumerOrderingData
            .Fixture("ordinary-link-bearing-student-school-association")
            .ExpectedEnvelope;
        _record = new(
            0,
            10,
            Encoding.UTF8.GetBytes(envelope.GetProperty("documentUuid").GetString()!),
            JsonSerializer.SerializeToUtf8Bytes(envelope)
        );
    }

    [TestCase("")]
    [TestCase("null")]
    [TestCase("{\"deleted\":true}")]
    [TestCase("{synthetic-secret-body")]
    public void It_rejects_non_null_bytes_that_are_not_public_upserts(string value)
    {
        FluentActions
            .Invoking(() => _consumer.Stage(_record with { Value = Encoding.UTF8.GetBytes(value) }))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Invalid public consumer value.");
        _consumer.HasPendingApplies.Should().BeFalse();
        _consumer.Documents.Should().BeEmpty();
    }

    [TestCase("9223372036854775808")]
    [TestCase("-9223372036854775809")]
    [TestCase("1.5")]
    [TestCase("\"222\"")]
    public void It_rejects_versions_outside_the_signed_int64_wire_contract(string version)
    {
        JsonObject envelope = JsonNode.Parse(_record.Value.Span)!.AsObject();
        envelope["contentVersion"] = JsonNode.Parse(version);
        FluentActions
            .Invoking(() =>
                _consumer.Stage(_record with { Value = JsonSerializer.SerializeToUtf8Bytes(envelope) })
            )
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Invalid public consumer value.");
    }

    [TestCase("documentUuid")]
    [TestCase("id")]
    public void It_requires_both_envelope_and_document_ids_to_match_the_canonical_key(string field)
    {
        JsonObject envelope = JsonNode.Parse(_record.Value.Span)!.AsObject();
        JsonObject target = field == "id" ? envelope["document"]!.AsObject() : envelope;
        target[field] = "00000000-0000-0000-0000-000000000000";
        FluentActions
            .Invoking(() =>
                _consumer.Stage(_record with { Value = JsonSerializer.SerializeToUtf8Bytes(envelope) })
            )
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Invalid public consumer value.");
    }

    [TestCase("AAAAAAAA-BBBB-CCCC-DDDD-000000000101")]
    [TestCase("\"aaaaaaaa-bbbb-cccc-dddd-000000000101\"")]
    [TestCase("aaaaaaaabbbbccccdddd000000000101")]
    [TestCase("")]
    public void It_requires_unquoted_lowercase_d_format_uuid_key_bytes(string key) =>
        FluentActions
            .Invoking(() => _consumer.Stage(_record with { Key = Encoding.UTF8.GetBytes(key) }))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Invalid public consumer key.");

    [Test]
    public void It_rejects_a_kafka_null_flag_with_non_null_value_bytes() =>
        FluentActions
            .Invoking(() => _consumer.Stage(_record with { IsKafkaNull = true }))
            .Should()
            .Throw<InvalidOperationException>();

    [Test]
    public void It_rejects_unassigned_partition_delivery() =>
        FluentActions
            .Invoking(() => _consumer.Stage(_record with { Partition = 2 }))
            .Should()
            .Throw<InvalidOperationException>();
}
