// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Unit.Profile.ProfileTestDoubles;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_when_buffer_exists_but_request_scope_state_is_null
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var scenario = SeparateScopeInstanceScenario.Create(
            requestScope: null,
            storedScope: null,
            buffer: SeparateScopeInstanceScenario.BufferKind.Present,
            currentRow: SeparateScopeInstanceScenario.CurrentRowKind.Absent
        );

        _act = () =>
            scenario.Synthesizer.SynthesizeSeparateScopeInstance(
                scenario.Request,
                scenario.ExtensionPlan,
                scenario.ScopeAddress,
                scenario.ParentPhysicalIdentityValues,
                scenario.Buffer,
                scenario.ScopedRequestNode,
                requestScope: null,
                storedScope: null,
                scenario.CurrentRowProjection,
                scenario.ResolvedReferenceLookups
            );
    }

    [Test]
    public void It_fails_closed_with_request_scope_contract_message() =>
        _act.Should().Throw<InvalidOperationException>().WithMessage("*RequestScopeStates has no entry*");
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_when_current_row_exists_but_stored_scope_state_is_null
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var requestScope = RequestVisibleAbsentScope(SeparateScopeInstanceScenario.Scope);
        var scenario = SeparateScopeInstanceScenario.Create(
            requestScope,
            storedScope: null,
            buffer: SeparateScopeInstanceScenario.BufferKind.Present,
            currentRow: SeparateScopeInstanceScenario.CurrentRowKind.Present
        );

        _act = () =>
            scenario.Synthesizer.SynthesizeSeparateScopeInstance(
                scenario.Request,
                scenario.ExtensionPlan,
                scenario.ScopeAddress,
                scenario.ParentPhysicalIdentityValues,
                scenario.Buffer,
                scenario.ScopedRequestNode,
                requestScope,
                storedScope: null,
                scenario.CurrentRowProjection,
                scenario.ResolvedReferenceLookups
            );
    }

    [Test]
    public void It_fails_closed_with_stored_scope_contract_message() =>
        _act.Should().Throw<InvalidOperationException>().WithMessage("*StoredScopeStates has no entry*");
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_when_visible_absent_has_no_current_row
{
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var requestScope = RequestVisibleAbsentScope(SeparateScopeInstanceScenario.Scope);
        var scenario = SeparateScopeInstanceScenario.Create(
            requestScope,
            storedScope: null,
            buffer: SeparateScopeInstanceScenario.BufferKind.Absent,
            currentRow: SeparateScopeInstanceScenario.CurrentRowKind.Absent
        );

        _result = scenario.Synthesizer.SynthesizeSeparateScopeInstance(
            scenario.Request,
            scenario.ExtensionPlan,
            scenario.ScopeAddress,
            scenario.ParentPhysicalIdentityValues,
            scenario.Buffer,
            scenario.ScopedRequestNode,
            requestScope,
            storedScope: null,
            scenario.CurrentRowProjection,
            scenario.ResolvedReferenceLookups
        );
    }

    [Test]
    public void It_skips_without_invoking_the_decider() => _result.IsSkipped.Should().BeTrue();
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_when_hidden_request_has_no_current_row
{
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var requestScope = RequestHiddenScope(SeparateScopeInstanceScenario.Scope);
        var scenario = SeparateScopeInstanceScenario.Create(
            requestScope,
            storedScope: null,
            buffer: SeparateScopeInstanceScenario.BufferKind.Absent,
            currentRow: SeparateScopeInstanceScenario.CurrentRowKind.Absent
        );

        _result = scenario.Synthesizer.SynthesizeSeparateScopeInstance(
            scenario.Request,
            scenario.ExtensionPlan,
            scenario.ScopeAddress,
            scenario.ParentPhysicalIdentityValues,
            scenario.Buffer,
            scenario.ScopedRequestNode,
            requestScope,
            storedScope: null,
            scenario.CurrentRowProjection,
            scenario.ResolvedReferenceLookups
        );
    }

    [Test]
    public void It_skips_without_invoking_the_decider() => _result.IsSkipped.Should().BeTrue();
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_when_visible_present_has_no_current_row
{
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var requestScope = RequestVisiblePresentScope(SeparateScopeInstanceScenario.Scope, creatable: true);
        var scenario = SeparateScopeInstanceScenario.Create(
            requestScope,
            storedScope: null,
            buffer: SeparateScopeInstanceScenario.BufferKind.Present,
            currentRow: SeparateScopeInstanceScenario.CurrentRowKind.Absent
        );

        _result = scenario.Synthesizer.SynthesizeSeparateScopeInstance(
            scenario.Request,
            scenario.ExtensionPlan,
            scenario.ScopeAddress,
            scenario.ParentPhysicalIdentityValues,
            scenario.Buffer,
            scenario.ScopedRequestNode,
            requestScope,
            storedScope: null,
            scenario.CurrentRowProjection,
            scenario.ResolvedReferenceLookups
        );
    }

    [Test]
    public void It_routes_to_insert() => _result.Outcome.Should().Be(ProfileSeparateTableMergeOutcome.Insert);

    [Test]
    public void It_treats_null_current_row_projection_as_absent() =>
        _result.TableState!.CurrentRows.Should().BeEmpty();
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_when_visible_present_is_not_creatable_and_has_no_current_row
{
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var requestScope = RequestVisiblePresentScope(SeparateScopeInstanceScenario.Scope, creatable: false);
        var scenario = SeparateScopeInstanceScenario.Create(
            requestScope,
            storedScope: null,
            buffer: SeparateScopeInstanceScenario.BufferKind.Present,
            currentRow: SeparateScopeInstanceScenario.CurrentRowKind.Absent
        );

        _result = scenario.Synthesizer.SynthesizeSeparateScopeInstance(
            scenario.Request,
            scenario.ExtensionPlan,
            scenario.ScopeAddress,
            scenario.ParentPhysicalIdentityValues,
            scenario.Buffer,
            scenario.ScopedRequestNode,
            requestScope,
            storedScope: null,
            scenario.CurrentRowProjection,
            scenario.ResolvedReferenceLookups
        );
    }

    [Test]
    public void It_routes_to_reject_create_denied() =>
        _result.Outcome.Should().Be(ProfileSeparateTableMergeOutcome.RejectCreateDenied);

    [Test]
    public void It_returns_a_creatability_rejection_for_the_scope()
    {
        _result.Rejection.Should().NotBeNull();
        _result.Rejection!.ScopeJsonScope.Should().Be(SeparateScopeInstanceScenario.Scope);
    }
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_when_visible_absent_has_visible_current_row
{
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var requestScope = RequestVisibleAbsentScope(SeparateScopeInstanceScenario.Scope);
        var storedScope = StoredVisiblePresentScope(SeparateScopeInstanceScenario.Scope);
        var scenario = SeparateScopeInstanceScenario.Create(
            requestScope,
            storedScope,
            buffer: SeparateScopeInstanceScenario.BufferKind.Present,
            currentRow: SeparateScopeInstanceScenario.CurrentRowKind.Present
        );

        _result = scenario.Synthesizer.SynthesizeSeparateScopeInstance(
            scenario.Request,
            scenario.ExtensionPlan,
            scenario.ScopeAddress,
            scenario.ParentPhysicalIdentityValues,
            scenario.Buffer,
            scenario.ScopedRequestNode,
            requestScope,
            storedScope,
            scenario.CurrentRowProjection,
            scenario.ResolvedReferenceLookups
        );
    }

    [Test]
    public void It_routes_to_delete() => _result.Outcome.Should().Be(ProfileSeparateTableMergeOutcome.Delete);

    [Test]
    public void It_treats_non_null_current_row_projection_as_present()
    {
        _result.TableState!.CurrentRows.Should().ContainSingle();
        _result.TableState.MergedRows.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_when_hidden_stored_scope_has_current_row
{
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var requestScope = RequestHiddenScope(SeparateScopeInstanceScenario.Scope);
        var storedScope = StoredHiddenScope(SeparateScopeInstanceScenario.Scope);
        var scenario = SeparateScopeInstanceScenario.Create(
            requestScope,
            storedScope,
            buffer: SeparateScopeInstanceScenario.BufferKind.Absent,
            currentRow: SeparateScopeInstanceScenario.CurrentRowKind.Present
        );

        _result = scenario.Synthesizer.SynthesizeSeparateScopeInstance(
            scenario.Request,
            scenario.ExtensionPlan,
            scenario.ScopeAddress,
            scenario.ParentPhysicalIdentityValues,
            scenario.Buffer,
            scenario.ScopedRequestNode,
            requestScope,
            storedScope,
            scenario.CurrentRowProjection,
            scenario.ResolvedReferenceLookups
        );
    }

    [Test]
    public void It_routes_to_preserve() =>
        _result.Outcome.Should().Be(ProfileSeparateTableMergeOutcome.Preserve);

    [Test]
    public void It_emits_the_current_row_as_the_merged_row()
    {
        var currentRow = _result.TableState!.CurrentRows.Should().ContainSingle().Subject;
        var mergedRow = _result.TableState.MergedRows.Should().ContainSingle().Subject;
        mergedRow.Should().BeSameAs(currentRow);
    }
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_when_visible_present_has_no_buffer
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var requestScope = RequestVisiblePresentScope(SeparateScopeInstanceScenario.Scope, creatable: true);
        var scenario = SeparateScopeInstanceScenario.Create(
            requestScope,
            storedScope: null,
            buffer: SeparateScopeInstanceScenario.BufferKind.Absent,
            currentRow: SeparateScopeInstanceScenario.CurrentRowKind.Absent
        );

        _act = () =>
            scenario.Synthesizer.SynthesizeSeparateScopeInstance(
                scenario.Request,
                scenario.ExtensionPlan,
                scenario.ScopeAddress,
                scenario.ParentPhysicalIdentityValues,
                scenario.Buffer,
                scenario.ScopedRequestNode,
                requestScope,
                storedScope: null,
                scenario.CurrentRowProjection,
                scenario.ResolvedReferenceLookups
            );
    }

    [Test]
    public void It_fails_closed_with_missing_buffer_contract_message() =>
        _act.Should().Throw<InvalidOperationException>().WithMessage("*no matching buffer*");
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_when_update_has_null_scoped_request_node
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var requestScope = RequestVisiblePresentScope(SeparateScopeInstanceScenario.Scope, creatable: true);
        var storedScope = StoredVisiblePresentScope(SeparateScopeInstanceScenario.Scope);
        var scenario = SeparateScopeInstanceScenario.Create(
            requestScope,
            storedScope,
            buffer: SeparateScopeInstanceScenario.BufferKind.Present,
            currentRow: SeparateScopeInstanceScenario.CurrentRowKind.Present,
            includeScopedRequestNode: false
        );

        _act = () =>
            scenario.Synthesizer.SynthesizeSeparateScopeInstance(
                scenario.Request,
                scenario.ExtensionPlan,
                scenario.ScopeAddress,
                scenario.ParentPhysicalIdentityValues,
                scenario.Buffer,
                scopedRequestNode: null,
                requestScope,
                storedScope,
                scenario.CurrentRowProjection,
                scenario.ResolvedReferenceLookups
            );
    }

    [Test]
    public void It_fails_closed_before_building_the_resolver_context() =>
        _act.Should().Throw<InvalidOperationException>().WithMessage("*scoped request node*");
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_update_with_sibling_scope_state_instances
{
    private const string ParentScope = "$.parents[*]";
    private const string AlignedScope = "$.parents[*]._ext.aligned";
    private const long DocumentId = 345L;

    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionJsonScope: AlignedScope,
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: $"{AlignedScope}.favoriteColor"
            )
        );
        var rootPlan = plan.TablePlansInDependencyOrder[0];
        var separateTable = plan.TablePlansInDependencyOrder[1];

        var addressA = ScopeAddressForParent("A");
        var addressB = ScopeAddressForParent("B");
        var requestA = new RequestScopeState(addressA, ProfileVisibilityKind.VisiblePresent, true);
        var requestB = new RequestScopeState(addressB, ProfileVisibilityKind.VisiblePresent, true);
        var storedA = new StoredScopeState(addressA, ProfileVisibilityKind.VisiblePresent, ["favoriteColor"]);
        var storedB = new StoredScopeState(addressB, ProfileVisibilityKind.VisiblePresent, []);

        var body = new JsonObject();
        var profileRequest = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            RequestVisiblePresentScope("$"),
            requestA,
            requestB
        );
        var context = CreateContext(
            profileRequest,
            visibleStoredBody: null,
            StoredVisiblePresentScope("$"),
            storedA,
            storedB
        );

        var extensionValues = BuildValues(separateTable, [DocumentId, "Blue"]);
        var rootExtensionRow = new RootExtensionWriteRowBuffer(separateTable, extensionValues);
        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                BuildValues(rootPlan, ["Ada"]),
                rootExtensionRows: [rootExtensionRow]
            )
        );
        var currentState = BuildCurrentStateWithRootAndExtensionRow(
            plan,
            rootRowValues: ["AdaStored"],
            extensionRowValues: [DocumentId, "Red"]
        );
        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: profileRequest,
            profileAppliedContext: context,
            resolvedReferences: EmptyResolvedReferenceSet()
        );

        _result = BuildProfileSynthesizer()
            .SynthesizeSeparateScopeInstance(
                mergeRequest,
                separateTable,
                addressB,
                [new FlattenedWriteValue.Literal(DocumentId)],
                SeparateScopeBuffer.From(rootExtensionRow),
                new JsonObject { ["favoriteColor"] = "Blue" },
                requestB,
                storedB,
                BuildCurrentProjection(separateTable, [DocumentId, "Red"]),
                EmptyResolvedReferenceLookups(plan)
            );
    }

    [Test]
    public void It_routes_to_update() => _result.Outcome.Should().Be(ProfileSeparateTableMergeOutcome.Update);

    [Test]
    public void It_uses_the_matching_instances_scope_state_when_building_the_update()
    {
        var mergedRow = _result.TableState!.MergedRows.Should().ContainSingle().Subject;
        ((FlattenedWriteValue.Literal)mergedRow.Values[1]).Value.Should().Be("Blue");
    }

    private static ScopeInstanceAddress ScopeAddressForParent(string parentId) =>
        new(
            AlignedScope,
            [
                new AncestorCollectionInstance(
                    ParentScope,
                    [new SemanticIdentityPart("$.parentId", JsonValue.Create(parentId), IsPresent: true)]
                ),
            ]
        );

    private static ImmutableArray<FlattenedWriteValue> BuildValues(
        TableWritePlan tablePlan,
        object?[] literals
    )
    {
        var values = new FlattenedWriteValue[tablePlan.ColumnBindings.Length];
        for (var i = 0; i < tablePlan.ColumnBindings.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(i < literals.Length ? literals[i] : null);
        }
        return values.ToImmutableArray();
    }

    private static CurrentSeparateScopeRowProjection BuildCurrentProjection(
        TableWritePlan tablePlan,
        object?[] row
    )
    {
        var projected = RelationalWriteMergeSupport.ProjectCurrentRows(tablePlan, [row])[0];
        var byColumn = RelationalWriteMergeSupport.BuildCurrentRowByColumnName(tablePlan.TableModel, row);
        return new CurrentSeparateScopeRowProjection(
            projected,
            byColumn,
            [new FlattenedWriteValue.Literal(DocumentId)]
        );
    }
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_for_aligned_extension_visible_present_no_current_row
{
    private AlignedSeparateScopeInstanceScenario _scenario = null!;
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        _scenario = AlignedSeparateScopeInstanceScenario.Create(
            requestScope: AlignedSeparateScopeInstanceScenario.BuildRequestScope(
                ProfileVisibilityKind.VisiblePresent,
                true
            ),
            storedScope: null,
            buffer: AlignedSeparateScopeInstanceScenario.BufferKind.Present,
            currentRow: AlignedSeparateScopeInstanceScenario.CurrentRowKind.Absent
        );

        _result = _scenario.Invoke();
    }

    [Test]
    public void It_routes_to_insert() => _result.Outcome.Should().Be(ProfileSeparateTableMergeOutcome.Insert);

    [Test]
    public void It_rewrites_the_parent_key_from_the_parent_collection_identity()
    {
        var mergedRow = _result.TableState!.MergedRows.Should().ContainSingle().Subject;
        ((FlattenedWriteValue.Literal)mergedRow.Values[0])
            .Value.Should()
            .Be(AlignedSeparateScopeInstanceScenario.ParentItemId);
        ((FlattenedWriteValue.Literal)mergedRow.Values[1]).Value.Should().Be("Blue");
    }
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_for_aligned_extension_visible_present_stored_matched
{
    private AlignedSeparateScopeInstanceScenario _scenario = null!;
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        _scenario = AlignedSeparateScopeInstanceScenario.Create(
            requestScope: AlignedSeparateScopeInstanceScenario.BuildRequestScope(
                ProfileVisibilityKind.VisiblePresent,
                true
            ),
            storedScope: AlignedSeparateScopeInstanceScenario.BuildStoredScope(
                ProfileVisibilityKind.VisiblePresent
            ),
            buffer: AlignedSeparateScopeInstanceScenario.BufferKind.Present,
            currentRow: AlignedSeparateScopeInstanceScenario.CurrentRowKind.Present
        );

        _result = _scenario.Invoke();
    }

    [Test]
    public void It_routes_to_update() => _result.Outcome.Should().Be(ProfileSeparateTableMergeOutcome.Update);

    [Test]
    public void It_rewrites_the_parent_key_and_overlays_visible_values()
    {
        _result.TableState!.CurrentRows.Should().ContainSingle();
        var mergedRow = _result.TableState.MergedRows.Should().ContainSingle().Subject;
        ((FlattenedWriteValue.Literal)mergedRow.Values[0])
            .Value.Should()
            .Be(AlignedSeparateScopeInstanceScenario.ParentItemId);
        ((FlattenedWriteValue.Literal)mergedRow.Values[1]).Value.Should().Be("Blue");
    }
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_for_aligned_extension_visible_absent_stored_matched
{
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var scenario = AlignedSeparateScopeInstanceScenario.Create(
            requestScope: AlignedSeparateScopeInstanceScenario.BuildRequestScope(
                ProfileVisibilityKind.VisibleAbsent,
                true
            ),
            storedScope: AlignedSeparateScopeInstanceScenario.BuildStoredScope(
                ProfileVisibilityKind.VisiblePresent
            ),
            buffer: AlignedSeparateScopeInstanceScenario.BufferKind.Absent,
            currentRow: AlignedSeparateScopeInstanceScenario.CurrentRowKind.Present
        );

        _result = scenario.Invoke();
    }

    [Test]
    public void It_routes_to_delete() => _result.Outcome.Should().Be(ProfileSeparateTableMergeOutcome.Delete);

    [Test]
    public void It_emits_current_rows_without_merged_rows()
    {
        _result.TableState!.CurrentRows.Should().ContainSingle();
        _result.TableState.MergedRows.Should().BeEmpty();
    }
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_for_aligned_extension_hidden_stored_scope
{
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var scenario = AlignedSeparateScopeInstanceScenario.Create(
            requestScope: AlignedSeparateScopeInstanceScenario.BuildRequestScope(
                ProfileVisibilityKind.Hidden,
                false
            ),
            storedScope: AlignedSeparateScopeInstanceScenario.BuildStoredScope(ProfileVisibilityKind.Hidden),
            buffer: AlignedSeparateScopeInstanceScenario.BufferKind.Absent,
            currentRow: AlignedSeparateScopeInstanceScenario.CurrentRowKind.Present
        );

        _result = scenario.Invoke();
    }

    [Test]
    public void It_routes_to_preserve() =>
        _result.Outcome.Should().Be(ProfileSeparateTableMergeOutcome.Preserve);

    [Test]
    public void It_emits_the_current_row_as_the_merged_row()
    {
        var currentRow = _result.TableState!.CurrentRows.Should().ContainSingle().Subject;
        var mergedRow = _result.TableState.MergedRows.Should().ContainSingle().Subject;
        mergedRow.Should().BeSameAs(currentRow);
    }
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_for_aligned_extension_visible_present_not_creatable
{
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var scenario = AlignedSeparateScopeInstanceScenario.Create(
            requestScope: AlignedSeparateScopeInstanceScenario.BuildRequestScope(
                ProfileVisibilityKind.VisiblePresent,
                false
            ),
            storedScope: AlignedSeparateScopeInstanceScenario.BuildStoredScope(
                ProfileVisibilityKind.VisibleAbsent
            ),
            buffer: AlignedSeparateScopeInstanceScenario.BufferKind.Present,
            currentRow: AlignedSeparateScopeInstanceScenario.CurrentRowKind.Absent
        );

        _result = scenario.Invoke();
    }

    [Test]
    public void It_routes_to_reject_create_denied() =>
        _result.Outcome.Should().Be(ProfileSeparateTableMergeOutcome.RejectCreateDenied);

    [Test]
    public void It_returns_a_creatability_rejection_for_the_aligned_scope()
    {
        _result.Rejection.Should().NotBeNull();
        _result.Rejection!.ScopeJsonScope.Should().Be(AlignedExtensionScopeTopologyBuilders.AlignedScope);
    }
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_for_aligned_extension_visible_absent_no_current_row
{
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var scenario = AlignedSeparateScopeInstanceScenario.Create(
            requestScope: AlignedSeparateScopeInstanceScenario.BuildRequestScope(
                ProfileVisibilityKind.VisibleAbsent,
                true
            ),
            storedScope: null,
            buffer: AlignedSeparateScopeInstanceScenario.BufferKind.Absent,
            currentRow: AlignedSeparateScopeInstanceScenario.CurrentRowKind.Absent
        );

        _result = scenario.Invoke();
    }

    [Test]
    public void It_skips_without_invoking_the_decider() => _result.IsSkipped.Should().BeTrue();
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_for_aligned_extension_hidden_request_with_request_data
{
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var scenario = AlignedSeparateScopeInstanceScenario.Create(
            requestScope: AlignedSeparateScopeInstanceScenario.BuildRequestScope(
                ProfileVisibilityKind.Hidden,
                false
            ),
            storedScope: null,
            buffer: AlignedSeparateScopeInstanceScenario.BufferKind.Present,
            currentRow: AlignedSeparateScopeInstanceScenario.CurrentRowKind.Absent
        );

        _result = scenario.Invoke();
    }

    [Test]
    public void It_rejects_hidden_request_data() =>
        _result.Outcome.Should().Be(ProfileSeparateTableMergeOutcome.RejectCreateDenied);

    [Test]
    public void It_returns_a_profile_policy_rejection_for_the_aligned_scope()
    {
        _result.Rejection.Should().NotBeNull();
        _result.Rejection!.ScopeJsonScope.Should().Be(AlignedExtensionScopeTopologyBuilders.AlignedScope);
        _result.Rejection.Message.Should().Contain("forbids writing hidden scope");
    }
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_for_aligned_extension_hidden_request_with_collection_only_buffer
{
    private SeparateScopeSynthesisResult _result;

    [SetUp]
    public void Setup()
    {
        var scenario = AlignedSeparateScopeInstanceScenario.Create(
            requestScope: AlignedSeparateScopeInstanceScenario.BuildRequestScope(
                ProfileVisibilityKind.Hidden,
                false
            ),
            storedScope: null,
            buffer: AlignedSeparateScopeInstanceScenario.BufferKind.PresentCollectionOnly,
            currentRow: AlignedSeparateScopeInstanceScenario.CurrentRowKind.Absent
        );

        _result = scenario.Invoke();
    }

    [Test]
    public void It_rejects_hidden_request_data() =>
        _result.Outcome.Should().Be(ProfileSeparateTableMergeOutcome.RejectCreateDenied);

    [Test]
    public void It_returns_a_profile_policy_rejection_for_the_aligned_scope()
    {
        _result.Rejection.Should().NotBeNull();
        _result.Rejection!.ScopeJsonScope.Should().Be(AlignedExtensionScopeTopologyBuilders.AlignedScope);
        _result.Rejection.Message.Should().Contain("forbids writing hidden scope");
    }
}

