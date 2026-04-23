// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

/// <summary>
/// Parity fixture: verifies that ClassifyBindingsWithExplicitHiddenPaths produces
/// identical dispositions to ClassifyBindings when the same hidden-member paths are
/// supplied directly instead of being derived from stored scope states.
///
/// Scenario: single scalar binding at $.birthDate; stored scope $ has HiddenMemberPaths
/// = ["birthDate"]. The scope-state path derives that hidden path from the context;
/// the row-level path receives it as an explicit argument. Both must classify the
/// binding as HiddenPreserved.
/// </summary>
[TestFixture]
public class Given_row_level_primitive_matches_scope_state_derivation_for_equivalent_hidden_paths
{
    private ImmutableArray<RootBindingDisposition> _scopeStateDerived;
    private ImmutableArray<RootBindingDisposition> _rowLevelDerived;

    [SetUp]
    public void Setup()
    {
        // Build a plan: single scalar at $.birthDate on the root table.
        var plan = ProfileTestDoubles.BuildSingleScalarBindingRootPlan(scalarRelativePath: "$.birthDate");
        var rootTable = plan.TablePlansInDependencyOrder[0];
        var resolverOwnedIndices = ProfileBindingClassificationCore.CollectResolverOwnedIndices(rootTable);

        // Scope-state-derived path: stored scope $ with HiddenMemberPaths = ["birthDate"].
        var request = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        var context = ProfileTestDoubles.CreateContext(
            request,
            storedScopeStates: ProfileTestDoubles.StoredVisiblePresentScope("$", "birthDate")
        );
        _scopeStateDerived = ProfileBindingClassificationCore.ClassifyBindings(
            plan,
            rootTable,
            request,
            context,
            resolverOwnedIndices
        );

        // Row-level path: same hidden paths supplied explicitly. No stored context.
        var requestForRowLevel = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        _rowLevelDerived = ProfileBindingClassificationCore.ClassifyBindingsWithExplicitHiddenPaths(
            plan,
            rootTable,
            requestForRowLevel,
            resolverOwnedIndices,
            hiddenMemberPaths: ["birthDate"]
        );
    }

    [Test]
    public void Both_paths_produce_equivalent_dispositions() =>
        _rowLevelDerived.Should().BeEquivalentTo(_scopeStateDerived);
}

/// <summary>
/// Verifies the fail-closed Ordinal rejection still fires when the scope-state path
/// (ClassifyBindings) encounters a WriteValueSource.Ordinal binding on a non-collection
/// table. Reaching this arm means upstream fencing failed.
/// </summary>
[TestFixture]
public class Given_ClassifyBindings_with_WriteValueSource_Ordinal_on_non_collection_table_fails_closed
{
    private Exception? _thrown;

    [SetUp]
    public void Setup()
    {
        // Build a root plan whose single binding is WriteValueSource.Ordinal. This is
        // intentionally malformed — the root table is not a collection table, so
        // ClassifyBindings must throw InvalidOperationException (fail-closed).
        var plan = ProfileTestDoubles.BuildSingleScalarBindingRootPlan(scalarRelativePath: "$.firstName");
        var rootTable = plan.TablePlansInDependencyOrder[0];

        // Replace the scalar binding with an Ordinal binding to simulate upstream fencing
        // failure. We build an equivalent plan by directly constructing the column binding
        // with WriteValueSource.Ordinal using the same column from the existing plan.
        var scalarColumn = rootTable.ColumnBindings[0].Column;
        var ordinalBinding = new WriteColumnBinding(
            scalarColumn,
            new WriteValueSource.Ordinal(),
            scalarColumn.ColumnName.Value
        );
        var ordinalTable = new TableWritePlan(
            TableModel: rootTable.TableModel,
            InsertSql: rootTable.InsertSql,
            UpdateSql: rootTable.UpdateSql,
            DeleteByParentSql: rootTable.DeleteByParentSql,
            BulkInsertBatching: rootTable.BulkInsertBatching,
            ColumnBindings: [ordinalBinding],
            KeyUnificationPlans: rootTable.KeyUnificationPlans
        );

        var request = ProfileTestDoubles.CreateRequest(
            scopeStates: ProfileTestDoubles.RequestVisiblePresentScope("$")
        );
        var resolverOwnedIndices = ProfileBindingClassificationCore.CollectResolverOwnedIndices(ordinalTable);

        try
        {
            ProfileBindingClassificationCore.ClassifyBindings(
                plan,
                ordinalTable,
                request,
                profileAppliedContext: null,
                resolverOwnedIndices
            );
        }
        catch (Exception ex)
        {
            _thrown = ex;
        }
    }

