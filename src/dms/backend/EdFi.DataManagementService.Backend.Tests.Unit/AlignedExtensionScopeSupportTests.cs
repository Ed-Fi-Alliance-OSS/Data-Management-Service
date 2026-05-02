// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_AlignedExtensionScopeSupport_classify_for_a_standard_aligned_scope
{
    private AlignedExtensionScopeShape? _shape;

    [SetUp]
    public void Setup() => _shape = AlignedExtensionScopeSupport.Classify("$.parents[*]._ext.sample");

    [Test]
    public void It_classifies_as_non_mirrored() => _shape!.Value.IsMirrored.Should().BeFalse();

    [Test]
    public void It_yields_the_immediate_parent_collection_scope() =>
        _shape!.Value.ParentCollectionScope.Should().Be("$.parents[*]");
}

[TestFixture]
public class Given_AlignedExtensionScopeSupport_classify_for_a_mirrored_aligned_scope_with_matching_names
{
    private AlignedExtensionScopeShape? _shape;

    [SetUp]
    public void Setup() =>
        _shape = AlignedExtensionScopeSupport.Classify("$._ext.sample.addresses[*]._ext.sample");

    [Test]
    public void It_classifies_as_mirrored() => _shape!.Value.IsMirrored.Should().BeTrue();

    [Test]
    public void It_re_roots_the_parent_under_the_document_root() =>
        _shape!.Value.ParentCollectionScope.Should().Be("$.addresses[*]");
}

[TestFixture]
public class Given_AlignedExtensionScopeSupport_classify_for_an_aligned_scope_with_mismatched_extension_names
{
    private AlignedExtensionScopeShape? _shape;

    [SetUp]
    public void Setup() =>
        _shape = AlignedExtensionScopeSupport.Classify("$._ext.sample.addresses[*]._ext.other");

    [Test]
    public void It_classifies_as_non_mirrored_because_leading_and_trailing_names_differ() =>
        _shape!.Value.IsMirrored.Should().BeFalse();

    [Test]
    public void It_keeps_the_parent_under_the_extension_prefixed_scope() =>
        _shape!.Value.ParentCollectionScope.Should().Be("$._ext.sample.addresses[*]");
}

[TestFixture]
public class Given_AlignedExtensionScopeSupport_classify_for_a_scope_without_an_aligned_extension_marker
{
    [Test]
    public void It_returns_null() =>
        AlignedExtensionScopeSupport.Classify("$.parents[*].kids[*]").Should().BeNull();
}

[TestFixture]
public class Given_AlignedExtensionScopeSupport_classify_for_a_trailing_marker_with_a_compound_name
{
    [Test]
    public void It_returns_null_because_the_trailing_extension_name_must_be_a_single_property_segment() =>
        AlignedExtensionScopeSupport.Classify("$.parents[*]._ext.sample.deeper").Should().BeNull();
}

[TestFixture]
public class Given_AlignedExtensionScopeSupport_classify_for_a_nested_extension_collection_scope
{
    private AlignedExtensionScopeShape? _shape;

    [SetUp]
    public void Setup() =>
        _shape = AlignedExtensionScopeSupport.Classify(
            "$._ext.sample.addresses[*]._ext.sample.sponsors[*]._ext.sample"
        );

    [Test]
    public void It_classifies_as_mirrored() => _shape!.Value.IsMirrored.Should().BeTrue();

    [Test]
    public void It_re_roots_the_full_nested_parent_under_the_document_root() =>
        _shape!.Value.ParentCollectionScope.Should().Be("$.addresses[*]._ext.sample.sponsors[*]");
}
