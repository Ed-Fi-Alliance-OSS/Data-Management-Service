// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc.Tests.Integration;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture(false)]
[TestFixture(true)]
public class Given_CdcCrudAssertions(bool descriptor)
{
    private static readonly Guid Uuid = Guid.Parse("abcdef00-0000-0000-0000-000000000001");
    private CdcBinding _binding = null!;
    private CdcSourceDocument _source = null!;
    private JsonObject _body = null!;
    private Dictionary<long, JsonElement> _expected = null!;
    private MessageContractConsumer _consumer = null!;
    private int _partition;
    private CdcApiResource Resource =>
        descriptor ? CdcApiResource.SchoolTypeDescriptor : CdcApiResource.Student;
    private string UpdatedField => descriptor ? "shortDescription" : "firstName";

    [SetUp]
    public void Setup()
    {
        _binding = new(
            1,
            "deployment",
            "",
            "1",
            "instance",
            1,
            CdcProvider.Postgresql,
            "source",
            "connector",
            "public",
            3,
            "kafka-murmur2-v1",
            1
        );
        _source = new(
            12,
            Uuid,
            32,
            901,
            new DateTimeOffset(2026, 9, 28, 12, 30, 45, TimeSpan.Zero),
            "abcdef0123456789",
            "EdFi",
            descriptor ? "SchoolTypeDescriptor" : "Student",
            "5.2.0"
        );
        _body = descriptor
            ? CdcApiClient.NewSchoolTypeDescriptor("Created")
            : CdcApiClient.NewStudent("Created");
        _expected = new() { [901] = CdcEnvelopeExpectations.Create(Resource, _body, _source) };
        _partition = MessageContractPartition.ForUuid(Uuid.ToString("D"), 3, _binding.PartitionerAlgorithm);
        _consumer = new(DateTimeOffset.UtcNow);
        _consumer.Assign(
            Enumerable.Range(0, 3).Select(p => new MessageContractPartitionBounds(p, 0, 0)).ToArray()
        );
    }

    [Test]
    public void It_retains_created_then_updated_state_and_applies_duplicate_tombstones_across_scans()
    {
        Apply([Upsert(0), Upsert(2)], 4);
        _consumer.Documents[Uuid.ToString("D")].ContentVersion.Should().Be(901);
        _body[UpdatedField] = "Updated";
        _expected[902] = CdcEnvelopeExpectations.Create(
            Resource,
            _body,
            _source with
            {
                ContentVersion = 902,
            }
        );
        Apply([Upsert(4, 902), Upsert(6, 901), Upsert(7, 902)], 9);
        _consumer.Documents[Uuid.ToString("D")].ContentVersion.Should().Be(902);
        _consumer
            .Documents[Uuid.ToString("D")]
            .Envelope.GetProperty("document")
            .GetProperty(UpdatedField)
            .GetString()
            .Should()
            .Be("Updated");
        Apply([Tombstone(9), Tombstone(10)], 12, true);
        _consumer.Documents.Should().BeEmpty();
        _consumer.DurableNextOffsets.Values.Should().OnlyContain(offset => offset == 12);
    }

    [Test]
    public void It_rejects_equal_version_replay_with_different_bytes_even_when_json_is_equal()
    {
        Apply([Upsert(0)], 1);
        var duplicate = Upsert(1) with
        {
            Value = new(false, Encoding.UTF8.GetBytes(" " + _expected[901].GetRawText())),
        };
        Action act = () => Apply([duplicate], 2);
        act.Should().Throw<AssertionException>();
    }