[TestFixture]
public class Given_SynthesizeSeparateScopeInstance_for_aligned_extension_visible_present_with_no_buffer
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        var scenario = AlignedSeparateScopeInstanceScenario.Create(
            requestScope: AlignedSeparateScopeInstanceScenario.BuildRequestScope(
                ProfileVisibilityKind.VisiblePresent,
                true
            ),
            storedScope: null,
            buffer: AlignedSeparateScopeInstanceScenario.BufferKind.Absent,
            currentRow: AlignedSeparateScopeInstanceScenario.CurrentRowKind.Absent
        );

        _act = () => scenario.Invoke();
    }

    [Test]
    public void It_fails_closed_with_missing_buffer_contract_message() =>
        _act.Should().Throw<InvalidOperationException>().WithMessage("*no matching buffer*");
}

internal sealed record SeparateScopeInstanceScenario(
    RelationalWriteProfileMergeSynthesizer Synthesizer,
    RelationalWriteProfileMergeRequest Request,
    ResourceWritePlan Plan,
    TableWritePlan ExtensionPlan,
    ScopeInstanceAddress ScopeAddress,
    ImmutableArray<FlattenedWriteValue> ParentPhysicalIdentityValues,
    SeparateScopeBuffer? Buffer,
    JsonNode? ScopedRequestNode,
    CurrentSeparateScopeRowProjection? CurrentRowProjection,
    FlatteningResolvedReferenceLookupSet ResolvedReferenceLookups
)
{
    public const string Scope = "$._ext.sample";
    private const long DocumentId = 345L;

    public enum BufferKind
    {
        Absent,
        Present,
    }

    public enum CurrentRowKind
    {
        Absent,
        Present,
    }

    public static SeparateScopeInstanceScenario Create(
        RequestScopeState? requestScope,
        StoredScopeState? storedScope,
        BufferKind buffer,
        CurrentRowKind currentRow,
        bool includeScopedRequestNode = true
    )
    {
        var plan = BuildRootPlusRootExtensionPlan(
            extensionBindings: new RootExtensionBindingSpec(
                "FavoriteColor",
                RootExtensionBindingKind.Scalar,
                RelativePath: "$._ext.sample.favoriteColor"
            )
        );
        var rootPlan = plan.TablePlansInDependencyOrder[0];
        var extensionPlan = plan.TablePlansInDependencyOrder[1];
        var rootValues = BuildValues(rootPlan, ["Ada"]);
        var extensionValues = BuildValues(extensionPlan, [DocumentId, "Blue"]);
        var rootExtensionRow = new RootExtensionWriteRowBuffer(extensionPlan, extensionValues);

        var body = new JsonObject
        {
            ["firstName"] = "Ada",
            ["_ext"] = new JsonObject { ["sample"] = new JsonObject { ["favoriteColor"] = "Blue" } },
        };

        var requestStates = new List<RequestScopeState> { RequestVisiblePresentScope("$") };
        if (requestScope is not null)
        {
            requestStates.Add(requestScope);
        }

        var profileRequest = CreateRequest(
            writableBody: body,
            rootResourceCreatable: true,
            [.. requestStates]
        );

        var currentState =
            currentRow is CurrentRowKind.Present
                ? BuildCurrentStateWithRootAndExtensionRow(
                    plan,
                    rootRowValues: ["AdaStored"],
                    extensionRowValues: [DocumentId, "Red"]
                )
                : BuildCurrentStateWithRootAndExtensionRow(
                    plan,
                    rootRowValues: ["AdaStored"],
                    extensionRowValues: null
                );

        var storedStates = new List<StoredScopeState> { StoredVisiblePresentScope("$") };
        if (storedScope is not null)
        {
            storedStates.Add(storedScope);
        }

        var appliedContext = CreateContext(profileRequest, visibleStoredBody: null, [.. storedStates]);

        var flattened = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                rootValues,
                rootExtensionRows: buffer is BufferKind.Present ? [rootExtensionRow] : []
            )
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: flattened,
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: profileRequest,
            profileAppliedContext: appliedContext,
            resolvedReferences: EmptyResolvedReferenceSet()
        );

        return new SeparateScopeInstanceScenario(
            BuildProfileSynthesizer(),
            mergeRequest,
            plan,
            extensionPlan,
            new ScopeInstanceAddress(Scope, []),
            [new FlattenedWriteValue.Literal(DocumentId)],
            buffer is BufferKind.Present ? SeparateScopeBuffer.From(rootExtensionRow) : null,
            includeScopedRequestNode ? body["_ext"]!["sample"] : null,
            currentRow is CurrentRowKind.Present
                ? BuildCurrentProjection(extensionPlan, [DocumentId, "Red"])
                : null,
            EmptyResolvedReferenceLookups(plan)
        );
    }

    private static ImmutableArray<FlattenedWriteValue> BuildValues(
        TableWritePlan tablePlan,
        object?[] literals
    )
    {
        var values = new FlattenedWriteValue[tablePlan.ColumnBindings.Length];
        for (var i = 0; i < tablePlan.ColumnBindings.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(i < literals.Length ? literals[i] : null);
        }
        return values.ToImmutableArray();
    }

    private static CurrentSeparateScopeRowProjection BuildCurrentProjection(
        TableWritePlan tablePlan,
        object?[] row
    )
    {
        var projected = RelationalWriteMergeSupport.ProjectCurrentRows(tablePlan, [row])[0];
        var byColumn = RelationalWriteMergeSupport.BuildCurrentRowByColumnName(tablePlan.TableModel, row);
        return new CurrentSeparateScopeRowProjection(
            projected,
            byColumn,
            [new FlattenedWriteValue.Literal(DocumentId)]
        );
    }
}

