// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

/// <summary>
/// Covers backend enforcement of deferred C4 creatability for flattened (root-table-owned)
/// embedded-object non-collection scopes — the case the separate-table decider and collection
/// planner do not reach (scenario 12, Assessment contentStandard).
/// </summary>
public abstract class FlattenedScopeCreatabilityDetectionTests
{
    private static readonly IReadOnlySet<string> RootOnlyTableScopes = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "$",
    };

    private static RequestScopeState Request(
        string scope,
        ProfileVisibilityKind visibility,
        bool creatable
    ) => new(new ScopeInstanceAddress(scope, []), visibility, creatable);

    private static StoredScopeState Stored(string scope, ProfileVisibilityKind visibility) =>
        new(new ScopeInstanceAddress(scope, []), visibility, []);

    [TestFixture]
    public class Given_New_NonCreatable_Flattened_Scope : FlattenedScopeCreatabilityDetectionTests
    {
        [Test]
        public void It_rejects_the_create()
        {
            var rejection = RelationalWriteProfileMergeSynthesizer.DetectFlattenedScopeCreatabilityRejection(
                [
                    Request("$", ProfileVisibilityKind.VisiblePresent, false),
                    Request("$.contentStandard", ProfileVisibilityKind.VisiblePresent, false),
                ],
                [Stored("$.contentStandard", ProfileVisibilityKind.VisibleAbsent)],
                RootOnlyTableScopes
            );

            rejection.Should().NotBeNull();
            rejection!.ScopeJsonScope.Should().Be("$.contentStandard");
        }
    }

    [TestFixture]
    public class Given_New_Creatable_Flattened_Scope : FlattenedScopeCreatabilityDetectionTests
    {
        [Test]
        public void It_does_not_reject()
        {
            RelationalWriteProfileMergeSynthesizer
                .DetectFlattenedScopeCreatabilityRejection(
                    [Request("$.contentStandard", ProfileVisibilityKind.VisiblePresent, true)],
                    [],
                    RootOnlyTableScopes
                )
                .Should()
                .BeNull();
        }
    }

    [TestFixture]
    public class Given_Existing_Visible_Flattened_Scope : FlattenedScopeCreatabilityDetectionTests
    {
        [Test]
        public void It_does_not_reject_an_update()
        {
            // Non-creatable + VisiblePresent, but the stored scope already exists visibly →
            // this is an update/preserve, not a create.
            RelationalWriteProfileMergeSynthesizer
                .DetectFlattenedScopeCreatabilityRejection(
                    [Request("$.contentStandard", ProfileVisibilityKind.VisiblePresent, false)],
                    [Stored("$.contentStandard", ProfileVisibilityKind.VisiblePresent)],
                    RootOnlyTableScopes
                )
                .Should()
                .BeNull();
        }
    }

    [TestFixture]
    public class Given_Unsupplied_Flattened_Scope : FlattenedScopeCreatabilityDetectionTests
    {
        [Test]
        public void It_does_not_reject_a_visible_absent_scope()
        {
            RelationalWriteProfileMergeSynthesizer
                .DetectFlattenedScopeCreatabilityRejection(
                    [Request("$.contentStandard", ProfileVisibilityKind.VisibleAbsent, false)],
                    [],
                    RootOnlyTableScopes
                )
                .Should()
                .BeNull();
        }
    }

    [TestFixture]
    public class Given_A_Separate_Table_Scope : FlattenedScopeCreatabilityDetectionTests
    {
        [Test]
        public void It_does_not_reject_handled_by_separate_table_decider()
        {
            // The scope has its own table plan → enforced by the separate-table decider, not here.
            var tableScopes = new HashSet<string>(StringComparer.Ordinal) { "$", "$._ext.sample" };

            RelationalWriteProfileMergeSynthesizer
                .DetectFlattenedScopeCreatabilityRejection(
                    [Request("$._ext.sample", ProfileVisibilityKind.VisiblePresent, false)],
                    [],
                    tableScopes
                )
                .Should()
                .BeNull();
        }
    }

    [TestFixture]
    public class Given_The_Root_Scope : FlattenedScopeCreatabilityDetectionTests
    {
        [Test]
        public void It_does_not_reject_root_handled_by_root_creatable_flag()
        {
            RelationalWriteProfileMergeSynthesizer
                .DetectFlattenedScopeCreatabilityRejection(
                    [Request("$", ProfileVisibilityKind.VisiblePresent, false)],
                    [],
                    RootOnlyTableScopes
                )
                .Should()
                .BeNull();
        }
    }

    [TestFixture]
    public class Given_A_Scope_Inside_A_Collection : FlattenedScopeCreatabilityDetectionTests
    {
        [Test]
        public void It_does_not_reject_collection_nested_scope()
        {
            // Scopes nested inside collections are materialized per item by the walker/planner.
            var addressInsideCollection = new ScopeInstanceAddress(
                "$.things[*].period",
                [new AncestorCollectionInstance("$.things[*]", [])]
            );

            RelationalWriteProfileMergeSynthesizer
                .DetectFlattenedScopeCreatabilityRejection(
                    [
                        new RequestScopeState(
                            addressInsideCollection,
                            ProfileVisibilityKind.VisiblePresent,
                            false
                        ),
                    ],
                    [],
                    RootOnlyTableScopes
                )
                .Should()
                .BeNull();
        }
    }
}