    [Test]
    public void It_throws_InvalidOperationException() =>
        _thrown.Should().BeOfType<InvalidOperationException>();

    [Test]
    public void It_mentions_Ordinal_in_the_message() => _thrown!.Message.Should().Contain("Ordinal");
}

/// <summary>
/// Verifies that ClassifyBindingsWithExplicitHiddenPaths classifies a WriteValueSource.Ordinal
/// binding on a collection table as StorageManaged. Ordinal columns are derived from row
/// position, not from request JSON, so the row-level primitive path must treat them as
/// storage-managed rather than throwing.
/// </summary>
[TestFixture]
public class Given_row_level_primitive_with_Ordinal_on_collection_table_classifies_as_StorageManaged
{
    private ImmutableArray<RootBindingDisposition> _dispositions;

    [SetUp]
    public void Setup()
    {
        // Build a full ResourceWritePlan with a root table and a collection table (SchoolAddress).
        // The collection table has bindings: [0]=Precomputed, [1]=DocumentId, [2]=Ordinal, [3]=Scalar.
        var plan = AdapterFactoryTestFixtures.BuildRootAndCollectionPlan();

        // The collection table is at index 1 in dependency order.
        var collectionTable = plan.TablePlansInDependencyOrder[1];
        var resolverOwnedIndices = ProfileBindingClassificationCore.CollectResolverOwnedIndices(
            collectionTable
        );

        // Production-shape request: no collection RequestScopeState. The classifier uses
        // the table's own JsonScope directly — no request scope state needed.
        var request = ProfileTestDoubles.CreateRequest();

        // Call the explicit-hidden-paths path with no hidden members.
        // Ordinal is unaffected by hidden paths — it must be StorageManaged regardless.
        _dispositions = ProfileBindingClassificationCore.ClassifyBindingsWithExplicitHiddenPaths(
            plan,
            collectionTable,
            request,
            resolverOwnedIndices,
            hiddenMemberPaths: ImmutableArray<string>.Empty
        );
    }

    [Test]
    public void It_classifies_the_Ordinal_binding_as_StorageManaged() =>
        _dispositions.Should().Contain(RootBindingDisposition.StorageManaged);

    [Test]
    public void It_does_not_throw_for_Ordinal_on_collection_table() =>
        _dispositions.Length.Should().BeGreaterThan(0);
}

/// <summary>
/// Regression fixture: verifies that ClassifyBindingsWithExplicitHiddenPaths produces
/// HiddenPreserved for a collection-row scalar binding even when the profile request has
/// NO RequestScopeState for the collection scope (production shape).
///
/// Previously the classifier built candidateScopes from RequestScopeStates. With no
/// collection scope in the request, candidateScopes was empty, TryMatchLongestScope
/// returned null for the binding path, and the binding fell through to VisibleWritable —
/// silently ignoring the hiddenMemberPaths argument.
/// </summary>
[TestFixture]
public class Given_row_level_primitive_with_no_collection_RequestScopeState_classifies_hidden_as_HiddenPreserved
{
    private ImmutableArray<RootBindingDisposition> _dispositions;

    [SetUp]
    public void Setup()
    {
        // Collection plan: [0]=Precomputed, [1]=DocumentId, [2]=Ordinal, [3]=Scalar($.addressType)
        var plan = AdapterFactoryTestFixtures.BuildRootAndCollectionPlan();
        var collectionTable = plan.TablePlansInDependencyOrder[1];
        var resolverOwnedIndices = ProfileBindingClassificationCore.CollectResolverOwnedIndices(
            collectionTable
        );

        // Production-shape request: NO RequestScopeState for "$.addresses[*]".
        // Core does not emit collection scope states — only root-level states are present.
        var request = ProfileTestDoubles.CreateRequest();

        // addressType is hidden — binding [3] must be HiddenPreserved.
        _dispositions = ProfileBindingClassificationCore.ClassifyBindingsWithExplicitHiddenPaths(
            plan,
            collectionTable,
            request,
            resolverOwnedIndices,
            hiddenMemberPaths: ["addressType"]
        );
    }

    [Test]
    public void It_classifies_hidden_scalar_as_HiddenPreserved() =>
        _dispositions[3].Should().Be(RootBindingDisposition.HiddenPreserved);

    [Test]
    public void It_classifies_storage_managed_bindings_as_StorageManaged() =>
        _dispositions[0].Should().Be(RootBindingDisposition.StorageManaged);
}
