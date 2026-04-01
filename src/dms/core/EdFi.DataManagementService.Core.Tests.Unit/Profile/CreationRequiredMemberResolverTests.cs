// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class CreationRequiredMemberResolverTests
{
    // -----------------------------------------------------------------------
    //  Root scope with required members, IncludeOnly profile hiding one member
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Root_Scope_With_Required_Members : CreationRequiredMemberResolverTests
    {
        private CreationRequiredMemberResult _result = null!;

        [SetUp]
        public void Setup()
        {
            CompiledScopeDescriptor rootScope = ProfileTestFixtures.SharedFixtureScopes[0];

            List<string> effectiveSchemaRequired = ["studentReference", "schoolReference", "entryDate"];

            // IncludeOnly profile that includes studentReference and schoolReference but NOT entryDate
            ScopeMemberFilter filter = new(
                MemberSelection.IncludeOnly,
                new HashSet<string> { "studentReference", "schoolReference" }
            );

            _result = CreationRequiredMemberResolver.Resolve(rootScope, effectiveSchemaRequired, filter);
        }

        [Test]
        public void It_should_report_entryDate_as_hidden()
        {
            _result.HiddenByProfile.Should().HaveCount(1);
            _result.HiddenByProfile.Should().Contain("entryDate");
        }
    }

    // -----------------------------------------------------------------------
    //  Collection scope with semantic identity that is also schema-required
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_Scope_With_Semantic_Identity : CreationRequiredMemberResolverTests
    {
        private CreationRequiredMemberResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // classPeriods collection scope with SemanticIdentityRelativePathsInOrder = ["classPeriodName"]
            CompiledScopeDescriptor collectionScope = ProfileTestFixtures.SharedFixtureScopes[2];

            List<string> effectiveSchemaRequired = ["classPeriodName"];

            // IncludeOnly profile that includes classPeriodName
            ScopeMemberFilter filter = new(
                MemberSelection.IncludeOnly,
                new HashSet<string> { "classPeriodName" }
            );

            _result = CreationRequiredMemberResolver.Resolve(
                collectionScope,
                effectiveSchemaRequired,
                filter
            );
        }

        [Test]
        public void It_should_have_no_hidden_members()
        {
            _result.HiddenByProfile.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  Root scope with storage-managed values that should be excluded
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Scope_With_Storage_Managed_Values : CreationRequiredMemberResolverTests
    {
        private CreationRequiredMemberResult _result = null!;

        [SetUp]
        public void Setup()
        {
            CompiledScopeDescriptor rootScope = ProfileTestFixtures.SharedFixtureScopes[0];

            // Effective schema includes storage-managed values alongside a real required member
            List<string> effectiveSchemaRequired = ["id", "studentReference", "_etag"];

            ScopeMemberFilter filter = new(MemberSelection.IncludeAll, new HashSet<string>());

            _result = CreationRequiredMemberResolver.Resolve(rootScope, effectiveSchemaRequired, filter);
        }

        [Test]
        public void It_should_have_no_hidden_members()
        {
            _result.HiddenByProfile.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  Collection with reference-backed semantic identity (dotted path)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Collection_With_Reference_Backed_Identity : CreationRequiredMemberResolverTests
    {
        private CreationRequiredMemberResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Custom scope with a dotted semantic identity path
            CompiledScopeDescriptor collectionScope = new(
                JsonScope: "$.enrollments[*]",
                ScopeKind: ScopeKind.Collection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: ["schoolReference.schoolId"],
                CanonicalScopeRelativeMemberPaths: ["schoolReference.schoolId", "enrollmentDate"]
            );

            // No schema-required members; the identity path alone contributes
            List<string> effectiveSchemaRequired = [];

            // IncludeOnly profile that includes schoolReference (the top-level member)
            ScopeMemberFilter filter = new(
                MemberSelection.IncludeOnly,
                new HashSet<string> { "schoolReference" }
            );

            _result = CreationRequiredMemberResolver.Resolve(
                collectionScope,
                effectiveSchemaRequired,
                filter
            );
        }

        [Test]
        public void It_should_have_no_hidden_members()
        {
            _result.HiddenByProfile.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  IncludeAll profile — nothing should be hidden
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_IncludeAll_Profile : CreationRequiredMemberResolverTests
    {
        private CreationRequiredMemberResult _result = null!;

        [SetUp]
        public void Setup()
        {
            CompiledScopeDescriptor rootScope = ProfileTestFixtures.SharedFixtureScopes[0];

            List<string> effectiveSchemaRequired = ["studentReference", "schoolReference", "entryDate"];

            ScopeMemberFilter filter = new(MemberSelection.IncludeAll, new HashSet<string>());

            _result = CreationRequiredMemberResolver.Resolve(rootScope, effectiveSchemaRequired, filter);
        }

        [Test]
        public void It_should_have_no_hidden_members()
        {
            _result.HiddenByProfile.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  ExcludeOnly profile that explicitly excludes a required member
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_ExcludeOnly_Profile_Excluding_Required : CreationRequiredMemberResolverTests
    {
        private CreationRequiredMemberResult _result = null!;

        [SetUp]
        public void Setup()
        {
            CompiledScopeDescriptor rootScope = ProfileTestFixtures.SharedFixtureScopes[0];

            List<string> effectiveSchemaRequired = ["studentReference", "schoolReference", "entryDate"];

            // ExcludeOnly profile that explicitly excludes entryDate
            ScopeMemberFilter filter = new(MemberSelection.ExcludeOnly, new HashSet<string> { "entryDate" });

            _result = CreationRequiredMemberResolver.Resolve(rootScope, effectiveSchemaRequired, filter);
        }

        [Test]
        public void It_should_report_entryDate_as_hidden()
        {
            _result.HiddenByProfile.Should().HaveCount(1);
            _result.HiddenByProfile.Should().Contain("entryDate");
        }
    }
}
