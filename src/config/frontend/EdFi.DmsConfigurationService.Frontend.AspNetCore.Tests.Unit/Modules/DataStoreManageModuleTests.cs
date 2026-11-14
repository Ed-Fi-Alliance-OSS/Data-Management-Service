// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

public class DataStoreManageModuleTests
{
    // Step 1.1 freezes expected data. These are fixture/source-contract checks, not HTTP,
    // formatter, normalization, persistence or provider-conformance tests. Future tests
    // must consume this corpus against production code; do not add shadow implementations.
    [TestFixture]
    public class Given_approved_contract_fixtures
    {
        private static readonly string[] PublicFields =
        [
            "id",
            "name",
            "dataStoreId",
            "dataStoreName",
            "status",
            "databaseTemplate",
            "databaseName",
            "lastRefreshed",
            "lastModifiedDate",
        ];

        private static readonly string[] States =
        [
            "PendingCreate",
            "CreateInProgress",
            "CreateFailed",
            "CreateError",
            "Created",
            "PendingDelete",
            "DeleteInProgress",
            "DeleteFailed",
            "DeleteError",
            "Deleted",
        ];

        private static readonly JsonElement Fixtures = ReadFixtures();

        private static JsonElement ReadFixtures()
        {
            using Stream stream =
                typeof(DataStoreManageModuleTests).Assembly.GetManifestResourceStream(
                    "DMS1439.ConformanceFixtures.json"
                ) ?? throw new InvalidOperationException("Missing DMS-1439 contract fixtures.");
            using JsonDocument document = JsonDocument.Parse(stream);
            return document.RootElement.Clone();
        }

        private static JsonElement Model(string status) =>
            Fixtures
                .GetProperty("models")
                .EnumerateArray()
                .Single(model => model.GetProperty("status").GetString() == status);

        [Test]
        public void It_records_the_approved_revision_and_source_pin()
        {
            Fixtures.GetProperty("specRevision").GetInt32().Should().Be(9);
            Fixtures
                .GetProperty("specSha256")
                .GetString()
                .Should()
                .Be("0deda56ca66aecffb4e184cb7acc1c4d770ee7dfc3248e07ab47bfb2792098ac");
            Fixtures
                .GetProperty("upstreamCommit")
                .GetString()
                .Should()
                .Be("ed115fd8c0cb3e17dcfefc86966582c74e39f277");
        }

        [Test]
        public void It_limits_golden_requests_to_the_two_public_properties()
        {
            foreach (JsonElement request in Fixtures.GetProperty("requests").EnumerateArray())
            {
                request
                    .EnumerateObject()
                    .Select(property => property.Name)
                    .Should()
                    .BeEquivalentTo("name", "databaseTemplate");
                request.GetProperty("name").ValueKind.Should().Be(JsonValueKind.String);
                request.GetProperty("databaseTemplate").GetString().Should().BeOneOf("Minimal", "Sample");
            }
            Fixtures
                .GetProperty("invalidRequests")
                .EnumerateArray()
                .Select(item => item.GetProperty("status").GetInt32())
                .Should()
                .OnlyContain(status => status == 400);
        }

        [Test]
        public void It_preserves_exactly_nine_public_fields_in_every_state()
        {
            JsonElement[] models = Fixtures.GetProperty("models").EnumerateArray().ToArray();
            models.Select(model => model.GetProperty("status").GetString()).Should().BeEquivalentTo(States);
            foreach (JsonElement model in models)
            {
                model
                    .EnumerateObject()
                    .Select(property => property.Name)
                    .Should()
                    .BeEquivalentTo(PublicFields);
                model.GetProperty("id").GetInt32().Should().Be(7);
                model.GetProperty("name").GetString().Should().Be("school 1");
                model.GetProperty("databaseTemplate").GetString().Should().Be("Minimal");
                model.GetProperty("lastRefreshed").GetDateTimeOffset().Offset.Should().Be(TimeSpan.Zero);
                model.GetProperty("lastModifiedDate").GetDateTimeOffset().Offset.Should().Be(TimeSpan.Zero);
            }
        }

        [TestCase("PendingCreate")]
        [TestCase("CreateInProgress")]
        [TestCase("CreateFailed")]
        [TestCase("CreateError")]
        public void It_keeps_all_three_unlinked_fields_explicitly_null(string status)
        {
            foreach (string field in new[] { "dataStoreId", "dataStoreName", "databaseName" })
            {
                Model(status).GetProperty(field).ValueKind.Should().Be(JsonValueKind.Null);
            }
        }

        [TestCase("Created")]
        [TestCase("PendingDelete")]
        [TestCase("DeleteInProgress")]
        [TestCase("DeleteFailed")]
        [TestCase("DeleteError")]
        [TestCase("Deleted")]
        public void It_retains_linked_or_historical_public_values(string status)
        {
            Model(status).GetProperty("dataStoreId").GetInt32().Should().Be(101);
            Model(status).GetProperty("dataStoreName").GetString().Should().Be("school 1");
            Model(status).GetProperty("databaseName").GetString().Should().Be("EdFi_Ods_school_1_Minimal");
        }

