// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DmsConfigurationService.Backend.EducationOrganizationProjection;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.EducationOrganizationProjection;

public class ProjectionResponseParserTests
{
    private const string Item =
        """{"educationOrganizationId":10,"nameOfInstitution":"N","shortNameOfInstitution":"S","discriminator":"edfi.School","parentId":1}""";

    /// <summary>What follows a name that ends a test's own text: the rest of a valid one-item page.</summary>
    private const string AfterName =
        "\",\"shortNameOfInstitution\":null,\"discriminator\":\"edfi.School\",\"parentId\":null}]}";

    /// <summary>A one-item page up to the opening quote's content of <c>nameOfInstitution</c>.</summary>
    private const string BeforeName =
        """{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"nameOfInstitution":"A""";

    private static ProjectionPage? Parse(string json) =>
        ProjectionResponseParser.Parse(Encoding.UTF8.GetBytes(json));

    [TestFixture]
    public class Given_the_contract_example_page
    {
        private ProjectionPage? _page;

        [SetUp]
        public void Setup() =>
            _page = ProjectionResponseParser.Parse(ProjectionContractExamples.Bytes("success-page.json"));

        [Test]
        public void It_reads_the_page_members() =>
            (_page!.ContractVersion, _page.DataStoreId, _page.NextCursor![..8])
                .Should()
                .Be(("educationOrganizationProjection.v1", 3788, "MSwzNzg4"));

        [Test]
        public void It_reads_every_item_with_its_nulls() =>
            _page!
                .Items.Should()
                .Equal(
                    new EducationOrganizationProjectionItem(
                        1,
                        "Example State Department of Education",
                        "ESDE",
                        "edfi.StateEducationAgency",
                        null
                    ),
                    new EducationOrganizationProjectionItem(
                        10,
                        "Region 10 Education Service Center",
                        "ESC 10",
                        "edfi.EducationServiceCenter",
                        1
                    )
                );
    }

    [TestFixture]
    public class Given_the_contract_example_last_page
    {
        private ProjectionPage? _page;

        [SetUp]
        public void Setup() =>
            _page = ProjectionResponseParser.Parse(
                ProjectionContractExamples.Bytes("success-last-page.json")
            );

        [Test]
        public void It_reads_a_null_cursor_and_null_item_members() =>
            (_page!.NextCursor, _page.Items.Single().ShortNameOfInstitution, _page.Items.Single().ParentId)
                .Should()
                .Be((null, null, null));
    }

    [TestFixture]
    public class Given_values_at_the_edges_of_their_types
    {
        private ProjectionPage? _page;

        [SetUp]
        public void Setup() =>
            _page = Parse(
                """
                {"contractVersion":"v","dataStoreId":2147483647,"nextCursor":null,"items":[
                {"educationOrganizationId":-9223372036854775808,"nameOfInstitution":"N","shortNameOfInstitution":"","discriminator":"x","parentId":9223372036854775807},
                {"educationOrganizationId":0,"nameOfInstitution":"","shortNameOfInstitution":null,"discriminator":"","parentId":0}]}
                """
            );

        [Test]
        public void It_reads_the_int64_and_int32_extremes() =>
            (
                _page!.DataStoreId,
                _page.Items[0].EducationOrganizationId,
                _page.Items[0].ParentId,
                _page.Items[1].EducationOrganizationId
            )
                .Should()
                .Be((int.MaxValue, long.MinValue, long.MaxValue, 0L));

        [Test]
        public void It_leaves_value_rules_to_the_reader() =>
            (
                _page!.Items[0].ShortNameOfInstitution,
                _page.Items[1].NameOfInstitution,
                _page.Items[1].Discriminator
            )
                .Should()
                .Be(("", "", ""));
    }

    [TestFixture]
    public class Given_a_supplementary_character
    {
        // U+1F600 as one escaped surrogate pair and as raw UTF-8 bytes.
        private ProjectionPage? _escaped;
        private ProjectionPage? _raw;

        [SetUp]
        public void Setup()
        {
            _escaped = Parse(
                """{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"nameOfInstitution":"A😀","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":null}]}"""
            );
            _raw = Parse(BeforeName + char.ConvertFromUtf32(0x1F600) + AfterName);
        }

        [Test]
        public void It_keeps_the_escaped_pair() =>
            _escaped!.Items.Single().NameOfInstitution.Should().Be("A" + char.ConvertFromUtf32(0x1F600));

        [Test]
        public void It_keeps_the_raw_character() =>
            _raw!.Items.Single().NameOfInstitution.Should().Be("A" + char.ConvertFromUtf32(0x1F600));
    }

