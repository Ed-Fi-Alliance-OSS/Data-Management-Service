// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Validation;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

/// <summary>
/// Which declared query fields the detector reports as colliding with a reserved query parameter, and
/// which it leaves alone.
/// </summary>
/// <remarks>
/// The per-name case is driven from <see cref="ReservedQueryParameters.All"/> rather than a list
/// written here, so a name added to the catalog is covered by this suite without anyone remembering
/// to extend it. That is the property that makes the defect unable to recur: a ninth reserved name
/// cannot be added without a test proving a schema declaring it is refused.
/// </remarks>
[TestFixture]
[Parallelizable]
public class ReservedQueryParameterCollisionDetectorTests
{
    private const string SchemaSource = "extension[0]";

    /// <summary>The resource a case uses when the resource itself is not the subject.</summary>
    private const string ResourceName = "AcademicWeek";

    public static IEnumerable<string> AllReservedNames() =>
        ReservedQueryParameters.All.Select(reserved => reserved.Name);

    /// <summary>
    /// A schema whose single resource declares the given query fields, built the way the production
    /// loader receives one.
    /// </summary>
    private static JsonNode SchemaDeclaring(string resourceName, params string[] queryFieldNames)
    {
        ApiSchemaBuilder builder = new ApiSchemaBuilder()
            .WithStartProject()
            .WithStartResource(resourceName)
            .WithStartQueryFieldMapping();

        foreach (string queryFieldName in queryFieldNames)
        {
            builder = builder.WithQueryField(queryFieldName, [new($"$.{queryFieldName}", "string")]);
        }

        return builder
            .WithEndQueryFieldMapping()
            .WithEndResource()
            .WithEndProject()
            .AsSingleApiSchemaRootNode();
    }

    private static IReadOnlyList<ApiSchemaNormalizationResult.ReservedQueryParameterCollision> Detect(
        JsonNode schemaNode
    ) => ReservedQueryParameterCollisionDetector.Detect(schemaNode, SchemaSource);

    [TestFixture]
    [Parallelizable]
    public class Given_A_Resource_Declaring_A_Reserved_Name : ReservedQueryParameterCollisionDetectorTests
    {
        [TestCaseSource(typeof(ReservedQueryParameterCollisionDetectorTests), nameof(AllReservedNames))]
        public void It_reports_every_reserved_name_in_the_catalog(string reservedName)
        {
            Detect(SchemaDeclaring(ResourceName, reservedName))
                .Should()
                .ContainSingle()
                .Which.Reserved.Name.Should()
                .Be(reservedName);
        }

        [Test]
        public void It_carries_the_schema_source_the_caller_supplied()
        {
            Detect(SchemaDeclaring(ResourceName, "pageSize"))
                .Should()
                .ContainSingle()
                .Which.SchemaSource.Should()
                .Be(SchemaSource);
        }

        [Test]
        public void It_names_the_declaring_project_and_resource()
        {
            var collision = Detect(SchemaDeclaring("Student", "pageToken")).Should().ContainSingle().Subject;

            collision.ProjectEndpointName.Should().Be("ed-fi");
            collision.ResourceEndpointName.Should().Be("students");
        }

        [Test]
        public void It_reports_the_field_name_as_the_schema_spells_it()
        {
            Detect(SchemaDeclaring(ResourceName, "PageSize"))
                .Should()
                .ContainSingle()
                .Which.QueryFieldName.Should()
                .Be("PageSize", "the reader has to find the property in the file");
        }