internal sealed record AlignedSeparateScopeInstanceScenario(
    RelationalWriteProfileMergeSynthesizer Synthesizer,
    RelationalWriteProfileMergeRequest Request,
    ResourceWritePlan Plan,
    TableWritePlan AlignedPlan,
    ScopeInstanceAddress ScopeAddress,
    ImmutableArray<FlattenedWriteValue> ParentPhysicalIdentityValues,
    SeparateScopeBuffer? Buffer,
    JsonNode? ScopedRequestNode,
    RequestScopeState RequestScope,
    StoredScopeState? StoredScope,
    CurrentSeparateScopeRowProjection? CurrentRowProjection,
    FlatteningResolvedReferenceLookupSet ResolvedReferenceLookups
)
{
    public const string ParentCode = "A";
    public const long DocumentId = 345L;
    public const long ParentItemId = 100L;

    public enum BufferKind
    {
        Absent,
        Present,
        PresentCollectionOnly,
    }

    public enum CurrentRowKind
    {
        Absent,
        Present,
    }

    public SeparateScopeSynthesisResult Invoke() =>
        Synthesizer.SynthesizeSeparateScopeInstance(
            Request,
            AlignedPlan,
            ScopeAddress,
            ParentPhysicalIdentityValues,
            Buffer,
            ScopedRequestNode,
            RequestScope,
            StoredScope,
            CurrentRowProjection,
            ResolvedReferenceLookups
        );

    public static RequestScopeState BuildRequestScope(ProfileVisibilityKind visibility, bool creatable) =>
        AlignedExtensionScopeTopologyBuilders.BuildRequestScopeState(ParentCode, visibility, creatable);

    public static StoredScopeState BuildStoredScope(ProfileVisibilityKind visibility) =>
        AlignedExtensionScopeTopologyBuilders.BuildStoredScopeState(ParentCode, visibility);

    public static AlignedSeparateScopeInstanceScenario Create(
        RequestScopeState requestScope,
        StoredScopeState? storedScope,
        BufferKind buffer,
        CurrentRowKind currentRow
    )
    {
        var (plan, parentsPlan, alignedPlan) =
            AlignedExtensionScopeTopologyBuilders.BuildRootParentsAndAlignedScopePlan();
        var rootPlan = plan.TablePlansInDependencyOrder[0];

        var alignedValues =
            buffer is BufferKind.PresentCollectionOnly
                ? BuildValues(alignedPlan, [])
                : BuildValues(alignedPlan, [null, "Blue"]);

        var candidateChildPlan = parentsPlan;
        ImmutableArray<CollectionWriteCandidate> alignedChildCandidates =
            buffer is BufferKind.PresentCollectionOnly
                ?
                [
                    new CollectionWriteCandidate(
                        tableWritePlan: candidateChildPlan,
                        ordinalPath: [0],
                        requestOrder: 0,
                        values: BuildValues(candidateChildPlan, [ParentItemId, DocumentId, 1, ParentCode]),
                        semanticIdentityValues: [ParentCode]
                    ),
                ]
                : [];

        var alignedScopeData = new CandidateAttachedAlignedScopeData(
            alignedPlan,
            alignedValues,
            alignedChildCandidates
        );

        var body = new JsonObject
        {
            ["parents"] = new JsonArray(
                new JsonObject
                {
                    ["identityField0"] = ParentCode,
                    ["_ext"] = new JsonObject { ["aligned"] = new JsonObject { ["favoriteColor"] = "Blue" } },
                }
            ),
        };

        var profileRequest = AlignedExtensionScopeTopologyBuilders.BuildRequest(
            body,
            [NestedTopologyBuilders.BuildParentRequestItem(ParentCode, arrayIndex: 0)],
            [requestScope]
        );

        var appliedContext = AlignedExtensionScopeTopologyBuilders.BuildContext(
            profileRequest,
            [NestedTopologyBuilders.BuildParentStoredRow(ParentCode)],
            storedScope is null ? [] : [storedScope]
        );

        var currentState = AlignedExtensionScopeTopologyBuilders.BuildCurrentState(
            rootPlan,
            parentsPlan,
            alignedPlan,
            DocumentId,
            parentRows:
            [
                [ParentItemId, DocumentId, 1, ParentCode],
            ],
            alignedRows: currentRow is CurrentRowKind.Present
                ?
                [
                    [ParentItemId, "Red"],
                ]
                : Array.Empty<object?[]>()
        );

        var mergeRequest = new RelationalWriteProfileMergeRequest(
            writePlan: plan,
            flattenedWriteSet: new FlattenedWriteSet(
                new RootWriteRowBuffer(rootPlan, BuildValues(rootPlan, [DocumentId]))
            ),
            writableRequestBody: body,
            currentState: currentState,
            profileRequest: profileRequest,
            profileAppliedContext: appliedContext,
            resolvedReferences: EmptyResolvedReferenceSet()
        );

        return new AlignedSeparateScopeInstanceScenario(
            BuildProfileSynthesizer(),
            mergeRequest,
            plan,
            alignedPlan,
            AlignedExtensionScopeTopologyBuilders.AlignedScopeAddress(ParentCode),
            [new FlattenedWriteValue.Literal(ParentItemId)],
            buffer is BufferKind.Present or BufferKind.PresentCollectionOnly
                ? SeparateScopeBuffer.From(alignedScopeData)
                : null,
            body["parents"]![0]!["_ext"]!["aligned"],
            requestScope,
            storedScope,
            currentRow is CurrentRowKind.Present ? BuildCurrentProjection(alignedPlan) : null,
            EmptyResolvedReferenceLookups(plan)
        );
    }

    private static ImmutableArray<FlattenedWriteValue> BuildValues(
        TableWritePlan tablePlan,
        object?[] literals
    )
    {
        var values = new FlattenedWriteValue[tablePlan.ColumnBindings.Length];
        for (var i = 0; i < tablePlan.ColumnBindings.Length; i++)
        {
            values[i] = new FlattenedWriteValue.Literal(i < literals.Length ? literals[i] : null);
        }
        return values.ToImmutableArray();
    }

    private static CurrentSeparateScopeRowProjection BuildCurrentProjection(TableWritePlan tablePlan)
    {
        var projected = RelationalWriteMergeSupport.ProjectCurrentRows(
            tablePlan,
            [
                [ParentItemId, "Red"],
            ]
        )[0];
        var byColumn = RelationalWriteMergeSupport.BuildCurrentRowByColumnName(
            tablePlan.TableModel,
            [ParentItemId, "Red"]
        );
        return new CurrentSeparateScopeRowProjection(
            projected,
            byColumn,
            [new FlattenedWriteValue.Literal(ParentItemId)]
        );
    }
}