    [TestCase("key")]
    [TestCase("topic")]
    [TestCase("partition")]
    [TestCase("body")]
    [TestCase("version")]
    [TestCase("null-key")]
    [TestCase("resource-name")]
    [TestCase("link")]
    [TestCase("etag-link-mode")]
    public void It_rejects_unexpected_public_records_instead_of_filtering_them_out(string mutation)
    {
        var record = Upsert(0);
        var value = JsonNode.Parse(_expected[901].GetRawText())!.AsObject();
        if (mutation == "body")
        {
            value["document"]![UpdatedField] = "Incorrect";
        }
        if (mutation == "version")
        {
            value["contentVersion"] = 999;
        }
        if (mutation == "resource-name")
        {
            value["resourceName"] = descriptor ? "Student" : "SchoolTypeDescriptor";
        }
        if (mutation == "link")
        {
            value["document"]!["link"] = new JsonObject { ["href"] = "/unexpected", ["rel"] = "self" };
        }
        if (mutation == "etag-link-mode")
        {
            value["document"]!["_etag"] = descriptor ? "901-abcdef01.j._.l.i" : "901-abcdef01.j._.n.i";
        }
        record = mutation switch
        {
            "key" => record with { Key = new(false, Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D"))) },
            "topic" => record with { Topic = "raw.DocumentProjectionWork" },
            "partition" => record with { Partition = (_partition + 1) % 3 },
            "null-key" => record with { Key = new(true, []) },
            _ => record with { Value = new(false, JsonSerializer.SerializeToUtf8Bytes(value)) },
        };
        Action act = () => Apply([record], 1);
        act.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_rejects_publication_while_the_initial_work_is_held()
    {
        var record = Upsert(0);
        _expected.Clear();
        Action act = () => Apply([record], 1);
        act.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_rejects_a_tombstone_before_canonical_delete()
    {
        Action act = () => Apply([Tombstone(0)], 1);
        act.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_rejects_json_null_as_a_record_level_tombstone()
    {
        var record = Tombstone(0) with { Value = new(false, "null"u8.ToArray()) };
        Action act = () => Apply([record], 1, true);
        act.Should().Throw<InvalidOperationException>();
    }

    [TestCase("body")]
    [TestCase("version")]
    [TestCase("etag")]
    [TestCase("timestamp")]
    [TestCase("embedded-etag")]
    [TestCase("resource-name")]
    [TestCase("link")]
    public void It_rejects_cache_rows_that_do_not_match_the_independent_expected_state(string mutation)
    {
        var document = JsonNode.Parse(_expected[901].GetProperty("document").GetRawText())!.AsObject();
        string etag = document["_etag"]!.GetValue<string>();
        document.Remove("_etag");
        var cache = new CdcCacheDocument(
            Uuid,
            901,
            etag,
            _source.ContentLastModifiedAt,
            "EdFi",
            descriptor ? "SchoolTypeDescriptor" : "Student",
            "5.2.0",
            document
        );
        CdcCrudAssertions.AssertCache(cache, _source, _expected[901]);
        if (mutation == "body")
        {
            document[UpdatedField] = "Incorrect";
        }
        if (mutation == "embedded-etag")
        {
            document["_etag"] = etag;
        }
        if (mutation == "link")
        {
            document["link"] = new JsonObject { ["href"] = "/unexpected", ["rel"] = "self" };
        }
        cache = mutation switch
        {
            "version" => cache with { ContentVersion = 902 },
            "resource-name" => cache with { ResourceName = "Incorrect" },
            "etag" => cache with { StreamEtag = "HTTP-etag" },
            "timestamp" => cache with { LastModifiedAt = _source.ContentLastModifiedAt.AddSeconds(1) },
            _ => cache,
        };
        Action act = () => CdcCrudAssertions.AssertCache(cache, _source, _expected[901]);
        act.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_checks_the_held_http_response_body_without_requiring_the_stream_etag()
    {
        var actual = JsonNode.Parse(_expected[901].GetProperty("document").GetRawText())!.AsObject();
        actual["_etag"] = "HTTP-etag";
        CdcCrudAssertions.AssertApiBody(actual, _body, Uuid);
        actual[UpdatedField] = "Stale";
        Action act = () => CdcCrudAssertions.AssertApiBody(actual, _body, Uuid);
        act.Should().Throw<AssertionException>();
    }

    private MessageContractKafkaRecord Upsert(long offset, long version = 901) =>
        new(
            _binding.TopicName,
            _partition,
            offset,
            new(false, Encoding.UTF8.GetBytes(Uuid.ToString("D"))),
            new(false, JsonSerializer.SerializeToUtf8Bytes(_expected[version])),
            [],
            1
        );

    private MessageContractKafkaRecord Tombstone(long offset) =>
        Upsert(offset) with
        {
            Value = new(true, []),
        };

    private void Apply(
        IReadOnlyList<MessageContractKafkaRecord> records,
        long end,
        bool allowTombstones = false
    ) =>
        CdcCrudAssertions.ApplyPublicScan(
            _binding,
            Uuid,
            _expected,
            new(
                records,
                Enumerable
                    .Range(0, 3)
                    .Select(p => new MessageContractKafkaBoundary(
                        _binding.TopicName,
                        p,
                        _consumer.DurableNextOffsets[p],
                        end
                    ))
                    .ToArray()
            ),
            _consumer,
            allowTombstones
        );
}
