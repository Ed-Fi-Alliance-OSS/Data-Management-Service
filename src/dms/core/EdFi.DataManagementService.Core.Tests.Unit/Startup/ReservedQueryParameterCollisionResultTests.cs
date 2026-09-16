// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Validation;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Startup;

/// <summary>
/// What an operator is told when a schema declares a query field spelled like a reserved query
/// parameter. This text is both thrown as a startup failure and written to the log, so it is the whole
/// of what the person who must fix the model gets to see.
/// </summary>
[TestFixture]
[Parallelizable]
public class ReservedQueryParameterCollisionResultTests
{
    private static ReservedQueryParameter Reserved(string name) =>
        ReservedQueryParameters.All.Single(reserved =>
            string.Equals(reserved.Name, name, StringComparison.Ordinal)
        );

    private static ApiSchemaNormalizationResult.ReservedQueryParameterCollision Collision(
        string schemaSource = "extension[0]",
        string projectEndpointName = "cursorpartitionext",
        string resourceEndpointName = "partitionContractItems",
        string queryFieldName = "number",
        string reservedName = "number"
    ) => new(schemaSource, projectEndpointName, resourceEndpointName, queryFieldName, Reserved(reservedName));

    private static ApiSchemaNormalizationResult.ReservedQueryParameterCollisionResult Result(
        params ApiSchemaNormalizationResult.ReservedQueryParameterCollision[] collisions
    ) => new(collisions);

    [TestFixture]
    [Parallelizable]
    public class Given_One_Collision : ReservedQueryParameterCollisionResultTests
    {
        private string _description = string.Empty;

        [SetUp]
        public void Setup()
        {
            _description = Result(Collision()).Describe();
        }

        [Test]
        public void It_names_the_schema_the_resource_and_the_field()
        {
            _description
                .Should()
                .Contain(
                    "Schema 'extension[0]' resource 'cursorpartitionext/partitionContractItems' "
                        + "declares query field 'number'"
                );
        }

        [Test]
        public void It_says_what_the_name_is_reserved_for()
        {
            _description.Should().Contain("which DMS reserves as the partition count on /partitions.");
        }

        [Test]
        public void It_counts_the_collisions_in_the_singular()
        {
            _description
                .Should()
                .StartWith("1 resource query field collides with a query parameter name DMS reserves.");
        }

