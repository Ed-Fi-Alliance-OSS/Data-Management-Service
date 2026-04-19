// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.Profile;
using FluentAssertions;
using NUnit.Framework;
using HiddenPathMatchKind = EdFi.DataManagementService.Backend.Profile.ProfileMemberGovernanceRules.HiddenPathMatchKind;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_IsHiddenGoverned_with_exact_match_and_exact_path
{
    private bool _result;

    [SetUp]
    public void Setup()
    {
        _result = ProfileMemberGovernanceRules.IsHiddenGoverned(
            governingPath: "birthDate",
            hiddenMemberPaths: ImmutableArray.Create("birthDate"),
            matchKind: HiddenPathMatchKind.Exact
        );
    }

    [Test]
    public void It_returns_true() => _result.Should().BeTrue();
}

[TestFixture]
public class Given_IsHiddenGoverned_with_exact_match_and_different_path
{
    private bool _result;

    [SetUp]
    public void Setup()
    {
        _result = ProfileMemberGovernanceRules.IsHiddenGoverned(
            governingPath: "middleName",
            hiddenMemberPaths: ImmutableArray.Create("birthDate"),
            matchKind: HiddenPathMatchKind.Exact
        );
    }

    [Test]
    public void It_returns_false() => _result.Should().BeFalse();
}

[TestFixture]
public class Given_IsHiddenGoverned_reference_rooted_with_whole_reference_hidden
{
    // Whole reference hidden: HiddenMemberPaths contains "schoolReference". A reference-rooted
    // binding whose governing path is also "schoolReference" is preserved by exact match.
    private bool _result;

    [SetUp]
    public void Setup()
    {
        _result = ProfileMemberGovernanceRules.IsHiddenGoverned(
            governingPath: "schoolReference",
            hiddenMemberPaths: ImmutableArray.Create("schoolReference"),
            matchKind: HiddenPathMatchKind.ReferenceRooted
        );
    }

    [Test]
    public void It_returns_true() => _result.Should().BeTrue();
}

[TestFixture]
public class Given_IsHiddenGoverned_reference_rooted_with_sub_reference_path_hidden
{
    // profiles.md:782 contract: a hidden path naming a sub-member of a reference
    // (schoolReference.schoolId) preserves the entire reference-derived storage family
    // keyed on the reference root (schoolReference). All three bindings whose governing
    // reference root is "schoolReference" are preserved — FK, this identity part, and any
    // sibling identity part.
    private bool _fkBinding;
    private bool _hiddenChildBinding;
    private bool _siblingChildBinding;

    [SetUp]
    public void Setup()
    {
        var hidden = ImmutableArray.Create("schoolReference.schoolId");

        _fkBinding = ProfileMemberGovernanceRules.IsHiddenGoverned(
            governingPath: "schoolReference",
            hiddenMemberPaths: hidden,
            matchKind: HiddenPathMatchKind.ReferenceRooted
        );
        _hiddenChildBinding = ProfileMemberGovernanceRules.IsHiddenGoverned(
            governingPath: "schoolReference",
            hiddenMemberPaths: hidden,
            matchKind: HiddenPathMatchKind.ReferenceRooted
        );
        _siblingChildBinding = ProfileMemberGovernanceRules.IsHiddenGoverned(
            governingPath: "schoolReference",
            hiddenMemberPaths: hidden,
            matchKind: HiddenPathMatchKind.ReferenceRooted
        );
    }

    [Test]
    public void It_preserves_the_FK_binding() => _fkBinding.Should().BeTrue();

    [Test]
    public void It_preserves_the_exact_child_binding() => _hiddenChildBinding.Should().BeTrue();

    [Test]
    public void It_preserves_the_sibling_child_binding() => _siblingChildBinding.Should().BeTrue();
}

[TestFixture]
public class Given_IsHiddenGoverned_reference_rooted_with_unrelated_prefix_without_dot_boundary
{
    // Defensive: a hidden path like "school" must not match governing path "schoolReference"
    // even though the strings share a prefix — the dot boundary is required.
    private bool _result;

    [SetUp]
    public void Setup()
    {
        _result = ProfileMemberGovernanceRules.IsHiddenGoverned(
            governingPath: "schoolReference",
            hiddenMemberPaths: ImmutableArray.Create("school"),
            matchKind: HiddenPathMatchKind.ReferenceRooted
        );
    }

    [Test]
    public void It_returns_false() => _result.Should().BeFalse();
}

[TestFixture]
public class Given_IsHiddenGoverned_reference_rooted_with_disjoint_hidden_path
{
    // A hidden path for an unrelated reference (or scalar) must not govern this reference.
    private bool _result;

    [SetUp]
    public void Setup()
    {
        _result = ProfileMemberGovernanceRules.IsHiddenGoverned(
            governingPath: "schoolReference",
            hiddenMemberPaths: ImmutableArray.Create("studentReference.studentUniqueId"),
            matchKind: HiddenPathMatchKind.ReferenceRooted
        );
    }

    [Test]
    public void It_returns_false() => _result.Should().BeFalse();
}

[TestFixture]
public class Given_IsHiddenGoverned_with_exact_match_and_ancestor_path
{
    // Exact-match bindings (scalar, descriptor) only match their own path exactly; a hidden path
    // naming an ancestor scope does not reach the scalar because scope-level hiding is expressed
    // via stored-scope visibility, not via ancestor member paths.
    private bool _result;

    [SetUp]
    public void Setup()
    {
        _result = ProfileMemberGovernanceRules.IsHiddenGoverned(
            governingPath: "studentReference.studentUniqueId",
            hiddenMemberPaths: ImmutableArray.Create("studentReference"),
            matchKind: HiddenPathMatchKind.Exact
        );
    }

    [Test]
    public void It_returns_false() => _result.Should().BeFalse();
}