        [Test]
        public void It_records_the_sourced_timestamp_event_mapping()
        {
            JsonElement[] events = Fixtures.GetProperty("timestampEvents").EnumerateArray().ToArray();
            events
                .Select(item => item.GetProperty("event").GetString())
                .Should()
                .BeEquivalentTo(
                    "createAcceptance",
                    "deleteAcceptance",
                    "inProgress",
                    "success",
                    "failure",
                    "terminalExhaustion"
                );
            foreach (JsonElement item in events)
            {
                item.GetProperty("lastModifiedDate").GetString().Should().Be("eventUtc");
                item.GetProperty("lastRefreshed")
                    .GetString()
                    .Should()
                    .Be(
                        item.GetProperty("event").GetString() == "deleteAcceptance" ? "unchanged" : "eventUtc"
                    );
            }
            Model("PendingDelete")
                .GetProperty("lastRefreshed")
                .GetDateTimeOffset()
                .Should()
                .Be(Model("Created").GetProperty("lastRefreshed").GetDateTimeOffset());
            Model("PendingDelete")
                .GetProperty("lastModifiedDate")
                .GetDateTimeOffset()
                .Should()
                .BeAfter(Model("Created").GetProperty("lastModifiedDate").GetDateTimeOffset());
        }

        [Test]
        public void It_records_bodyless_acceptance_and_absolute_managed_location()
        {
            JsonElement post = Fixtures.GetProperty("http").GetProperty("postAccepted");
            post.GetProperty("status").GetInt32().Should().Be(202);
            post.GetProperty("body").GetString().Should().BeEmpty();
            post.GetProperty("headers")
                .EnumerateObject()
                .Select(property => property.Name)
                .Should()
                .Equal("Location");
            post.GetProperty("headers")
                .GetProperty("Location")
                .GetString()
                .Should()
                .Be("https://cms.example.test:8443/cms/v3/dataStores/manage/7");

            JsonElement delete = Fixtures.GetProperty("http").GetProperty("deleteAccepted");
            delete.GetProperty("status").GetInt32().Should().Be(204);
            delete.GetProperty("body").GetString().Should().BeEmpty();
            delete.GetProperty("headers").EnumerateObject().Should().BeEmpty();
            Fixtures
                .GetProperty("http")
                .GetProperty("emptyCollection")
                .GetProperty("body")
                .EnumerateArray()
                .Should()
                .BeEmpty();

            foreach (JsonElement location in Fixtures.GetProperty("locations").EnumerateArray())
            {
                var uri = new Uri(location.GetProperty("manageLocation").GetString()!, UriKind.Absolute);
                uri.Scheme.Should().Be(location.GetProperty("scheme").GetString());
                uri.Authority.Should().Be(location.GetProperty("host").GetString());
                uri.AbsolutePath.Should().EndWith("/v3/dataStores/manage/7");
                uri.AbsolutePath.Should().NotContain("//");
            }
        }

        [Test]
        public void It_records_closed_readiness_as_a_safe_503_with_no_writes()
        {
            JsonElement response = Fixtures.GetProperty("http").GetProperty("closedReadiness");
            response.GetProperty("status").GetInt32().Should().Be(503);
            response.GetProperty("contentType").GetString().Should().Be("application/problem+json");
            response.GetProperty("headers").EnumerateObject().Should().BeEmpty();
            JsonElement body = response.GetProperty("body");
            body.EnumerateObject()
                .Select(property => property.Name)
                .Should()
                .BeEquivalentTo(
                    "detail",
                    "type",
                    "title",
                    "status",
                    "correlationId",
                    "validationErrors",
                    "errors"
                );
            body.GetProperty("type").GetString().Should().Be("urn:ed-fi:management-api:service-unavailable");
            body.GetProperty("title").GetString().Should().Be("Service Unavailable");
            body.GetProperty("detail")
                .GetString()
                .Should()
                .Be("Managed data store provisioning is unavailable.");
            body.GetProperty("status").GetInt32().Should().Be(503);
            body.GetProperty("validationErrors").EnumerateObject().Should().BeEmpty();
            body.GetProperty("errors").EnumerateArray().Should().BeEmpty();
            foreach (
                string counter in new[]
                {
                    "aggregateWrites",
                    "reservationWrites",
                    "jobWrites",
                    "lifecycleWrites",
                    "timestampWrites",
                    "linkWrites",
                    "physicalCalls",
                    "earlyPostSecretResolutions",
                }
            )
            {
                response.GetProperty(counter).GetInt32().Should().Be(0);
            }
        }