        [TestCase("PAGETOKEN")]
        [TestCase("MinChangeVersion")]
        [TestCase("Number")]
        [TestCase("tOtAlCoUnT")]
        public void It_detects_a_reserved_name_declared_in_another_case(string declaredName)
        {
            Detect(SchemaDeclaring(ResourceName, declaredName))
                .Should()
                .ContainSingle(
                    "a query field is matched against a supplied key case-insensitively, so it "
                        + "collides whichever case it is declared in"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Resource_Declaring_Ordinary_Names : ReservedQueryParameterCollisionDetectorTests
    {
        [TestCase("numberOfPartitions")]
        [TestCase("limits")]
        [TestCase("pageTokens")]
        [TestCase("minChange")]
        [TestCase("schoolId")]
        [TestCase("id")]
        [TestCase("beginDate")]
        [TestCase("offsetHours")]
        public void It_reports_nothing(string queryFieldName)
        {
            Detect(SchemaDeclaring(ResourceName, queryFieldName))
                .Should()
                .BeEmpty("only an exact name, ignoring case, is consumed as a control parameter");
        }

        [Test]
        public void It_reports_nothing_for_a_resource_with_no_query_fields()
        {
            Detect(SchemaDeclaring(ResourceName)).Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Several_Collisions : ReservedQueryParameterCollisionDetectorTests
    {
        [Test]
        public void It_reports_every_colliding_field_of_one_resource()
        {
            Detect(SchemaDeclaring(ResourceName, "pageSize", "schoolId", "number", "minChangeVersion"))
                .Select(collision => collision.QueryFieldName)
                .Should()
                .Equal("minChangeVersion", "number", "pageSize");
        }

        [Test]
        public void It_orders_fields_ordinally_whatever_order_the_document_writes_them()
        {
            string[] declaredOneWay =
            [
                .. Detect(SchemaDeclaring(ResourceName, "totalCount", "limit", "offset"))
                    .Select(collision => collision.QueryFieldName),
            ];

            string[] declaredAnother =
            [
                .. Detect(SchemaDeclaring(ResourceName, "offset", "totalCount", "limit"))
                    .Select(collision => collision.QueryFieldName),
            ];

            declaredOneWay
                .Should()
                .Equal(
                    declaredAnother,
                    "a diagnostic a reader compares against a previous run must not change because a "
                        + "generator emitted its keys differently"
                );

            declaredOneWay.Should().Equal("limit", "offset", "totalCount");
        }

        [Test]
        public void It_reports_collisions_across_resources_in_resource_order()
        {
            JsonNode schema = new ApiSchemaBuilder()
                .WithStartProject()
                .WithStartResource("Student")
                .WithStartQueryFieldMapping()
                .WithQueryField("pageSize", [new("$.pageSize", "string")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithStartResource("AcademicWeek")
                .WithStartQueryFieldMapping()
                .WithQueryField("limit", [new("$.limit", "string")])
                .WithEndQueryFieldMapping()
                .WithEndResource()
                .WithEndProject()
                .AsSingleApiSchemaRootNode();

            Detect(schema)
                .Select(collision => (collision.ResourceEndpointName, collision.QueryFieldName))
                .Should()
                .Equal(("academicWeeks", "limit"), ("students", "pageSize"));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Node_Of_An_Unexpected_Shape : ReservedQueryParameterCollisionDetectorTests
    {
        private static IEnumerable<TestCaseData> MalformedNodes()
        {
            yield return new TestCaseData(new JsonObject()).SetName("{m}(no projectSchema)");

            yield return new TestCaseData(new JsonObject { ["projectSchema"] = "not an object" }).SetName(
                "{m}(projectSchema is not an object)"
            );

            yield return new TestCaseData(new JsonObject { ["projectSchema"] = new JsonObject() }).SetName(
                "{m}(no resourceSchemas)"
            );

            yield return new TestCaseData(
                new JsonObject
                {
                    ["projectSchema"] = new JsonObject { ["resourceSchemas"] = new JsonArray() },
                }
            ).SetName("{m}(resourceSchemas is not an object)");

            yield return new TestCaseData(
                new JsonObject
                {
                    ["projectSchema"] = new JsonObject
                    {
                        ["resourceSchemas"] = new JsonObject { ["students"] = new JsonObject() },
                    },
                }
            ).SetName("{m}(resource has no queryFieldMapping)");

            yield return new TestCaseData(
                new JsonObject
                {
                    ["projectSchema"] = new JsonObject
                    {
                        ["resourceSchemas"] = new JsonObject
                        {
                            ["students"] = new JsonObject { ["queryFieldMapping"] = "not an object" },
                        },
                    },
                }
            ).SetName("{m}(queryFieldMapping is not an object)");
        }

        [TestCaseSource(nameof(MalformedNodes))]
        public void It_reports_nothing_rather_than_throwing(JsonNode schemaNode)
        {
            Detect(schemaNode)
                .Should()
                .BeEmpty(
                    "structure is the JSON Schema validator's subject, and throwing here would replace "
                        + "its message with a stack trace naming this file"
                );
        }

        [Test]
        public void It_still_reports_a_collision_when_the_project_endpoint_name_is_missing()
        {
            JsonNode schemaNode = new JsonObject
            {
                ["projectSchema"] = new JsonObject
                {
                    ["resourceSchemas"] = new JsonObject
                    {
                        ["students"] = new JsonObject
                        {
                            ["queryFieldMapping"] = new JsonObject { ["pageSize"] = new JsonArray() },
                        },
                    },
                },
            };

            var collision = Detect(schemaNode).Should().ContainSingle().Subject;

            collision.ProjectEndpointName.Should().BeEmpty();
            collision.ResourceEndpointName.Should().Be("students");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Null_Arguments : ReservedQueryParameterCollisionDetectorTests
    {
        [Test]
        public void It_rejects_a_null_schema_node()
        {
            Action act = () => ReservedQueryParameterCollisionDetector.Detect(null!, SchemaSource);

            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void It_rejects_a_null_schema_source()
        {
            Action act = () =>
                ReservedQueryParameterCollisionDetector.Detect(
                    SchemaDeclaring(ResourceName, "pageSize"),
                    null!
                );

            act.Should().Throw<ArgumentNullException>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Shipped_Data_Standard_Shape : ReservedQueryParameterCollisionDetectorTests
    {
        /// <summary>
        /// The query fields a real resource declares. No shipped Ed-Fi schema collides, which is what
        /// makes refusing a collision at load safe, so the detector must stay silent on this shape.
        /// </summary>
        [Test]
        public void It_reports_nothing_for_ordinary_resource_query_fields()
        {
            Detect(
                    SchemaDeclaring(
                        "AcademicWeek",
                        "schoolId",
                        "weekIdentifier",
                        "beginDate",
                        "endDate",
                        "totalInstructionalDays",
                        "id"
                    )
                )
                .Should()
                .BeEmpty();
        }
    }
}
