// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_IsDirectMirroredCollectionExtensionScopeChild_for_a_real_mirrored_pair
{
    [Test]
    public void It_returns_true_for_addresses_with_sample_mirror() =>
        ProfileCollectionWalker
            .IsDirectMirroredCollectionExtensionScopeChild(
                "$.addresses[*]",
                "$._ext.sample.addresses[*]._ext.sample"
            )
            .Should()
            .BeTrue();
}

[TestFixture]
public class Given_IsDirectMirroredCollectionExtensionScopeChild_for_a_non_mirrored_extension
{
    [Test]
    public void It_returns_false_for_a_directly_appended_aligned_extension() =>
        ProfileCollectionWalker
            .IsDirectMirroredCollectionExtensionScopeChild("$.addresses[*]", "$.addresses[*]._ext.sample")
            .Should()
            .BeFalse();
}

[TestFixture]
public class Given_IsDirectMirroredCollectionExtensionScopeChild_for_mismatched_extension_names
{
    [Test]
    public void It_returns_false_when_leading_and_trailing_extension_names_differ() =>
        ProfileCollectionWalker
            .IsDirectMirroredCollectionExtensionScopeChild(
                "$.addresses[*]",
                "$._ext.sample.addresses[*]._ext.other"
            )
            .Should()
            .BeFalse();
}

[TestFixture]
public class Given_IsDirectMirroredCollectionExtensionScopeChild_for_unrelated_parent
{
    [Test]
    public void It_returns_false_when_the_middle_does_not_match_the_parent_remainder() =>
        ProfileCollectionWalker
            .IsDirectMirroredCollectionExtensionScopeChild(
                "$.addresses[*]",
                "$._ext.sample.programs[*]._ext.sample"
            )
            .Should()
            .BeFalse();
}

[TestFixture]
public class Given_IsDirectMirroredCollectionExtensionScopeChild_for_root_parent
{
    [Test]
    public void It_returns_false_because_root_is_not_a_collection_parent() =>
        ProfileCollectionWalker
            .IsDirectMirroredCollectionExtensionScopeChild("$", "$._ext.sample.addresses[*]._ext.sample")
            .Should()
            .BeFalse();
}

[TestFixture]
public class Given_StripAlignedScopeToParentCollectionScope_for_standard_aligned_scope
{
    [Test]
    public void It_strips_the_trailing_ext_marker_to_yield_the_parent_collection_scope() =>
        ProfileCollectionWalker
            .StripAlignedScopeToParentCollectionScope("$.parents[*]._ext.sample")
            .Should()
            .Be("$.parents[*]");
}

[TestFixture]
public class Given_StripAlignedScopeToParentCollectionScope_for_mirrored_aligned_scope
{
    [Test]
    public void It_unwraps_the_mirrored_pattern_to_the_base_collection_scope() =>
        ProfileCollectionWalker
            .StripAlignedScopeToParentCollectionScope("$._ext.sample.addresses[*]._ext.sample")
            .Should()
            .Be("$.addresses[*]");
}

[TestFixture]
public class Given_StripAlignedScopeToParentCollectionScope_for_a_non_aligned_scope
{
    [Test]
    public void It_returns_null_when_the_trailing_ext_marker_is_absent() =>
        ProfileCollectionWalker
            .StripAlignedScopeToParentCollectionScope("$.parents[*].kids[*]")
            .Should()
            .BeNull();
}

[TestFixture]
public class Given_TryNavigateConcreteNode_for_a_mirrored_aligned_scope_against_a_request_body
{
    private JsonNode? _resolved;
    private bool _ok;

    [SetUp]
    public void Setup()
    {
        var body = new JsonObject
        {
            ["addresses"] = new JsonArray(
                new JsonObject { ["street"] = "Base 0" },
                new JsonObject { ["street"] = "Base 1" }
            ),
            ["_ext"] = new JsonObject
            {
                ["sample"] = new JsonObject
                {
                    ["addresses"] = new JsonArray(
                        new JsonObject
                        {
                            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["zone"] = "ZoneA" } },
                        },
                        new JsonObject
                        {
                            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["zone"] = "ZoneB" } },
                        }
                    ),
                },
            },
        };

        var alignedScope = new JsonPathExpression(
            "$._ext.sample.addresses[*]._ext.sample",
            [
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample"),
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("_ext"),
                new JsonPathSegment.Property("sample"),
            ]
        );

        _ok = RelationalWriteFlattener.TryNavigateConcreteNode(
            body,
            RelationalJsonPathSupport.GetRestrictedSegments(alignedScope),
            new[] { 1 },
            out _resolved
        );
    }

    [Test]
    public void It_resolves_the_mirrored_node_at_the_requested_ordinal() => _ok.Should().BeTrue();

    [Test]
    public void It_returns_the_zone_value_for_the_targeted_address() =>
        _resolved!["zone"]!.GetValue<string>().Should().Be("ZoneB");
}