        [Test]
        public void It_records_the_complete_delete_state_and_readiness_matrix()
        {
            JsonElement[] cases = Fixtures.GetProperty("deleteStates").EnumerateArray().ToArray();
            cases
                .Select(item => item.GetProperty("state").GetString())
                .Should()
                .BeEquivalentTo(States.Append("absent"));
            foreach (JsonElement item in cases)
            {
                string state = item.GetProperty("state").GetString()!;
                int expected = state switch
                {
                    "Created" => 204,
                    "Deleted" or "absent" => 404,
                    _ => 400,
                };
                item.GetProperty("readyStatus").GetInt32().Should().Be(expected);
                item.GetProperty("closedStatus").GetInt32().Should().Be(state == "Created" ? 503 : expected);
            }
        }

        [TestCase(" school 1 ", "EdFi_Ods_school_1_Minimal")]
        [TestCase("__school__", "EdFi_Ods_school_Sample")]
        [TestCase("EDFI__ODS_edfi_ods_School", "EdFi_Ods_School_Minimal")]
        [TestCase("EdFi_Ods", "EdFi_Ods_Minimal")]
        [TestCase("_", "EdFi_Ods_Minimal")]
        [TestCase("edfi_odsX", "EdFi_Ods_edfi_odsX_Minimal")]
        public void It_preserves_the_reviewed_naming_examples(string name, string databaseName)
        {
            // Compare the frozen example, not the output of a test-only formatter.
            Fixtures
                .GetProperty("naming")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == name)
                .GetProperty("databaseName")
                .GetString()
                .Should()
                .Be(databaseName);
        }

        [Test]
        public void It_includes_the_46_character_boundary_and_physical_collision()
        {
            JsonElement boundary = Fixtures
                .GetProperty("naming")
                .EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == new string('a', 46));
            boundary
                .GetProperty("databaseName")
                .GetString()
                .Should()
                .Be($"EdFi_Ods_{new string('a', 46)}_Minimal")
                .And.HaveLength(63);
            Fixtures
                .GetProperty("invalidNames")
                .EnumerateArray()
                .Select(item => item.GetString())
                .Should()
                .Contain(new string('a', 47));
            JsonElement collision = Fixtures.GetProperty("physicalNameCollision");
            collision.GetProperty("status").GetInt32().Should().Be(400);
            collision.GetProperty("physicalCalls").GetInt32().Should().Be(0);
            collision
                .GetProperty("names")
                .EnumerateArray()
                .Select(item => item.GetString())
                .Should()
                .BeEquivalentTo("EdFi_Ods", "_");
        }

        [Test]
        public void It_preserves_managed_query_defaults_and_the_public_sort_whitelist()
        {
            JsonElement query = Fixtures.GetProperty("managedQuery");
            JsonElement defaults = query.GetProperty("defaults");
            defaults.GetProperty("offset").GetInt32().Should().Be(0);
            defaults.GetProperty("limit").GetInt32().Should().Be(25);
            defaults.GetProperty("orderBy").GetString().Should().Be("id");
            defaults.GetProperty("direction").GetString().Should().Be("Descending");
            query
                .GetProperty("orderBy")
                .EnumerateArray()
                .Select(item => item.GetString())
                .Should()
                .BeEquivalentTo(PublicFields);
            query.GetProperty("nullPlacement").GetString().Should().Be("last in both directions");
            query.GetProperty("tieBreaker").GetString().Should().Be("id in requested direction");
        }

        [Test]
        public void It_records_one_canonical_policy_and_valid_ordinary_result_references()
        {
            JsonElement names = Fixtures.GetProperty("canonicalNames");
            names
                .GetProperty("trimCodePoints")
                .EnumerateArray()
                .Select(item => item.GetInt32())
                .Should()
                .Equal(
                    9,
                    10,
                    11,
                    12,
                    13,
                    32,
                    133,
                    160,
                    5760,
                    8192,
                    8193,
                    8194,
                    8195,
                    8196,
                    8197,
                    8198,
                    8199,
                    8200,
                    8201,
                    8202,
                    8232,
                    8233,
                    8239,
                    8287,
                    12288
                );
            JsonElement[] pairs = names.GetProperty("pairs").EnumerateArray().ToArray();
            pairs
                .Single(item => item.GetProperty("left").GetString() == "Store")
                .GetProperty("managedPostWithExistingOrdinaryStatus")
                .GetInt32()
                .Should()
                .Be(400);
            pairs
                .Single(item => item.GetProperty("right").GetString() == "cafe")
                .GetProperty("managedPostWithExistingOrdinaryStatus")
                .GetInt32()
                .Should()
                .Be(202);

            JsonElement ordinary = Fixtures.GetProperty("ordinaryQuery");
            int[] ids = ordinary
                .GetProperty("rows")
                .EnumerateArray()
                .Select(row => row.GetProperty("id").GetInt32())
                .ToArray();
            ids.Should().OnlyHaveUniqueItems();
            foreach (JsonElement item in ordinary.GetProperty("cases").EnumerateArray())
            {
                int[] expected = item.GetProperty("expectedIds")
                    .EnumerateArray()
                    .Select(id => id.GetInt32())
                    .ToArray();
                expected.Should().OnlyHaveUniqueItems().And.BeSubsetOf(ids);
            }
        }
    }
}
