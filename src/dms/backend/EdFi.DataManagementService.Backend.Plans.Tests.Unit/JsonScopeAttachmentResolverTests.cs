// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_JsonScopeAttachmentResolver
{
    [Test]
    public void It_should_parse_canonical_only_scope_segments()
    {
        var segments = JsonScopeAttachmentResolver.GetRestrictedSegments(
            Path("$.addresses[*].telephoneNumbers[*]")
        );

        segments
            .Should()
            .Equal(
                new JsonPathSegment.Property("addresses"),
                new JsonPathSegment.AnyArrayElement(),
                new JsonPathSegment.Property("telephoneNumbers"),
                new JsonPathSegment.AnyArrayElement()
            );
    }

    [Test]
    public void It_should_prefer_explicit_segments_over_canonical_parsing()
    {
        var segments = JsonScopeAttachmentResolver.GetRestrictedSegments(
            new JsonPathExpression("$.addresses[*]", [new JsonPathSegment.Property("explicitSegment")])
        );

        segments.Should().Equal(new JsonPathSegment.Property("explicitSegment"));
    }

    [Test]
    public void It_should_reject_empty_canonical_path()
    {
        Action act = () => JsonScopeAttachmentResolver.GetRestrictedSegments(Path(""));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Restricted JSONPath '' must start with '$'.");
    }

    [Test]
    public void It_should_reject_unsupported_jsonpath_token()
    {
        Action act = () => JsonScopeAttachmentResolver.GetRestrictedSegments(Path("$.addresses[0]"));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Restricted JSONPath '$.addresses[0]' contains unsupported token at position 11.");
    }

    [TestCase("$[")]
    [TestCase("$[*")]
    public void It_should_reject_incomplete_array_wildcard(string canonicalPath)
    {
        Action act = () => JsonScopeAttachmentResolver.GetRestrictedSegments(Path(canonicalPath));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage($"Restricted JSONPath '{canonicalPath}' contains unsupported token at position 1.");
    }

    [Test]
    public void It_should_reject_empty_property_segment()
    {
        Action act = () => JsonScopeAttachmentResolver.GetRestrictedSegments(Path("$.addresses[*]."));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Restricted JSONPath '$.addresses[*].' contains an empty property segment.");
    }

    [Test]
    public void It_should_format_restricted_scope_segments()
    {
        var canonical = JsonScopeAttachmentResolver.FormatScope([
            new JsonPathSegment.Property("addresses"),
            new JsonPathSegment.AnyArrayElement(),
            new JsonPathSegment.Property("telephoneNumbers"),
            new JsonPathSegment.AnyArrayElement(),
        ]);

        canonical.Should().Be("$.addresses[*].telephoneNumbers[*]");
    }

    [Test]
    public void It_should_reject_unsupported_scope_segment_when_formatting()
    {
        Action act = () => JsonScopeAttachmentResolver.FormatScope([new UnsupportedSegment()]);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Unsupported restricted JsonPath segment.");
    }

    [Test]
    public void It_should_report_segments_as_unequal_when_kinds_differ()
    {
        var segmentsAreEqual = JsonScopeAttachmentResolver.AreSegmentsEqual(
            [new JsonPathSegment.Property("addresses")],
            [new JsonPathSegment.AnyArrayElement()]
        );

        segmentsAreEqual.Should().BeFalse();
    }

    [Test]
    public void It_should_report_unknown_segment_types_as_unequal()
    {
        var segmentsAreEqual = JsonScopeAttachmentResolver.AreSegmentsEqual(
            [new UnsupportedSegment()],
            [new UnsupportedSegment()]
        );

        segmentsAreEqual.Should().BeFalse();
    }

    [Test]
    public void It_should_resolve_collection_immediate_parent_scope()
    {
        var parentScopeSegments =
            JsonScopeAttachmentResolver.ResolveExpectedImmediateParentScopeSegmentsOrThrow(
                Path("$.addresses[*].telephoneNumbers[*]"),
                DbTableKind.Collection
            );

        JsonScopeAttachmentResolver.FormatScope(parentScopeSegments).Should().Be("$.addresses[*]");
    }

    [Test]
    public void It_should_reject_collection_scope_without_an_immediate_parent()
    {
        Action act = () =>
            JsonScopeAttachmentResolver.ResolveExpectedImmediateParentScopeSegmentsOrThrow(
                Path("$"),
                DbTableKind.Collection
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot resolve immediate parent scope: scope '$' does not have a valid ancestor table scope."
            );
    }

    [Test]
    public void It_should_report_unsupported_immediate_parent_scope_kind()
    {
        Action act = () =>
            JsonScopeAttachmentResolver.ResolveExpectedImmediateParentScopeSegmentsOrThrow(
                Path("$.schoolId"),
                DbTableKind.Root
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot resolve expected immediate parent scope for table kind 'Root' at scope '$.schoolId'."
            );
    }

    [Test]
    public void It_should_resolve_extension_collection_attachment_from_base_collection_scope()
    {
        var relativeSegments = JsonScopeAttachmentResolver.ResolveRelativeAttachmentSegmentsOrThrow(
            Path("$.addresses[*]"),
            Path("$._ext.sample.addresses[*]._ext.sample"),
            DbTableKind.CollectionExtensionScope
        );

        JsonScopeAttachmentResolver.FormatScope(relativeSegments).Should().Be("$._ext.sample");
    }

    [Test]
    public void It_should_resolve_same_scope_attachment_to_empty_relative_segments()
    {
        var resolved = JsonScopeAttachmentResolver.TryResolveRelativeAttachmentSegments(
            Path("$.addresses[*]"),
            Path("$.addresses[*]"),
            DbTableKind.Collection,
            out var relativeSegments
        );

        resolved.Should().BeTrue();
        relativeSegments.Should().BeEmpty();
    }

    [Test]
    public void It_should_return_false_when_parent_scope_is_not_an_attachment_prefix()
    {
        var resolved = JsonScopeAttachmentResolver.TryResolveRelativeAttachmentSegments(
            Path("$.otherCollection[*]"),
            Path("$.addresses[*].telephoneNumbers[*]"),
            DbTableKind.Collection,
            out var relativeSegments
        );

        resolved.Should().BeFalse();
        relativeSegments.Should().BeEmpty();
    }

    [Test]
    public void It_should_return_false_when_parent_scope_is_longer_than_child_scope()
    {
        var resolved = JsonScopeAttachmentResolver.TryResolveRelativeAttachmentSegments(
            Path("$.addresses[*].telephoneNumbers[*]"),
            Path("$.addresses[*]"),
            DbTableKind.Collection,
            out var relativeSegments
        );

        resolved.Should().BeFalse();
        relativeSegments.Should().BeEmpty();
    }

    [Test]
    public void It_should_report_unresolvable_relative_attachment()
    {
        Action act = () =>
            JsonScopeAttachmentResolver.ResolveRelativeAttachmentSegmentsOrThrow(
                Path("$.otherCollection[*]"),
                Path("$.addresses[*].telephoneNumbers[*]"),
                DbTableKind.Collection
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Cannot resolve scope attachment from child scope '$.addresses[*].telephoneNumbers[*]' "
                    + "relative to parent scope '$.otherCollection[*]' for table kind 'Collection'."
            );
    }

    private static JsonPathExpression Path(string canonical) => new(canonical, []);

    private sealed record UnsupportedSegment : JsonPathSegment;
}