    [TestFixture(
        "unknown page member",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[],"extra":1}"""
    )]
    [TestFixture(
        "unknown item member",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"nameOfInstitution":"N","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":null,"extra":1}]}"""
    )]
    [TestFixture(
        "duplicate page member",
        $$"""{"contractVersion":"v","dataStoreId":1,"dataStoreId":1,"nextCursor":null,"items":[]}"""
    )]
    [TestFixture(
        "duplicate item member",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"educationOrganizationId":1,"nameOfInstitution":"N","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":null}]}"""
    )]
    [TestFixture(
        "member name in another case",
        $$"""{"ContractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[]}"""
    )]
    [TestFixture("missing contractVersion", $$"""{"dataStoreId":1,"nextCursor":null,"items":[]}""")]
    [TestFixture("missing dataStoreId", $$"""{"contractVersion":"v","nextCursor":null,"items":[]}""")]
    [TestFixture("missing nextCursor", $$"""{"contractVersion":"v","dataStoreId":1,"items":[]}""")]
    [TestFixture("missing items", $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null}""")]
    [TestFixture(
        "missing educationOrganizationId",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"nameOfInstitution":"N","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":null}]}"""
    )]
    [TestFixture(
        "missing nameOfInstitution",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":null}]}"""
    )]
    [TestFixture(
        "missing shortNameOfInstitution",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"nameOfInstitution":"N","discriminator":"edfi.School","parentId":null}]}"""
    )]
    [TestFixture(
        "missing discriminator",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"nameOfInstitution":"N","shortNameOfInstitution":null,"parentId":null}]}"""
    )]
    [TestFixture(
        "missing parentId",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"nameOfInstitution":"N","shortNameOfInstitution":null,"discriminator":"edfi.School"}]}"""
    )]
    [TestFixture(
        "null contractVersion",
        $$"""{"contractVersion":null,"dataStoreId":1,"nextCursor":null,"items":[]}"""
    )]
    [TestFixture(
        "null dataStoreId",
        $$"""{"contractVersion":"v","dataStoreId":null,"nextCursor":null,"items":[]}"""
    )]
    [TestFixture(
        "null items",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":null}"""
    )]
    [TestFixture(
        "null item",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{{Item}},null]}"""
    )]
    [TestFixture(
        "null educationOrganizationId",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":null,"nameOfInstitution":"N","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":null}]}"""
    )]
    [TestFixture(
        "null nameOfInstitution",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"nameOfInstitution":null,"shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":null}]}"""
    )]
    [TestFixture(
        "null discriminator",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"nameOfInstitution":"N","shortNameOfInstitution":null,"discriminator":null,"parentId":null}]}"""
    )]
    [TestFixture(
        "empty nextCursor",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":"","items":[{{Item}}]}"""
    )]
    [TestFixture(
        "nextCursor not a string",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":7,"items":[{{Item}}]}"""
    )]
    [TestFixture(
        "dataStoreId as a string",
        $$"""{"contractVersion":"v","dataStoreId":"1","nextCursor":null,"items":[]}"""
    )]
    [TestFixture(
        "dataStoreId beyond int32",
        $$"""{"contractVersion":"v","dataStoreId":2147483648,"nextCursor":null,"items":[]}"""
    )]
    [TestFixture(
        "id as a string",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":"1","nameOfInstitution":"N","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":null}]}"""
    )]
    [TestFixture(
        "id with a fraction",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1.5,"nameOfInstitution":"N","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":null}]}"""
    )]
    [TestFixture(
        "id beyond int64",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":9223372036854775808,"nameOfInstitution":"N","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":null}]}"""
    )]
    [TestFixture(
        "parentId as a string",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"nameOfInstitution":"N","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":"2"}]}"""
    )]
    [TestFixture(
        "shortNameOfInstitution a number",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"nameOfInstitution":"N","shortNameOfInstitution":5,"discriminator":"edfi.School","parentId":null}]}"""
    )]
    [TestFixture(
        "items an object",
        """{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":{}}"""
    )]
    [TestFixture(
        "item an array",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[[]]}"""
    )]
    [TestFixture(
        "lone high surrogate",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"nameOfInstitution":"A\ud800","shortNameOfInstitution":null,"discriminator":"edfi.School","parentId":null}]}"""
    )]
    [TestFixture(
        "lone low surrogate",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[{"educationOrganizationId":1,"nameOfInstitution":"N","shortNameOfInstitution":"\udc00A","discriminator":"edfi.School","parentId":null}]}"""
    )]
    [TestFixture(
        "comment",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[] /* c */}"""
    )]
    [TestFixture(
        "trailing comma",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[],}"""
    )]
    [TestFixture(
        "content after the root",
        $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[]} {}"""
    )]
    [TestFixture("truncated", $$"""{"contractVersion":"v","dataStoreId":1,"nextCursor":null,"items":[""")]
    [TestFixture("root array", "[]")]
    [TestFixture("root null", "null")]
    [TestFixture("empty body", "")]
    public class Given_a_page_that_breaks_a_rule(string rule, string json)
    {
        private ProjectionPage? _page;

        [SetUp]
        public void Setup() => _page = Parse(json);

        [Test]
        public void It_is_not_a_page() => _page.Should().BeNull(rule);
    }

    [TestFixture]
    public class Given_invalid_utf8_inside_a_string
    {
        private ProjectionPage? _page;

        [SetUp]
        public void Setup()
        {
            _page = ProjectionResponseParser.Parse([
                .. Encoding.UTF8.GetBytes(BeforeName),
                0xC3,
                0x28,
                .. Encoding.UTF8.GetBytes(AfterName),
            ]);
        }

        [Test]
        public void It_is_not_a_page() => _page.Should().BeNull();
    }
}