        [Test]
        public void It_tells_the_reader_what_to_do_about_it()
        {
            _description
                .Should()
                .EndWith(
                    "Rename the colliding property in the MetaEd model and rebuild the ApiSchema. A "
                        + "reserved name is consumed as a control parameter before resource filters are "
                        + "matched on at least one route the resource exposes, so the field would not be "
                        + "served as its schema declares it."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Name_Reserved_On_Every_Operation : ReservedQueryParameterCollisionResultTests
    {
        [Test]
        public void It_lists_every_operation_in_catalog_order()
        {
            Result(Collision(queryFieldName: "pageSize", reservedName: "pageSize"))
                .Describe()
                .Should()
                .Contain(
                    "which DMS reserves as the cursor paging page size on the resource and descriptor "
                        + "collection GET, /partitions, and the Change Query /deletes and /keyChanges "
                        + "endpoints."
                );
        }

        [Test]
        public void It_joins_two_operations_without_a_comma()
        {
            ReservedQueryParameter twoOperations = new(
                "invented",
                ReservedQueryParameterOperations.CollectionGet | ReservedQueryParameterOperations.Partitions,
                ReservedQueryParameterMatching.Ordinal,
                "invented control"
            );

            Result(
                    new ApiSchemaNormalizationResult.ReservedQueryParameterCollision(
                        "core",
                        "ed-fi",
                        "students",
                        "invented",
                        twoOperations
                    )
                )
                .Describe()
                .Should()
                .Contain("on the resource and descriptor collection GET and /partitions.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Several_Collisions : ReservedQueryParameterCollisionResultTests
    {
        private string _description = string.Empty;

        [SetUp]
        public void Setup()
        {
            _description = Result(
                    Collision(
                        schemaSource: "core",
                        projectEndpointName: "ed-fi",
                        resourceEndpointName: "students",
                        queryFieldName: "pageToken",
                        reservedName: "pageToken"
                    ),
                    Collision()
                )
                .Describe();
        }

        [Test]
        public void It_counts_them_in_the_plural()
        {
            _description
                .Should()
                .StartWith("2 resource query fields collide with query parameter names DMS reserves.");
        }

        [Test]
        public void It_reports_every_collision_on_its_own_line()
        {
            _description
                .Split('\n')
                .Where(line => line.StartsWith("  - ", StringComparison.Ordinal))
                .Should()
                .HaveCount(2);
        }

        [Test]
        public void It_reports_them_in_the_order_they_were_found()
        {
            _description
                .IndexOf("'pageToken'", StringComparison.Ordinal)
                .Should()
                .BeLessThan(
                    _description.IndexOf("'number'", StringComparison.Ordinal),
                    "the detector decides the order, and the description must not reorder it"
                );
        }

        [Test]
        public void It_pluralizes_the_remediation()
        {
            _description.Should().Contain("Rename the colliding properties in the MetaEd model");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Field_Declared_In_Another_Case : ReservedQueryParameterCollisionResultTests
    {
        [Test]
        public void It_reports_the_declared_spelling_and_the_reserved_purpose()
        {
            string description = Result(Collision(queryFieldName: "PageSize", reservedName: "pageSize"))
                .Describe();

            description
                .Should()
                .Contain(
                    "declares query field 'PageSize'",
                    "the reader has to find the property in the file, which spells it this way"
                );

            description.Should().Contain("the cursor paging page size");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Schema_Supplied_Text_Carrying_Control_Characters
        : ReservedQueryParameterCollisionResultTests
    {
        private string _description = string.Empty;

        [SetUp]
        public void Setup()
        {
            _description = Result(
                    Collision(
                        projectEndpointName: "ed-fi\nFATAL injected",
                        resourceEndpointName: "students\r\n",
                        queryFieldName: "num\tber"
                    )
                )
                .Describe();
        }

        [Test]
        public void It_strips_the_control_characters_from_every_schema_supplied_fragment()
        {
            _description
                .Split('\n')
                .Should()
                .HaveCount(
                    3,
                    "the only newlines are the ones this description writes: one before the single "
                        + "collision line and one before the remediation"
                );

            _description.Should().NotContain("\r").And.NotContain("\t");
        }

        [Test]
        public void It_keeps_the_surrounding_message_intact()
        {
            _description.Should().Contain("which DMS reserves as the partition count on /partitions.");
        }

        [Test]
        public void It_does_not_let_injected_text_pose_as_a_log_line()
        {
            _description.Should().NotContain("FATAL injected\n");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Loader_Composed_Schema_Source : ReservedQueryParameterCollisionResultTests
    {
        [Test]
        public void It_reports_the_extension_index_verbatim()
        {
            Result(Collision(schemaSource: "extension[3]"))
                .Describe()
                .Should()
                .Contain(
                    "Schema 'extension[3]'",
                    "the brackets name which loaded extension to open, and the log sanitizer's "
                        + "whitelist would strip them"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Operation_Description_Table : ReservedQueryParameterCollisionResultTests
    {
        [Test]
        public void It_describes_every_declared_operation()
        {
            List<ReservedQueryParameterOperations> undescribed = [];

            foreach (
                ReservedQueryParameterOperations operation in Enum.GetValues<ReservedQueryParameterOperations>()
            )
            {
                if (operation == ReservedQueryParameterOperations.None)
                {
                    continue;
                }

                ReservedQueryParameter single = new(
                    "invented",
                    operation,
                    ReservedQueryParameterMatching.Ordinal,
                    "invented control"
                );

                if (
                    Result(
                            new ApiSchemaNormalizationResult.ReservedQueryParameterCollision(
                                "core",
                                "ed-fi",
                                "students",
                                "invented",
                                single
                            )
                        )
                        .Describe()
                        .Contains(operation.ToString(), StringComparison.Ordinal)
                )
                {
                    undescribed.Add(operation);
                }
            }

            undescribed
                .Should()
                .BeEmpty(
                    "an operation the table does not describe falls back to its flag name, which would "
                        + "tell an operator less than the refusal requires"
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Result_Type : ReservedQueryParameterCollisionResultTests
    {
        [Test]
        public void It_is_a_normalization_result()
        {
            Result(Collision()).Should().BeAssignableTo<ApiSchemaNormalizationResult>();
        }

        [Test]
        public void It_carries_the_collisions_it_was_given()
        {
            var collision = Collision();

            Result(collision).Collisions.Should().Equal(collision);
        }

        [Test]
        public void It_spells_the_reserved_name_from_the_catalog()
        {
            Collision()
                .Reserved.Name.Should()
                .Be(
                    PartitionRequestValidator.NumberParameter,
                    "the message must name the parameter the request pipeline really reserves"
                );
        }
    }
}
