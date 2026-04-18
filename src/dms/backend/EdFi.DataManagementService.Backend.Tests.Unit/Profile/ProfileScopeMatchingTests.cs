// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_ProfileScopeMatching_building_candidate_scope_sets
{
    private ImmutableArray<string> _candidateScopes;

    [SetUp]
    public void Setup()
    {
        var request = ProfileTestDoubles.CreateRequest(
            writableBody: null,
            rootResourceCreatable: true,
            ProfileTestDoubles.RequestVisiblePresentScope("$"),
            ProfileTestDoubles.RequestVisiblePresentScope("$.studentEducationOrganizationAssociation"),
            ProfileTestDoubles.RequestVisiblePresentScope(
                "$.studentEducationOrganizationAssociation.schoolReference"
            )
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            visibleStoredBody: null,
            ProfileTestDoubles.StoredVisiblePresentScope("$.studentEducationOrganizationAssociation"),
            ProfileTestDoubles.StoredVisiblePresentScope("$.schoolReference")
        );

        _candidateScopes = ProfileScopeMatching.BuildCandidateScopeSet(request, context);
    }

    [Test]
    public void It_unions_request_and_stored_scopes_without_duplicates() =>
        _candidateScopes.Should().OnlyHaveUniqueItems();

    [Test]
    public void It_sorts_scopes_longest_first() =>
        _candidateScopes
            .Should()
            .Equal(
                "$.studentEducationOrganizationAssociation.schoolReference",
                "$.studentEducationOrganizationAssociation",
                "$.schoolReference",
                "$"
            );
}

[TestFixture]
public class Given_ProfileScopeMatching_matching_longest_scopes
{
    private ImmutableArray<string> _candidateScopes;

    [SetUp]
    public void Setup()
    {
        _candidateScopes =
        [
            "$.studentEducationOrganizationAssociation.schoolReference",
            "$.studentEducationOrganizationAssociation",
            "$",
        ];
    }

    [Test]
    public void It_returns_the_longest_matching_scope_for_descendant_paths() =>
        ProfileScopeMatching
            .TryMatchLongestScope(
                "$.studentEducationOrganizationAssociation.schoolReference.schoolId",
                _candidateScopes
            )
            .Should()
            .Be("$.studentEducationOrganizationAssociation.schoolReference");

    [Test]
    public void It_returns_the_exact_scope_when_binding_path_equals_scope() =>
        ProfileScopeMatching
            .TryMatchLongestScope(
                "$.studentEducationOrganizationAssociation.schoolReference",
                _candidateScopes
            )
            .Should()
            .Be("$.studentEducationOrganizationAssociation.schoolReference");

    [Test]
    public void It_does_not_match_partial_scope_tokens() =>
        ProfileScopeMatching
            .TryMatchLongestScope(
                "$.studentEducationOrganizationAssociationAlias.schoolReference",
                _candidateScopes
            )
            .Should()
            .Be("$");
}

[TestFixture]
public class Given_ProfileScopeMatching_stripping_scope_prefixes
{
    [Test]
    public void It_returns_the_relative_member_path_for_descendant_bindings() =>
        ProfileScopeMatching
            .StripScopePrefix(
                "$.studentEducationOrganizationAssociation.schoolReference.schoolId",
                "$.studentEducationOrganizationAssociation.schoolReference"
            )
            .Should()
            .Be("schoolId");

    [Test]
    public void It_returns_empty_string_for_exact_scope_matches() =>
        ProfileScopeMatching
            .StripScopePrefix(
                "$.studentEducationOrganizationAssociation.schoolReference",
                "$.studentEducationOrganizationAssociation.schoolReference"
            )
            .Should()
            .BeEmpty();
}
