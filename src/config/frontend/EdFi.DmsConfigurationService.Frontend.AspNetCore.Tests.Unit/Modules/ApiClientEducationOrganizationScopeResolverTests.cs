// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// The client education organization rules that need the Application, against an Application
/// holding 1001, 1002 and 1003.
/// </summary>
public class ApiClientEducationOrganizationScopeResolverTests
{
    private static readonly long[] ApplicationIds = [1003, 1001, 1002];

    private static ApiClientEducationOrganizationState Inheriting() =>
        new(1, true, [1001, 1002, 1003], [1001, 1002, 1003], null);

    private static ApiClientEducationOrganizationState NarrowedTo(params long[] ids) =>
        new(1, false, ids, ids, null);

    private static ApiClientEducationOrganizationScopeResolution Resolve(
        long[]? requestedIds,
        bool? inherits,
        ApiClientEducationOrganizationState? current = null
    ) => ApiClientEducationOrganizationScopeResolver.Resolve(requestedIds, inherits, ApplicationIds, current);

    private static ApiClientEducationOrganizationScopeResolution.Resolved Resolved(
        ApiClientEducationOrganizationScopeResolution resolution
    ) => resolution.Should().BeOfType<ApiClientEducationOrganizationScopeResolution.Resolved>().Subject;

    private static string InvalidField(ApiClientEducationOrganizationScopeResolution resolution) =>
        resolution
            .Should()
            .BeOfType<ApiClientEducationOrganizationScopeResolution.Invalid>()
            .Subject.Failure.PropertyName;

    [TestFixture]
    public class Given_a_list_without_the_flag : ApiClientEducationOrganizationScopeResolverTests
    {
        private ApiClientEducationOrganizationScopeResolution.Resolved _resolved = null!;

        [SetUp]
        public void Setup() => _resolved = Resolved(Resolve([1003, 1001, 1003], null, Inheriting()));

        [Test]
        public void It_narrows_to_the_distinct_sorted_list()
        {
            _resolved
                .Scope.Should()
                .BeOfType<ApiClientEducationOrganizationScope.Narrow>()
                .Which.EducationOrganizationIds.Should()
                .Equal(1001, 1003);
            _resolved.Inherits.Should().BeFalse();
            _resolved.EducationOrganizationIds.Should().Equal(1001, 1003);
        }

        [Test]
        public void It_claims_the_narrowed_list() => _resolved.Claim.Should().Be("1001,1003");
    }

    [TestFixture]
    public class Given_a_list_equal_to_the_Application_without_the_flag
        : ApiClientEducationOrganizationScopeResolverTests
    {
        [Test]
        public void It_narrows_rather_than_inherits()
        {
            var resolved = Resolved(Resolve([1002, 1001, 1003], null));

            resolved.Inherits.Should().BeFalse();
            resolved.Scope.Should().BeOfType<ApiClientEducationOrganizationScope.Narrow>();
            resolved.EducationOrganizationIds.Should().Equal(1001, 1002, 1003);
        }
    }

    [TestFixture]
    public class Given_false_with_a_list : ApiClientEducationOrganizationScopeResolverTests
    {
        [Test]
        public void It_narrows_to_the_list()
        {
            var resolved = Resolved(Resolve([1002], false, Inheriting()));

            resolved.Inherits.Should().BeFalse();
            resolved.EducationOrganizationIds.Should().Equal(1002);
        }
    }

    [TestFixture]
    public class Given_true_without_a_list : ApiClientEducationOrganizationScopeResolverTests
    {
        [Test]
        public void It_requests_inheritance_of_the_Application_list()
        {
            var resolved = Resolved(Resolve(null, true, NarrowedTo(1002)));

            resolved.Scope.Should().BeOfType<ApiClientEducationOrganizationScope.Inherit>();
            resolved.Inherits.Should().BeTrue();
            resolved.Claim.Should().Be("1001,1002,1003");
        }
    }

    [TestFixture]
    public class Given_true_with_the_Application_list_reordered_and_repeated
        : ApiClientEducationOrganizationScopeResolverTests
    {
        [Test]
        public void It_inherits_and_ignores_the_list()
        {
            var resolved = Resolved(Resolve([1003, 1002, 1001, 1002], true, NarrowedTo(1002)));

            resolved.Scope.Should().BeOfType<ApiClientEducationOrganizationScope.Inherit>();
            resolved.EducationOrganizationIds.Should().Equal(1001, 1002, 1003);
        }
    }

    [TestFixture]
    public class Given_true_with_another_list : ApiClientEducationOrganizationScopeResolverTests
    {
        [Test]
        public void It_rejects_the_list() =>
            InvalidField(Resolve([1001, 1002], true)).Should().Be("EducationOrganizationIds");
    }

