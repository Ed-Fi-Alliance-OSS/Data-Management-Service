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
            memberPath: "birthDate",
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
            memberPath: "middleName",
            hiddenMemberPaths: ImmutableArray.Create("birthDate"),
            matchKind: HiddenPathMatchKind.Exact
        );
    }

    [Test]
    public void It_returns_false() => _result.Should().BeFalse();
}

[TestFixture]
public class Given_IsHiddenGoverned_with_ancestor_match_and_whole_reference_hidden
{
    private bool _result;

    [SetUp]
    public void Setup()
    {
        _result = ProfileMemberGovernanceRules.IsHiddenGoverned(
            memberPath: "schoolReference.schoolId",
            hiddenMemberPaths: ImmutableArray.Create("schoolReference"),
            matchKind: HiddenPathMatchKind.AncestorOrExact
        );
    }

    [Test]
    public void It_returns_true() => _result.Should().BeTrue();
}

[TestFixture]
public class Given_IsHiddenGoverned_with_ancestor_match_and_exact_child_hidden
{
    private bool _child;
    private bool _sibling;

    [SetUp]
    public void Setup()
    {
        var hidden = ImmutableArray.Create("schoolReference.schoolId");
        _child = ProfileMemberGovernanceRules.IsHiddenGoverned(
            memberPath: "schoolReference.schoolId",
            hiddenMemberPaths: hidden,
            matchKind: HiddenPathMatchKind.AncestorOrExact
        );
        _sibling = ProfileMemberGovernanceRules.IsHiddenGoverned(
            memberPath: "schoolReference.localEducationAgencyId",
            hiddenMemberPaths: hidden,
            matchKind: HiddenPathMatchKind.AncestorOrExact
        );
    }

    [Test]
    public void It_matches_the_exact_child() => _child.Should().BeTrue();

    [Test]
    public void It_does_not_match_a_sibling_child() => _sibling.Should().BeFalse();
}

[TestFixture]
public class Given_IsHiddenGoverned_with_ancestor_match_and_prefix_without_dot_boundary
{
    private bool _result;

    [SetUp]
    public void Setup()
    {
        _result = ProfileMemberGovernanceRules.IsHiddenGoverned(
            memberPath: "schoolReference.schoolId",
            hiddenMemberPaths: ImmutableArray.Create("school"),
            matchKind: HiddenPathMatchKind.AncestorOrExact
        );
    }

    [Test]
    public void It_returns_false() => _result.Should().BeFalse();
}

[TestFixture]
public class Given_IsHiddenGoverned_with_exact_match_and_ancestor_path
{
    private bool _result;

    [SetUp]
    public void Setup()
    {
        _result = ProfileMemberGovernanceRules.IsHiddenGoverned(
            memberPath: "studentReference.studentUniqueId",
            hiddenMemberPaths: ImmutableArray.Create("studentReference"),
            matchKind: HiddenPathMatchKind.Exact
        );
    }

    [Test]
    public void It_returns_false() => _result.Should().BeFalse();
}
