// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

public abstract class ProfileWriteReferenceFilterTests
{
    private static JsonNode ShapedBody() =>
        JsonNode.Parse(
            """
            {
                "calendarCode": "CAL-1",
                "schoolReference": { "schoolId": 255901107 },
                "gradeLevels": [
                    { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade" }
                ]
            }
            """
        )!;

    // -----------------------------------------------------------------------
    //  PathExists — concrete path resolution against a shaped body
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_PathExists : ProfileWriteReferenceFilterTests
    {
        [Test]
        public void It_returns_true_for_the_root_path() =>
            ProfileWriteReferenceFilter.PathExists(ShapedBody(), "$").Should().BeTrue();

        [Test]
        public void It_returns_true_for_a_present_top_level_member() =>
            ProfileWriteReferenceFilter.PathExists(ShapedBody(), "$.schoolReference").Should().BeTrue();

        [Test]
        public void It_returns_true_for_a_present_nested_member() =>
            ProfileWriteReferenceFilter
                .PathExists(ShapedBody(), "$.schoolReference.schoolId")
                .Should()
                .BeTrue();

        [Test]
        public void It_returns_true_for_a_present_array_element_member() =>
            ProfileWriteReferenceFilter
                .PathExists(ShapedBody(), "$.gradeLevels[0].gradeLevelDescriptor")
                .Should()
                .BeTrue();

        [Test]
        public void It_returns_false_for_an_absent_top_level_member() =>
            ProfileWriteReferenceFilter
                .PathExists(ShapedBody(), "$.schoolYearTypeReference")
                .Should()
                .BeFalse();

        [Test]
        public void It_returns_false_for_an_absent_nested_member() =>
            ProfileWriteReferenceFilter.PathExists(ShapedBody(), "$.schoolReference.foo").Should().BeFalse();

        [Test]
        public void It_returns_false_for_an_out_of_range_array_index() =>
            ProfileWriteReferenceFilter
                .PathExists(ShapedBody(), "$.gradeLevels[1].gradeLevelDescriptor")
                .Should()
                .BeFalse();
    }

    // -----------------------------------------------------------------------
    //  RetainPresent — drops references/descriptors absent from the shaped body
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_RetainPresent : ProfileWriteReferenceFilterTests
    {
        private static DescriptorReference DescriptorAt(string path) =>
            new(
                new BaseResourceInfo(
                    new ProjectName("ed-fi"),
                    new ResourceName("GradeLevelDescriptor"),
                    true
                ),
                new DocumentIdentity([]),
                new ReferentialId(Guid.Empty),
                new JsonPath(path)
            );

        private static DocumentReference DocumentReferenceAt(string path) =>
            new(
                new BaseResourceInfo(new ProjectName("ed-fi"), new ResourceName("School"), false),
                new DocumentIdentity([]),
                new ReferentialId(Guid.Empty),
                new JsonPath(path)
            );

        [Test]
        public void It_keeps_descriptor_references_present_in_the_shaped_body()
        {
            IReadOnlyList<DescriptorReference> input =
            [
                DescriptorAt("$.gradeLevels[0].gradeLevelDescriptor"),
                DescriptorAt("$.calendarTypeDescriptor"),
            ];

            var retained = ProfileWriteReferenceFilter.RetainPresent(input, ShapedBody());

            retained.Should().ContainSingle();
            retained[0].Path.Value.Should().Be("$.gradeLevels[0].gradeLevelDescriptor");
        }

        [Test]
        public void It_keeps_identity_document_references_and_drops_hidden_ones()
        {
            IReadOnlyList<DocumentReference> input =
            [
                DocumentReferenceAt("$.schoolReference"),
                DocumentReferenceAt("$.schoolYearTypeReference"),
            ];

            var retained = ProfileWriteReferenceFilter.RetainPresent(input, ShapedBody());

            retained.Should().ContainSingle();
            retained[0].Path.Value.Should().Be("$.schoolReference");
        }
    }
}
