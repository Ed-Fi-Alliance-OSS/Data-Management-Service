// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_CdcEnvelopeExpectations
{
    private CdcSourceDocument _source = null!;
    private JsonObject _body = null!;

    [SetUp]
    public void Setup()
    {
        _source = new(
            12,
            Guid.Parse("ABCDEF00-0000-0000-0000-000000000001"),
            32,
            901,
            new DateTimeOffset(2026, 9, 28, 12, 30, 45, TimeSpan.FromHours(-5)).AddTicks(1234567),
            "abcdef0123456789",
            "EdFi",
            "Student",
            "5.2.0"
        );
        _body = JsonNode
            .Parse(
                """
                {"studentUniqueId":"cdc-unique", "firstName":"Original", "lastSurname":"CDC", "birthDate":"2010-05-01",
                 "id":"http-id", "_etag":"901-abcdef01.j._.n.i", "_lastModifiedDate":"http-date"}
                """
            )!
            .AsObject();
    }

    [Test]
    public void It_builds_the_complete_envelope_from_api_body_and_canonical_metadata()
    {
        var actual = JsonNode.Parse(
            CdcEnvelopeExpectations.Create(CdcApiResource.Student, _body, _source).GetRawText()
        );
        var expected = JsonNode.Parse(
            """
            {"contractVersion":1,"documentUuid":"abcdef00-0000-0000-0000-000000000001",
             "projectName":"EdFi","resourceName":"Student","resourceVersion":"5.2.0","contentVersion":901,
             "lastModifiedAt":"2026-09-28T17:30:45Z",
             "document":{"id":"abcdef00-0000-0000-0000-000000000001","studentUniqueId":"cdc-unique",
             "firstName":"Original","lastSurname":"CDC","birthDate":"2010-05-01",
             "_etag":"901-abcdef01.j._.l.i","_lastModifiedDate":"2026-09-28T17:30:45Z"}}
            """
        );
        JsonNode.DeepEquals(actual, expected).Should().BeTrue();
    }

    [Test]
    public void It_uses_descriptor_no_link_mode_and_preserves_optional_api_fields()
    {
        JsonObject body = CdcApiClient.NewSchoolTypeDescriptor("Created");
        body["description"] = "Optional description";
        body["effectiveBeginDate"] = "2026-09-01";
        body["_etag"] = "wrong-http-etag";
        var source = _source with { ResourceKeyId = 17, ResourceName = "SchoolTypeDescriptor" };
        var envelope = CdcEnvelopeExpectations.Create(CdcApiResource.SchoolTypeDescriptor, body, source);
        var expectedBody = body.DeepClone().AsObject();
        expectedBody["id"] = source.DocumentUuid.ToString("D");
        expectedBody["_etag"] = "901-abcdef01.j._.n.i";
        expectedBody["_lastModifiedDate"] = "2026-09-28T17:30:45Z";
        JsonNode
            .DeepEquals(JsonNode.Parse(envelope.GetProperty("document").GetRawText()), expectedBody)
            .Should()
            .BeTrue();
        envelope.GetProperty("resourceName").GetString().Should().Be("SchoolTypeDescriptor");
    }

    [Test]
    public void It_tracks_changed_input_source_version_and_resource_key_metadata()
    {
        _body["firstName"] = "Updated";
        var source = _source with
        {
            ContentVersion = 9223372036854775806,
            EffectiveSchemaHash = "123456789abcdef0",
            ProjectName = "AuthoritativeProject",
            ResourceVersion = "6.0.0",
            ContentLastModifiedAt = _source.ContentLastModifiedAt.AddSeconds(1),
        };
        var envelope = CdcEnvelopeExpectations.Create(CdcApiResource.Student, _body, source);
        envelope.GetProperty("contentVersion").GetInt64().Should().Be(9223372036854775806);
        envelope.GetProperty("projectName").GetString().Should().Be("AuthoritativeProject");
        envelope.GetProperty("resourceVersion").GetString().Should().Be("6.0.0");
        envelope.GetProperty("lastModifiedAt").GetString().Should().Be("2026-09-28T17:30:46Z");
        envelope
            .GetProperty("document")
            .GetProperty("_etag")
            .GetString()
            .Should()
            .Be("9223372036854775806-12345678.j._.l.i");
        envelope.GetProperty("document").GetProperty("firstName").GetString().Should().Be("Updated");
    }

    [Test]
    public void It_does_not_rewrite_the_api_result_used_by_later_requests()
    {
        string original = _body.ToJsonString();
        _ = CdcEnvelopeExpectations.Create(CdcApiResource.Student, _body, _source);
        _body.ToJsonString().Should().Be(original);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void It_rejects_metadata_from_a_different_resource_key(bool descriptor)
    {
        Action act = () =>
            CdcEnvelopeExpectations.Create(
                descriptor ? CdcApiResource.SchoolTypeDescriptor : CdcApiResource.Student,
                _body,
                _source with
                {
                    ResourceName = descriptor ? "Student" : "SchoolTypeDescriptor",
                }
            );
        act.Should().Throw<InvalidOperationException>();
    }
}