    [TestFixture]
    public class Given_a_list_with_an_education_organization_not_on_the_Application
        : ApiClientEducationOrganizationScopeResolverTests
    {
        private ApiClientEducationOrganizationScopeResolution _resolution = null!;

        [SetUp]
        public void Setup() => _resolution = Resolve([1001, 1004, 1005], false);

        [Test]
        public void It_rejects_the_list() =>
            InvalidField(_resolution).Should().Be("EducationOrganizationIds");

        [Test]
        public void It_names_the_missing_ids() =>
            _resolution
                .Should()
                .BeOfType<ApiClientEducationOrganizationScopeResolution.Invalid>()
                .Subject.Failure.ErrorMessage.Should()
                .EndWith("Not on the Application: 1004, 1005.");
    }

    [TestFixture]
    public class Given_neither_field_on_create : ApiClientEducationOrganizationScopeResolverTests
    {
        [Test]
        public void It_inherits_by_the_repository_default()
        {
            var resolved = Resolved(Resolve(null, null));

            resolved.Scope.Should().BeNull();
            resolved.Inherits.Should().BeTrue();
            resolved.Claim.Should().Be("1001,1002,1003");
        }
    }

    [TestFixture]
    public class Given_neither_field_for_a_narrowed_client : ApiClientEducationOrganizationScopeResolverTests
    {
        [Test]
        public void It_keeps_the_narrowed_list()
        {
            var resolved = Resolved(Resolve(null, null, NarrowedTo(1003, 1001)));

            resolved.Scope.Should().BeNull();
            resolved.Inherits.Should().BeFalse();
            resolved.Claim.Should().Be("1001,1003");
        }

        [Test]
        public void It_keeps_an_empty_narrowed_list()
        {
            var resolved = Resolved(Resolve(null, null, NarrowedTo()));

            resolved.Inherits.Should().BeFalse();
            resolved.Claim.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_neither_field_for_a_narrowed_client_that_does_not_fit_its_Application
        : ApiClientEducationOrganizationScopeResolverTests
    {
        [Test]
        public void It_reports_a_scope_conflict_with_the_missing_ids() =>
            Resolve(null, null, NarrowedTo(1001, 1004))
                .Should()
                .BeOfType<ApiClientEducationOrganizationScopeResolution.ScopeConflict>()
                .Which.MissingEducationOrganizationIds.Should()
                .Equal(1004);

        [Test]
        public void It_resolves_an_explicit_replacement_list_instead()
        {
            var resolved = Resolved(Resolve([1002], null, NarrowedTo(1001, 1004)));

            resolved.EducationOrganizationIds.Should().Equal(1002);
        }

        [Test]
        public void It_resolves_an_explicit_true_instead() =>
            Resolved(Resolve(null, true, NarrowedTo(1001, 1004))).Inherits.Should().BeTrue();
    }

    [TestFixture]
    public class Given_neither_field_for_an_inheriting_client_whose_rows_lag_its_Application
        : ApiClientEducationOrganizationScopeResolverTests
    {
        private static readonly ApiClientEducationOrganizationState LaggingRows = new(
            1,
            true,
            [1001, 1002],
            [1001, 1002, 1003],
            null
        );

        [Test]
        public void It_claims_the_Application_list_and_expects_the_rows_kept()
        {
            var resolved = Resolved(Resolve(null, null, LaggingRows));

            resolved.Claim.Should().Be("1001,1002,1003");
            resolved.StoredEducationOrganizationIds.Should().Equal(1001, 1002);
        }

        [Test]
        public void It_expects_the_Application_list_as_the_rows_of_a_move() =>
            Resolved(
                ApiClientEducationOrganizationScopeResolver.Resolve(
                    null,
                    null,
                    ApplicationIds,
                    LaggingRows,
                    moves: true
                )
            )
                .StoredEducationOrganizationIds.Should()
                .Equal(1001, 1002, 1003);

        [Test]
        public void It_expects_the_Application_list_as_the_rows_of_an_explicit_inherit() =>
            Resolved(Resolve(null, true, LaggingRows))
                .StoredEducationOrganizationIds.Should()
                .Equal(1001, 1002, 1003);
    }

    [TestFixture]
    public class Given_neither_field_for_an_inheriting_client
        : ApiClientEducationOrganizationScopeResolverTests
    {
        [Test]
        public void It_keeps_inheriting_the_Application_list()
        {
            var resolved = Resolved(Resolve(null, null, Inheriting()));

            resolved.Scope.Should().BeNull();
            resolved.Inherits.Should().BeTrue();
            resolved.Claim.Should().Be("1001,1002,1003");
        }
    }
}
