// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Relational_Write_Profile_Merge_Synthesizer
{
    private RelationalWriteMergeSynthesizer _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new RelationalWriteMergeSynthesizer();
    }

    [Test]
    public void It_overlays_hidden_scalar_from_current_state_on_root_table_update()
    {
        var fixture = CreateFixture();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    Literal("Updated Name"),
                    Literal("SHOULD_NOT_APPEAR"),
                ]
            )
        );
        var currentState = CreateCurrentState(
            fixture,
            rootRows:
            [
                [345L, "Original Name", "HIDDEN_CODE"],
            ]
        );

        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: ["$.code"]
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        result.TablesInDependencyOrder.Should().HaveCount(1);

        var rootState = result.TablesInDependencyOrder[0];
        rootState.Inserts.Should().BeEmpty();
        rootState.Updates.Should().ContainSingle();
        rootState.Deletes.Should().BeEmpty();
        rootState.PreservedRows.Should().BeEmpty();

        var update = rootState.Updates[0];
        // StorageManaged DocumentId passes through from request
        update.Values[0].Should().BeSameAs(FlattenedWriteValue.UnresolvedRootDocumentId.Instance);
        // Visible scalar uses request value
        LiteralValue(update.Values[1]).Should().Be("Updated Name");
        // Hidden scalar preserved from current state
        LiteralValue(update.Values[2]).Should().Be("HIDDEN_CODE");
    }

    [Test]
    public void It_inserts_root_table_on_create_with_no_current_state()
    {
        var fixture = CreateFixture();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    Literal("New Name"),
                    Literal("NEW_CODE"),
                ]
            )
        );

        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
        ]);

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                CurrentState: null,
                profileRequest,
                ProfileContext: null,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        result.TablesInDependencyOrder.Should().HaveCount(1);

        var rootState = result.TablesInDependencyOrder[0];
        rootState.Inserts.Should().ContainSingle();
        rootState.Updates.Should().BeEmpty();
        rootState.Deletes.Should().BeEmpty();

        var insert = rootState.Inserts[0];
        insert.Values[0].Should().BeSameAs(FlattenedWriteValue.UnresolvedRootDocumentId.Instance);
        LiteralValue(insert.Values[1]).Should().Be("New Name");
        LiteralValue(insert.Values[2]).Should().Be("NEW_CODE");
    }

    [Test]
    public void It_updates_extension_table_overlaying_hidden_value_when_scope_is_visible_present()
    {
        var fixture = CreateFixtureWithExtension();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("updated-ext"), Literal("SHOULD_NOT_APPEAR")]
                    ),
                ]
            )
        );
        var currentState = CreateCurrentStateWithExtension(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows:
            [
                [345L, "original-ext", "HIDDEN_EXT_VALUE"],
            ]
        );

        var extensionScope = "$._ext.sample";
        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
            new RequestScopeState(
                ScopeAddress(extensionScope),
                ProfileVisibilityKind.VisiblePresent,
                Creatable: true
            ),
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: ["$.hiddenExt"]
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var extensionState = result.TablesInDependencyOrder[1];
        extensionState.Inserts.Should().BeEmpty();
        extensionState.Updates.Should().ContainSingle();
        extensionState.Deletes.Should().BeEmpty();
        extensionState.PreservedRows.Should().BeEmpty();

        var update = extensionState.Updates[0];
        // ParentKeyPart / DocumentId: passes through from request
        LiteralValue(update.Values[0]).Should().Be(345L);
        // Visible scalar uses request value
        LiteralValue(update.Values[1]).Should().Be("updated-ext");
        // Hidden scalar preserved from current state
        LiteralValue(update.Values[2]).Should().Be("HIDDEN_EXT_VALUE");
    }

    [Test]
    public void It_deletes_extension_row_when_scope_is_visible_absent()
    {
        var fixture = CreateFixtureWithExtension();
        // No extension row in the flattened write set — the scope is absent from the request
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                rootExtensionRows:
                [
                    // Extension row is still present in the flattened write set buffer
                    // (the flattener always emits a row), but the profile scope state
                    // indicates VisibleAbsent, which drives the delete decision.
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("ignored-ext"), Literal("ignored-hidden")]
                    ),
                ]
            )
        );
        var currentState = CreateCurrentStateWithExtension(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows:
            [
                [345L, "stored-ext", "STORED_HIDDEN"],
            ]
        );

        var extensionScope = "$._ext.sample";
        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
            new RequestScopeState(
                ScopeAddress(extensionScope),
                ProfileVisibilityKind.VisibleAbsent,
                Creatable: false
            ),
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var extensionState = result.TablesInDependencyOrder[1];
        extensionState.Inserts.Should().BeEmpty();
        extensionState.Updates.Should().BeEmpty();
        extensionState.Deletes.Should().ContainSingle();
        extensionState.PreservedRows.Should().BeEmpty();
    }

    [Test]
    public void It_preserves_extension_row_when_scope_is_hidden()
    {
        var fixture = CreateFixtureWithExtension();
        // The flattened write set still has a root extension row buffer entry, but the scope is hidden
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("ignored"), Literal("ignored")]
                    ),
                ]
            )
        );
        var currentState = CreateCurrentStateWithExtension(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows:
            [
                [345L, "preserved-ext", "PRESERVED_HIDDEN"],
            ]
        );

        var extensionScope = "$._ext.sample";
        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
            // Hidden scopes don't appear in request scope states
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.Hidden,
                    HiddenMemberPaths: []
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var extensionState = result.TablesInDependencyOrder[1];
        extensionState.Inserts.Should().BeEmpty();
        extensionState.Updates.Should().BeEmpty();
        extensionState.Deletes.Should().BeEmpty();
        extensionState.PreservedRows.Should().ContainSingle();

        var preserved = extensionState.PreservedRows[0];
        LiteralValue(preserved.Values[0]).Should().Be(345L);
        LiteralValue(preserved.Values[1]).Should().Be("preserved-ext");
        LiteralValue(preserved.Values[2]).Should().Be("PRESERVED_HIDDEN");
    }

    [Test]
    public void It_populates_comparable_rowsets_for_no_op_detection_on_root_table_update()
    {
        var fixture = CreateFixture();
        // Update where visible value is identical to current
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    Literal("Same Name"),
                    Literal("SHOULD_NOT_APPEAR"),
                ]
            )
        );
        var currentState = CreateCurrentState(
            fixture,
            rootRows:
            [
                [345L, "Same Name", "HIDDEN_CODE"],
            ]
        );

        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: ["$.code"]
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var rootState = result.TablesInDependencyOrder[0];
        rootState.ComparableCurrentRowset.Should().ContainSingle();
        rootState.ComparableMergedRowset.Should().ContainSingle();

        // The current row comparable values include all binding values
        var currentComparable = rootState.ComparableCurrentRowset[0].ComparableValues;
        var mergedComparable = rootState.ComparableMergedRowset[0].ComparableValues;

        // Both should have 3 values (all bindings for a non-collection table)
        currentComparable.Should().HaveCount(3);
        mergedComparable.Should().HaveCount(3);

        // The visible name is the same, the hidden code is the same (preserved from current)
        // so the merged comparable should match the current comparable on visible+hidden values
        LiteralValue(currentComparable[1]).Should().Be("Same Name");
        LiteralValue(mergedComparable[1]).Should().Be("Same Name");
        LiteralValue(currentComparable[2]).Should().Be("HIDDEN_CODE");
        LiteralValue(mergedComparable[2]).Should().Be("HIDDEN_CODE");
    }

    // --- Key-unification tests ---

    [Test]
    public void It_preserves_key_unification_canonical_when_visible_member_absent_and_hidden_member_present_in_stored()
    {
        // Root table with key-unification: primaryType (hidden) and secondaryType (visible).
        // Request has secondaryType absent. Stored has primaryType present with value "StoredPrimary".
        // The canonical should be preserved from stored state since no visible member contributes.
        var rootPlan = CreateRootPlanWithKeyUnification();
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
        var writePlan = new ResourceWritePlan(resourceModel, [rootPlan]);

        // Request values: DocumentId placeholder, "Updated Name", null canonical (no visible member present),
        // false (primaryType absent from request - hidden), false (secondaryType absent from request - visible but absent)
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                rootPlan,
                [
                    FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                    Literal("Updated Name"),
                    Literal(null), // canonical - null because no visible member present
                    Literal(null), // primaryType presence - null (hidden, request doesn't have it)
                    Literal(null), // secondaryType presence - null (visible but absent)
                ]
            )
        );

        // Stored state: canonical is "StoredPrimary" (from hidden member), primaryType present, secondaryType absent
        var currentState = new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(
                    rootPlan.TableModel,
                    [
                        [345L, "Original Name", "StoredPrimary", true, false],
                    ]
                ),
            ]
        );

        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: ["$.primaryType"]
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                writePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var update = result.TablesInDependencyOrder[0].Updates[0];
        // Canonical (idx 2) should be preserved from stored state "StoredPrimary"
        LiteralValue(update.Values[2]).Should().Be("StoredPrimary");
        // Hidden member's synthetic presence (idx 3) preserved from stored state
        LiteralValue(update.Values[3]).Should().Be(true);
        // Visible member's presence (idx 4) stays as request value (null = absent)
        LiteralValue(update.Values[4]).Should().BeNull();
    }

    // --- Collection merge tests ---

    [Test]
    public void It_updates_matched_visible_collection_row_and_preserves_hidden_row()
    {
        var fixture = CreateCollectionFixture();

        // Request has 2 visible candidates matching the 2 visible rows
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: ["Period1"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("Period1"),
                            Literal("Updated Value1"),
                            Literal("SHOULD_NOT_APPEAR"),
                        ]
                    ),
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 1,
                        semanticIdentityValues: ["Period3"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("Period3"),
                            Literal("Updated Value3"),
                            Literal("SHOULD_NOT_APPEAR"),
                        ]
                    ),
                ]
            )
        );

        // 3 current rows: visible(0), hidden(1), visible(2)
        var currentState = CreateCurrentStateWithCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 100L, 0, "Period1", "Value1", "HIDDEN1"],
                [345L, 101L, 1, "Period2", "Value2", "HIDDEN2"],
                [345L, 102L, 2, "Period3", "Value3", "HIDDEN3"],
            ]
        );

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true)],
            [
                CreateVisibleRequestCollectionItem("$.classPeriods", "Period1"),
                CreateVisibleRequestCollectionItem("$.classPeriods", "Period3"),
            ]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            [
                CreateVisibleStoredCollectionRow("$.classPeriods", "Period1", ["$.hiddenField"]),
                CreateVisibleStoredCollectionRow("$.classPeriods", "Period3", ["$.hiddenField"]),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var collectionState = result.TablesInDependencyOrder[1];
        collectionState.Updates.Should().HaveCount(2);
        collectionState.PreservedRows.Should().ContainSingle();
        collectionState.Inserts.Should().BeEmpty();
        collectionState.Deletes.Should().BeEmpty();

        // Hidden row preserved with correct ordinal
        var preserved = collectionState.PreservedRows[0];
        LiteralValue(preserved.Values[3]).Should().Be("Period2");

        // Updates carry stable row identity from current state
        collectionState.Updates[0].StableRowIdentityValue.Should().Be(100L);
        collectionState.Updates[1].StableRowIdentityValue.Should().Be(102L);

        // Hidden values preserved on matched updates
        LiteralValue(collectionState.Updates[0].Values[5]).Should().Be("HIDDEN1");
        LiteralValue(collectionState.Updates[1].Values[5]).Should().Be("HIDDEN3");

        // Ordinals renumbered: update=0, hidden=1, update=2
        LiteralValue(collectionState.Updates[0].Values[2]).Should().Be(0);
        LiteralValue(preserved.Values[2]).Should().Be(1);
        LiteralValue(collectionState.Updates[1].Values[2]).Should().Be(2);
    }

    [Test]
    public void It_inserts_new_visible_row_when_no_match_exists()
    {
        var fixture = CreateCollectionFixture();

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: ["NewPeriod"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("NewPeriod"),
                            Literal("New Value"),
                            Literal("NewHidden"),
                        ]
                    ),
                ]
            )
        );

        // 1 current hidden row
        var currentState = CreateCurrentStateWithCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 100L, 0, "HiddenPeriod", "HiddenValue", "HIDDEN1"],
            ]
        );

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true)],
            [CreateVisibleRequestCollectionItem("$.classPeriods", "NewPeriod")]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            [] // No visible stored rows (hidden only)
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var collectionState = result.TablesInDependencyOrder[1];
        collectionState.Inserts.Should().ContainSingle();
        collectionState.PreservedRows.Should().ContainSingle();
        collectionState.Updates.Should().BeEmpty();
        collectionState.Deletes.Should().BeEmpty();

        // Hidden row at ordinal 0, insert appended at ordinal 1
        var preserved = collectionState.PreservedRows[0];
        LiteralValue(preserved.Values[2]).Should().Be(0);
        LiteralValue(preserved.Values[3]).Should().Be("HiddenPeriod");

        var insert = collectionState.Inserts[0];
        LiteralValue(insert.Values[2]).Should().Be(1);
        LiteralValue(insert.Values[3]).Should().Be("NewPeriod");
    }

    [Test]
    public void It_deletes_omitted_visible_row()
    {
        var fixture = CreateCollectionFixture();

        // Request has 0 visible candidates (visible row omitted)
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")]
            )
        );

        // 2 current rows: 1 visible, 1 hidden
        var currentState = CreateCurrentStateWithCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 100L, 0, "VisiblePeriod", "Value1", "Hidden1"],
                [345L, 101L, 1, "HiddenPeriod", "Value2", "Hidden2"],
            ]
        );

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true)],
            [] // No visible request items (omitted)
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            [CreateVisibleStoredCollectionRow("$.classPeriods", "VisiblePeriod", [])]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog:
                [
                    new CompiledScopeDescriptor("$", ScopeKind.Root, null, [], [], []),
                    new CompiledScopeDescriptor(
                        "$.classPeriods",
                        ScopeKind.Collection,
                        ImmediateParentJsonScope: "$",
                        CollectionAncestorsInOrder: [],
                        SemanticIdentityRelativePathsInOrder: [],
                        CanonicalScopeRelativeMemberPaths: []
                    ),
                ]
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var collectionState = result.TablesInDependencyOrder[1];
        collectionState.Deletes.Should().ContainSingle();
        collectionState.PreservedRows.Should().BeEmpty();
        collectionState.Updates.Should().ContainSingle();
        collectionState.Inserts.Should().BeEmpty();

        // Deleted row's stable identity
        collectionState.Deletes[0].StableRowIdentityValue.Should().Be(100L);

        // Hidden row ordinal renumbered to 0 — emitted as update since ordinal changed
        var hiddenUpdate = collectionState.Updates[0];
        LiteralValue(hiddenUpdate.Values[2]).Should().Be(0);
        LiteralValue(hiddenUpdate.Values[3]).Should().Be("HiddenPeriod");
        hiddenUpdate.StableRowIdentityValue.Should().Be(101L);
    }

    [Test]
    public void It_preserves_hidden_rows_in_relative_gaps_during_ordinal_recomputation()
    {
        var fixture = CreateCollectionFixture();

        // Request has 2 visible candidates matching the 2 visible rows
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: ["V1"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("V1"),
                            Literal("UpdatedV1"),
                            Literal("X"),
                        ]
                    ),
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 1,
                        semanticIdentityValues: ["V2"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("V2"),
                            Literal("UpdatedV2"),
                            Literal("X"),
                        ]
                    ),
                ]
            )
        );

        // 5 current rows: [hidden(0), visible(1), hidden(2), visible(3), hidden(4)]
        var currentState = CreateCurrentStateWithCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 200L, 0, "H1", "hv1", "hh1"],
                [345L, 201L, 1, "V1", "v1", "vh1"],
                [345L, 202L, 2, "H2", "hv2", "hh2"],
                [345L, 203L, 3, "V2", "v2", "vh2"],
                [345L, 204L, 4, "H3", "hv3", "hh3"],
            ]
        );

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true)],
            [
                CreateVisibleRequestCollectionItem("$.classPeriods", "V1"),
                CreateVisibleRequestCollectionItem("$.classPeriods", "V2"),
            ]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            [
                CreateVisibleStoredCollectionRow("$.classPeriods", "V1", []),
                CreateVisibleStoredCollectionRow("$.classPeriods", "V2", []),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var collectionState = result.TablesInDependencyOrder[1];
        collectionState.Updates.Should().HaveCount(2);
        collectionState.PreservedRows.Should().HaveCount(3);
        collectionState.Inserts.Should().BeEmpty();
        collectionState.Deletes.Should().BeEmpty();

        // Verify ordinals: [hidden=0, update=1, hidden=2, update=3, hidden=4]
        // Find all ordinals from updates and preserved rows
        var allOrdinals = collectionState
            .PreservedRows.Select(p => (int)LiteralValue(p.Values[2])!)
            .Concat(collectionState.Updates.Select(u => (int)LiteralValue(u.Values[2])!))
            .Order()
            .ToArray();
        allOrdinals.Should().BeEquivalentTo([0, 1, 2, 3, 4]);
    }

    [Test]
    public void It_appends_extra_inserts_after_last_visible_position()
    {
        var fixture = CreateCollectionFixture();

        // Request has 3 visible candidates: 2 matched + 1 new
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: ["V1"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("V1"),
                            Literal("UpdatedV1"),
                            Literal("X"),
                        ]
                    ),
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 1,
                        semanticIdentityValues: ["V2"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("V2"),
                            Literal("UpdatedV2"),
                            Literal("X"),
                        ]
                    ),
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 2,
                        semanticIdentityValues: ["NewV3"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("NewV3"),
                            Literal("NewValue3"),
                            Literal("NewHidden3"),
                        ]
                    ),
                ]
            )
        );

        // 3 current rows: [visible(0), hidden(1), visible(2)]
        var currentState = CreateCurrentStateWithCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 300L, 0, "V1", "v1", "vh1"],
                [345L, 301L, 1, "H1", "hv1", "hh1"],
                [345L, 302L, 2, "V2", "v2", "vh2"],
            ]
        );

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true)],
            [
                CreateVisibleRequestCollectionItem("$.classPeriods", "V1"),
                CreateVisibleRequestCollectionItem("$.classPeriods", "V2"),
                CreateVisibleRequestCollectionItem("$.classPeriods", "NewV3"),
            ]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            [
                CreateVisibleStoredCollectionRow("$.classPeriods", "V1", []),
                CreateVisibleStoredCollectionRow("$.classPeriods", "V2", []),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var collectionState = result.TablesInDependencyOrder[1];
        collectionState.Updates.Should().HaveCount(2);
        collectionState.Inserts.Should().ContainSingle();
        collectionState.PreservedRows.Should().ContainSingle();
        collectionState.Deletes.Should().BeEmpty();

        // Ordinals: update=0, hidden=1, update=2, insert=3
        LiteralValue(collectionState.Updates[0].Values[2]).Should().Be(0);
        LiteralValue(collectionState.PreservedRows[0].Values[2]).Should().Be(1);
        LiteralValue(collectionState.Updates[1].Values[2]).Should().Be(2);
        LiteralValue(collectionState.Inserts[0].Values[2]).Should().Be(3);

        // Insert is the new row
        LiteralValue(collectionState.Inserts[0].Values[3]).Should().Be("NewV3");
    }

    [Test]
    public void It_handles_delete_all_visible_while_hidden_rows_remain()
    {
        var fixture = CreateCollectionFixture();

        // Request has 0 visible candidates
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")]
            )
        );

        // 3 current rows: [visible(0), hidden(1), visible(2)]
        var currentState = CreateCurrentStateWithCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 400L, 0, "V1", "v1", "vh1"],
                [345L, 401L, 1, "H1", "hv1", "hh1"],
                [345L, 402L, 2, "V2", "v2", "vh2"],
            ]
        );

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true)],
            [] // No visible request items
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            [
                CreateVisibleStoredCollectionRow("$.classPeriods", "V1", []),
                CreateVisibleStoredCollectionRow("$.classPeriods", "V2", []),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog:
                [
                    new CompiledScopeDescriptor("$", ScopeKind.Root, null, [], [], []),
                    new CompiledScopeDescriptor(
                        "$.classPeriods",
                        ScopeKind.Collection,
                        ImmediateParentJsonScope: "$",
                        CollectionAncestorsInOrder: [],
                        SemanticIdentityRelativePathsInOrder: [],
                        CanonicalScopeRelativeMemberPaths: []
                    ),
                ]
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var collectionState = result.TablesInDependencyOrder[1];
        collectionState.Deletes.Should().HaveCount(2);
        collectionState.PreservedRows.Should().BeEmpty();
        collectionState.Updates.Should().ContainSingle();
        collectionState.Inserts.Should().BeEmpty();

        // Hidden row ordinal renumbered to 0 — emitted as update since ordinal changed
        var hiddenUpdate = collectionState.Updates[0];
        LiteralValue(hiddenUpdate.Values[2]).Should().Be(0);
        LiteralValue(hiddenUpdate.Values[3]).Should().Be("H1");
        hiddenUpdate.StableRowIdentityValue.Should().Be(401L);
    }

    // --- Extension gap coverage tests ---

    [Test]
    public void It_preserves_hidden_extension_namespace_with_all_descendants()
    {
        var fixture = CreateExtensionWithChildCollectionFixture();

        // Build a flattened write set with root + extension + extension child collection candidates
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("ignored-ext"), Literal("ignored-hidden")],
                        collectionCandidates:
                        [
                            CreateCollectionCandidate(
                                fixture.ExtensionChildCollectionPlan,
                                requestOrder: 0,
                                semanticIdentityValues: ["Thing1"],
                                values:
                                [
                                    Literal(345L),
                                    FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                                    Literal(0),
                                    Literal("Thing1"),
                                    Literal("ignored-value"),
                                ]
                            ),
                        ]
                    ),
                ]
            )
        );

        var currentState = CreateCurrentStateWithExtensionAndChildCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows:
            [
                [345L, "preserved-ext", "PRESERVED_HIDDEN"],
            ],
            extensionChildCollectionRows:
            [
                [345L, 500L, 0, "Thing1", "StoredValue1"],
                [345L, 501L, 1, "Thing2", "StoredValue2"],
            ]
        );

        var extensionScope = "$._ext.sample";
        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.Hidden,
                    HiddenMemberPaths: []
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog:
                [
                    new CompiledScopeDescriptor("$", ScopeKind.Root, null, [], [], []),
                    new CompiledScopeDescriptor(
                        "$._ext.sample",
                        ScopeKind.NonCollection,
                        ImmediateParentJsonScope: "$",
                        CollectionAncestorsInOrder: [],
                        SemanticIdentityRelativePathsInOrder: [],
                        CanonicalScopeRelativeMemberPaths: []
                    ),
                    new CompiledScopeDescriptor(
                        "$._ext.sample.things",
                        ScopeKind.Collection,
                        ImmediateParentJsonScope: "$._ext.sample",
                        CollectionAncestorsInOrder: [],
                        SemanticIdentityRelativePathsInOrder: [],
                        CanonicalScopeRelativeMemberPaths: []
                    ),
                ]
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        // Extension row is preserved
        var extensionState = result.TablesInDependencyOrder[1];
        extensionState.Inserts.Should().BeEmpty();
        extensionState.Updates.Should().BeEmpty();
        extensionState.Deletes.Should().BeEmpty();
        extensionState.PreservedRows.Should().ContainSingle();
        LiteralValue(extensionState.PreservedRows[0].Values[1]).Should().Be("preserved-ext");

        // Extension child collection rows are ALL preserved
        var childCollectionState = result.TablesInDependencyOrder[2];
        childCollectionState.Inserts.Should().BeEmpty();
        childCollectionState.Updates.Should().BeEmpty();
        childCollectionState.Deletes.Should().BeEmpty();
        childCollectionState.PreservedRows.Should().HaveCount(2);
        LiteralValue(childCollectionState.PreservedRows[0].Values[3]).Should().Be("Thing1");
        LiteralValue(childCollectionState.PreservedRows[1].Values[3]).Should().Be("Thing2");
    }

    [Test]
    public void It_overlays_hidden_columns_on_visible_extension_row()
    {
        var fixture = CreateFixtureWithExtension();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("updated-ext"), Literal("SHOULD_NOT_APPEAR")]
                    ),
                ]
            )
        );
        var currentState = CreateCurrentStateWithExtension(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows:
            [
                [345L, "original-ext", "HIDDEN_EXT_VALUE"],
            ]
        );

        var extensionScope = "$._ext.sample";
        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
            new RequestScopeState(
                ScopeAddress(extensionScope),
                ProfileVisibilityKind.VisiblePresent,
                Creatable: true
            ),
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: ["$.hiddenExt"]
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var extensionState = result.TablesInDependencyOrder[1];
        extensionState.Updates.Should().ContainSingle();
        extensionState.Inserts.Should().BeEmpty();
        extensionState.Deletes.Should().BeEmpty();
        extensionState.PreservedRows.Should().BeEmpty();

        var update = extensionState.Updates[0];
        // Visible scalar uses request value
        LiteralValue(update.Values[1]).Should().Be("updated-ext");
        // Hidden scalar preserved from current state
        LiteralValue(update.Values[2]).Should().Be("HIDDEN_EXT_VALUE");
    }

    [Test]
    public void It_preserves_hidden_extension_child_collection_under_visible_extension()
    {
        var fixture = CreateExtensionWithTwoChildCollectionsFixture();

        // Build a flattened write set: extension is visible with candidates only for the visible collection
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("updated-ext"), Literal("SHOULD_NOT_APPEAR")],
                        collectionCandidates:
                        [
                            // Only visible collection candidate
                            CreateCollectionCandidate(
                                fixture.VisibleChildCollectionPlan,
                                requestOrder: 0,
                                semanticIdentityValues: ["VisItem1"],
                                values:
                                [
                                    Literal(345L),
                                    FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                                    Literal(0),
                                    Literal("VisItem1"),
                                    Literal("UpdatedVisible1"),
                                ]
                            ),
                        ]
                    ),
                ]
            )
        );

        var extensionScope = "$._ext.sample";
        var visibleCollectionScope = "$._ext.sample.visibleThings";
        var hiddenCollectionScope = "$._ext.sample.hiddenThings";

        var currentState = CreateCurrentStateWithExtensionAndTwoChildCollections(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows:
            [
                [345L, "original-ext", "HIDDEN_EXT_VALUE"],
            ],
            visibleChildCollectionRows:
            [
                [345L, 600L, 0, "VisItem1", "OldVisible1"],
            ],
            hiddenChildCollectionRows:
            [
                [345L, 700L, 0, "HidItem1", "HidValue1"],
                [345L, 701L, 1, "HidItem2", "HidValue2"],
            ]
        );

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [
                new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
            ],
            [CreateVisibleRequestCollectionItemForScope(visibleCollectionScope, "VisItem1")]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: ["$.hiddenExt"]
                ),
                new StoredScopeState(
                    ScopeAddress(hiddenCollectionScope),
                    ProfileVisibilityKind.Hidden,
                    HiddenMemberPaths: []
                ),
            ],
            [
                CreateVisibleStoredCollectionRowForScope(
                    visibleCollectionScope,
                    "VisItem1",
                    "$.visibleItemName",
                    []
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog:
                [
                    new CompiledScopeDescriptor("$", ScopeKind.Root, null, [], [], []),
                    new CompiledScopeDescriptor(
                        extensionScope,
                        ScopeKind.NonCollection,
                        ImmediateParentJsonScope: "$",
                        CollectionAncestorsInOrder: [],
                        SemanticIdentityRelativePathsInOrder: [],
                        CanonicalScopeRelativeMemberPaths: []
                    ),
                    new CompiledScopeDescriptor(
                        visibleCollectionScope,
                        ScopeKind.Collection,
                        ImmediateParentJsonScope: extensionScope,
                        CollectionAncestorsInOrder: [],
                        SemanticIdentityRelativePathsInOrder: [],
                        CanonicalScopeRelativeMemberPaths: []
                    ),
                    new CompiledScopeDescriptor(
                        hiddenCollectionScope,
                        ScopeKind.Collection,
                        ImmediateParentJsonScope: extensionScope,
                        CollectionAncestorsInOrder: [],
                        SemanticIdentityRelativePathsInOrder: [],
                        CanonicalScopeRelativeMemberPaths: []
                    ),
                ]
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        // Extension row is updated (visible scope)
        var extensionState = result.TablesInDependencyOrder[1];
        extensionState.Updates.Should().ContainSingle();
        LiteralValue(extensionState.Updates[0].Values[1]).Should().Be("updated-ext");
        LiteralValue(extensionState.Updates[0].Values[2]).Should().Be("HIDDEN_EXT_VALUE");

        // Visible child collection merges normally
        var visibleCollectionState = result.TablesInDependencyOrder[2];
        visibleCollectionState.Updates.Should().ContainSingle();
        LiteralValue(visibleCollectionState.Updates[0].Values[3]).Should().Be("VisItem1");
        LiteralValue(visibleCollectionState.Updates[0].Values[4]).Should().Be("UpdatedVisible1");

        // Hidden child collection rows are ALL preserved
        var hiddenCollectionState = result.TablesInDependencyOrder[3];
        hiddenCollectionState.Inserts.Should().BeEmpty();
        hiddenCollectionState.Updates.Should().BeEmpty();
        hiddenCollectionState.Deletes.Should().BeEmpty();
        hiddenCollectionState.PreservedRows.Should().HaveCount(2);
        LiteralValue(hiddenCollectionState.PreservedRows[0].Values[3]).Should().Be("HidItem1");
        LiteralValue(hiddenCollectionState.PreservedRows[1].Values[3]).Should().Be("HidItem2");
    }

    // --- Creatability enforcement tests ---

    [Test]
    public void It_rejects_non_creatable_scope_insert()
    {
        var fixture = CreateFixtureWithExtension();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("new-ext"), Literal("new-hidden")]
                    ),
                ]
            )
        );

        // No current state — this is a new document, but the extension scope is being inserted
        // while the profile says it's not creatable
        var extensionScope = "$._ext.sample";
        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
            new RequestScopeState(
                ScopeAddress(extensionScope),
                ProfileVisibilityKind.VisiblePresent,
                Creatable: false
            ),
        ]);

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                CurrentState: null,
                profileRequest,
                ProfileContext: null,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.ValidationFailure>();
        var failure = (RelationalWriteMergeSynthesisOutcome.ValidationFailure)outcome;
        failure.Failures.Should().ContainSingle();
        failure.Failures[0].Message.Should().Contain(extensionScope);
    }

    [Test]
    public void It_allows_update_when_scope_is_not_creatable_but_already_exists()
    {
        var fixture = CreateFixtureWithExtension();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("updated-ext"), Literal("SHOULD_NOT_APPEAR")]
                    ),
                ]
            )
        );

        // Extension row EXISTS in current state — this is an update, not a create
        var currentState = CreateCurrentStateWithExtension(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows:
            [
                [345L, "original-ext", "HIDDEN_EXT_VALUE"],
            ]
        );

        var extensionScope = "$._ext.sample";
        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
            new RequestScopeState(
                ScopeAddress(extensionScope),
                ProfileVisibilityKind.VisiblePresent,
                Creatable: false
            ),
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: ["$.hiddenExt"]
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var extensionState = result.TablesInDependencyOrder[1];
        extensionState.Updates.Should().ContainSingle();
        extensionState.Inserts.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_non_creatable_collection_item_insert()
    {
        var fixture = CreateCollectionFixture();

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    // Existing matched row
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: ["Period1"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("Period1"),
                            Literal("Updated Value1"),
                            Literal("SHOULD_NOT_APPEAR"),
                        ]
                    ),
                    // New unmatched candidate — non-creatable
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 1,
                        semanticIdentityValues: ["NewPeriod"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("NewPeriod"),
                            Literal("New Value"),
                            Literal("NewHidden"),
                        ]
                    ),
                ]
            )
        );

        // 1 visible current row (Period1)
        var currentState = CreateCurrentStateWithCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 100L, 0, "Period1", "Value1", "HIDDEN1"],
            ]
        );

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true)],
            [
                CreateVisibleRequestCollectionItem("$.classPeriods", "Period1"),
                // NewPeriod is visible but NOT creatable
                new VisibleRequestCollectionItem(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.classPeriodName",
                                System.Text.Json.Nodes.JsonValue.Create("NewPeriod"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    Creatable: false,
                    RequestJsonPath: "$.classPeriods[1]"
                ),
            ]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            [CreateVisibleStoredCollectionRow("$.classPeriods", "Period1", ["$.hiddenField"])]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.ValidationFailure>();
        var failure = (RelationalWriteMergeSynthesisOutcome.ValidationFailure)outcome;
        failure.Failures.Should().ContainSingle();
        failure.Failures[0].Message.Should().Contain("$.classPeriods");
    }

    [Test]
    public void It_allows_collection_item_update_when_not_creatable_but_matched()
    {
        var fixture = CreateCollectionFixture();

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: ["Period1"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("Period1"),
                            Literal("Updated Value1"),
                            Literal("SHOULD_NOT_APPEAR"),
                        ]
                    ),
                ]
            )
        );

        // 1 visible current row matched by semantic identity
        var currentState = CreateCurrentStateWithCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 100L, 0, "Period1", "Value1", "HIDDEN1"],
            ]
        );

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true)],
            [
                // Period1 is visible but NOT creatable — should not matter since it matches
                new VisibleRequestCollectionItem(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.classPeriodName",
                                System.Text.Json.Nodes.JsonValue.Create("Period1"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    Creatable: false,
                    RequestJsonPath: "$.classPeriods[0]"
                ),
            ]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            [CreateVisibleStoredCollectionRow("$.classPeriods", "Period1", ["$.hiddenField"])]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var collectionState = result.TablesInDependencyOrder[1];
        collectionState.Updates.Should().ContainSingle();
        collectionState.Inserts.Should().BeEmpty();
    }

    // --- Three-level creatability chain tests ---

    [Test]
    public void It_rejects_non_creatable_extension_scope_insert_blocking_descendant_child_collection()
    {
        var fixture = CreateExtensionWithChildCollectionFixture();

        // Request includes extension scope + descendant child collection candidate
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("new-ext"), Literal("new-hidden")],
                        collectionCandidates:
                        [
                            CreateCollectionCandidate(
                                fixture.ExtensionChildCollectionPlan,
                                requestOrder: 0,
                                semanticIdentityValues: ["Thing1"],
                                values:
                                [
                                    Literal(345L),
                                    FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                                    Literal(0),
                                    Literal("Thing1"),
                                    Literal("ThingValue1"),
                                ]
                            ),
                        ]
                    ),
                ]
            )
        );

        // Document exists (update scenario) but extension scope row does NOT exist —
        // the hidden required member $.hiddenExt means Core marks this scope non-creatable
        var currentState = CreateCurrentStateWithExtensionAndChildCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows: [],
            extensionChildCollectionRows: []
        );

        var extensionScope = "$._ext.sample";
        var childCollectionScope = "$._ext.sample.things";

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [
                new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ],
            [CreateVisibleRequestCollectionItemForScope(childCollectionScope, "Thing1")]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: ["$.hiddenExt"]
                ),
            ],
            []
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        // Gate 2 rejects the non-creatable extension scope insert — blocking descendant
        // child collection creation in the same request
        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.ValidationFailure>();
        var failure = (RelationalWriteMergeSynthesisOutcome.ValidationFailure)outcome;
        failure.Failures.Should().ContainSingle();
        failure.Failures[0].Message.Should().Contain(extensionScope);
    }

    [Test]
    public void It_allows_existing_non_creatable_extension_scope_update_with_descendant_child_collection_create()
    {
        var fixture = CreateExtensionWithChildCollectionFixture();

        // Request includes extension scope update + NEW descendant child collection candidate
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("updated-ext"), Literal("SHOULD_NOT_APPEAR")],
                        collectionCandidates:
                        [
                            CreateCollectionCandidate(
                                fixture.ExtensionChildCollectionPlan,
                                requestOrder: 0,
                                semanticIdentityValues: ["NewThing"],
                                values:
                                [
                                    Literal(345L),
                                    FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                                    Literal(0),
                                    Literal("NewThing"),
                                    Literal("NewThingValue"),
                                ]
                            ),
                        ]
                    ),
                ]
            )
        );

        // Document exists AND extension scope row exists — non-creatable scope is allowed
        // because it's an update, not a create
        var currentState = CreateCurrentStateWithExtensionAndChildCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows:
            [
                [345L, "original-ext", "HIDDEN_EXT_VALUE"],
            ],
            extensionChildCollectionRows: []
        );

        var extensionScope = "$._ext.sample";
        var childCollectionScope = "$._ext.sample.things";

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [
                new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: false
                ),
            ],
            [CreateVisibleRequestCollectionItemForScope(childCollectionScope, "NewThing")]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: ["$.hiddenExt"]
                ),
            ],
            []
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        // Extension scope update succeeds (non-creatable but already exists),
        // and descendant child collection item is created
        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        // Extension scope: updated with hidden value preserved
        var extensionState = result.TablesInDependencyOrder[1];
        extensionState.Updates.Should().ContainSingle();
        extensionState.Inserts.Should().BeEmpty();
        LiteralValue(extensionState.Updates[0].Values[1]).Should().Be("updated-ext");
        LiteralValue(extensionState.Updates[0].Values[2]).Should().Be("HIDDEN_EXT_VALUE");

        // Descendant child collection: new item inserted
        var childCollectionState = result.TablesInDependencyOrder[2];
        childCollectionState.Inserts.Should().ContainSingle();
        childCollectionState.Deletes.Should().BeEmpty();
        LiteralValue(childCollectionState.Inserts[0].Values[3]).Should().Be("NewThing");
        LiteralValue(childCollectionState.Inserts[0].Values[4]).Should().Be("NewThingValue");
    }

    // --- Contract completeness tests ---

    [Test]
    public void It_returns_contract_mismatch_when_collection_has_request_candidates_but_no_visible_request_items()
    {
        // Core emitted a CollectionWriteCandidate for the collection scope but failed to emit
        // a corresponding VisibleRequestCollectionItem. Without the VisibleRequestCollectionItem,
        // the merge cannot decide whether the candidate is a visible insert/update or hidden,
        // so it surfaces a category-5 contract mismatch instead of partial-credit merging.
        var fixture = CreateCollectionFixture();

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: ["Period1"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("Period1"),
                            Literal("Value1"),
                            Literal(null),
                        ]
                    ),
                ]
            )
        );

        // Deliberately omit VisibleRequestCollectionItems despite having a candidate.
        var profileRequest = CreateProfileRequestWithCollectionItems(
            [new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true)],
            []
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            []
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                CurrentState: null,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.ContractMismatch>();
        var mismatch = (RelationalWriteMergeSynthesisOutcome.ContractMismatch)outcome;
        mismatch.Messages.Should().HaveCount(1);
        mismatch
            .Messages[0]
            .Should()
            .Contain("'$.classPeriods'")
            .And.Contain("zero VisibleRequestCollectionItems");
    }

    [Test]
    public void It_returns_contract_mismatch_when_collection_receives_duplicate_visible_request_candidates()
    {
        // Two CollectionWriteCandidates with identical semantic identity that are both
        // marked visible by the matching VisibleRequestCollectionItems. Core is expected to
        // dedupe these before reaching the backend; if it doesn't, surface a category-5
        // contract mismatch rather than throwing.
        var fixture = CreateCollectionFixture();

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: ["Period1"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("Period1"),
                            Literal("ValueA"),
                            Literal(null),
                        ]
                    ),
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 1,
                        semanticIdentityValues: ["Period1"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(1),
                            Literal("Period1"),
                            Literal("ValueB"),
                            Literal(null),
                        ]
                    ),
                ]
            )
        );

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true)],
            [
                CreateVisibleRequestCollectionItem("$.classPeriods", "Period1"),
                CreateVisibleRequestCollectionItem("$.classPeriods", "Period1"),
            ]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            []
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                CurrentState: null,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.ContractMismatch>();
        var mismatch = (RelationalWriteMergeSynthesisOutcome.ContractMismatch)outcome;
        mismatch.Messages.Should().HaveCount(1);
        mismatch.Messages[0].Should().Contain("'$.classPeriods'").And.Contain("duplicate visible");
    }

    [Test]
    public void It_returns_contract_mismatch_when_extension_scope_has_no_request_scope_state_on_update()
    {
        var fixture = CreateFixtureWithExtension();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("ext-value"), Literal("hidden-value")]
                    ),
                ]
            )
        );
        var currentState = CreateCurrentStateWithExtension(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows:
            [
                [345L, "original-ext", "HIDDEN_EXT_VALUE"],
            ]
        );

        var extensionScope = "$._ext.sample";

        // Deliberately omit the extension scope state — only provide root scope state
        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: ["$.hiddenExt"]
                ),
            ]
        );

        // Provide a CompiledScopeCatalog that includes the extension scope
        var compiledScopeCatalog = new CompiledScopeDescriptor[]
        {
            new(
                "$",
                ScopeKind.Root,
                ImmediateParentJsonScope: null,
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["$.name"]
            ),
            new(
                extensionScope,
                ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$",
                CollectionAncestorsInOrder: [],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["$.extValue", "$.hiddenExt"]
            ),
        };

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: compiledScopeCatalog
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.ContractMismatch>();
        var mismatch = (RelationalWriteMergeSynthesisOutcome.ContractMismatch)outcome;
        mismatch.Messages.Should().HaveCount(1);
        mismatch.Messages[0].Should().Contain($"'{extensionScope}'").And.Contain("no RequestScopeState");
    }

    // --- Fixture builders ---

    private sealed record SimpleFixture(ResourceWritePlan WritePlan, TableWritePlan RootPlan);

    private sealed record ExtensionFixture(
        ResourceWritePlan WritePlan,
        TableWritePlan RootPlan,
        TableWritePlan RootExtensionPlan
    );

    private sealed record CollectionFixture(
        ResourceWritePlan WritePlan,
        TableWritePlan RootPlan,
        TableWritePlan CollectionPlan
    );

    private sealed record ExtensionWithChildCollectionFixture(
        ResourceWritePlan WritePlan,
        TableWritePlan RootPlan,
        TableWritePlan RootExtensionPlan,
        TableWritePlan ExtensionChildCollectionPlan
    );

    private sealed record ExtensionWithTwoChildCollectionsFixture(
        ResourceWritePlan WritePlan,
        TableWritePlan RootPlan,
        TableWritePlan RootExtensionPlan,
        TableWritePlan VisibleChildCollectionPlan,
        TableWritePlan HiddenChildCollectionPlan
    );

    private sealed record CollectionWithInlinedScopeFixture(
        ResourceWritePlan WritePlan,
        TableWritePlan RootPlan,
        TableWritePlan CollectionPlan
    );

    private sealed record CollectionWithAlignedExtensionScopeFixture(
        ResourceWritePlan WritePlan,
        TableWritePlan RootPlan,
        TableWritePlan CollectionPlan,
        TableWritePlan AlignedExtensionScopePlan
    );

    private static SimpleFixture CreateFixture()
    {
        var rootPlan = CreateRootPlanWithHiddenColumn();
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new SimpleFixture(new ResourceWritePlan(resourceModel, [rootPlan]), rootPlan);
    }

    private static ExtensionFixture CreateFixtureWithExtension()
    {
        var rootPlan = CreateSimpleRootPlan();
        var rootExtensionPlan = CreateRootExtensionPlanWithHiddenColumn();
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, rootExtensionPlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ExtensionFixture(
            new ResourceWritePlan(resourceModel, [rootPlan, rootExtensionPlan]),
            rootPlan,
            rootExtensionPlan
        );
    }

    private static ExtensionWithChildCollectionFixture CreateExtensionWithChildCollectionFixture()
    {
        var rootPlan = CreateSimpleRootPlan();
        var rootExtensionPlan = CreateRootExtensionPlanWithHiddenColumn();
        var extensionChildCollectionPlan = CreateExtensionChildCollectionPlan(
            "$._ext.sample.things",
            "SchoolExtensionThing"
        );
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder:
            [
                rootPlan.TableModel,
                rootExtensionPlan.TableModel,
                extensionChildCollectionPlan.TableModel,
            ],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ExtensionWithChildCollectionFixture(
            new ResourceWritePlan(resourceModel, [rootPlan, rootExtensionPlan, extensionChildCollectionPlan]),
            rootPlan,
            rootExtensionPlan,
            extensionChildCollectionPlan
        );
    }

    private static ExtensionWithTwoChildCollectionsFixture CreateExtensionWithTwoChildCollectionsFixture()
    {
        var rootPlan = CreateSimpleRootPlan();
        var rootExtensionPlan = CreateRootExtensionPlanWithHiddenColumn();
        var visibleChildCollectionPlan = CreateExtensionChildCollectionPlan(
            "$._ext.sample.visibleThings",
            "SchoolExtensionVisibleThing"
        );
        var hiddenChildCollectionPlan = CreateExtensionChildCollectionPlan(
            "$._ext.sample.hiddenThings",
            "SchoolExtensionHiddenThing"
        );
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder:
            [
                rootPlan.TableModel,
                rootExtensionPlan.TableModel,
                visibleChildCollectionPlan.TableModel,
                hiddenChildCollectionPlan.TableModel,
            ],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new ExtensionWithTwoChildCollectionsFixture(
            new ResourceWritePlan(
                resourceModel,
                [rootPlan, rootExtensionPlan, visibleChildCollectionPlan, hiddenChildCollectionPlan]
            ),
            rootPlan,
            rootExtensionPlan,
            visibleChildCollectionPlan,
            hiddenChildCollectionPlan
        );
    }

    /// <summary>
    /// Creates an extension child collection table plan with 5 columns:
    /// 0: School_DocumentId (ParentKeyPart), 1: CollectionItemId (Precomputed/StableRowIdentity),
    /// 2: Ordinal, 3: ItemName (Scalar, semantic identity), 4: ItemValue (Scalar)
    /// </summary>
    private static TableWritePlan CreateExtensionChildCollectionPlan(
        string jsonScopeCanonical,
        string tableName
    )
    {
        var scopeParts = jsonScopeCanonical.Split('.');
        var jsonSegments = scopeParts
            .Where(s => s != "$")
            .Select(s => new JsonPathSegment.Property(s))
            .Cast<JsonPathSegment>()
            .ToArray();
        var lastScopePart = scopeParts[^1];

        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), tableName),
            new JsonPathExpression(jsonScopeCanonical, jsonSegments),
            new TableKey(
                $"PK_{tableName}",
                [
                    new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64, MaxLength: null),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32, MaxLength: null),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("ItemName"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                    false,
                    new JsonPathExpression(
                        $"$.{lastScopePart}Name",
                        [new JsonPathSegment.Property($"{lastScopePart}Name")]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("ItemValue"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.value", [new JsonPathSegment.Property("value")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.ExtensionCollection,
                [new DbColumnName("School_DocumentId"), new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.visibleItemName",
                            [new JsonPathSegment.Property("visibleItemName")]
                        ),
                        new DbColumnName("ItemName")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: $"insert into sample.\"{tableName}\" values (@p)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.visibleItemName",
                            [new JsonPathSegment.Property("visibleItemName")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "ItemName"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.value", [new JsonPathSegment.Property("value")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "ItemValue"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                BindingIndex: 1
            ),
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.visibleItemName",
                            [new JsonPathSegment.Property("visibleItemName")]
                        ),
                        BindingIndex: 3
                    ),
                ],
                StableRowIdentityBindingIndex: 1,
                UpdateByStableRowIdentitySql: $"update sample.\"{tableName}\" set @p where CollectionItemId = @id",
                DeleteByStableRowIdentitySql: $"delete from sample.\"{tableName}\" where CollectionItemId = @id",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4]
            )
        );
    }

    /// <summary>
    /// Root table with 3 columns: DocumentId (StorageManaged), Name (Scalar "$.name"), Code (Scalar "$.code")
    /// </summary>
    private static TableWritePlan CreateRootPlanWithHiddenColumn()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Code"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    false,
                    new JsonPathExpression("$.code", [new JsonPathSegment.Property("code")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Name, @Code)",
            UpdateSql: "update edfi.\"School\" set \"Name\" = @Name, \"Code\" = @Code where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.code", [new JsonPathSegment.Property("code")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 50)
                    ),
                    "Code"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    /// <summary>
    /// Root table with key-unification: 5 columns.
    /// 0: DocumentId (ParentKeyPart), 1: Name (Scalar "$.name"),
    /// 2: PrimaryType_Unified (Precomputed canonical), 3: PrimaryType_Present (Precomputed synthetic presence),
    /// 4: SecondaryType_Present (Precomputed synthetic presence)
    /// Key-unification class: PrimaryType (member path "$.primaryType", presence idx 3) and
    /// SecondaryType (member path "$.secondaryType", presence idx 4), canonical idx 2.
    /// </summary>
    private static TableWritePlan CreateRootPlanWithKeyUnification()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("PrimaryType_Unified"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                    true,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("PrimaryType_Present"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Boolean),
                    true,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SecondaryType_Present"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Boolean),
                    true,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@p)",
            UpdateSql: "update edfi.\"School\" set @p where DocumentId = @id",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Precomputed(),
                    "PrimaryType_Unified"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Precomputed(),
                    "PrimaryType_Present"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Precomputed(),
                    "SecondaryType_Present"
                ),
            ],
            KeyUnificationPlans:
            [
                new KeyUnificationWritePlan(
                    CanonicalColumn: new DbColumnName("PrimaryType_Unified"),
                    CanonicalBindingIndex: 2,
                    MembersInOrder:
                    [
                        new KeyUnificationMemberWritePlan.ScalarMember(
                            MemberPathColumn: new DbColumnName("PrimaryType"),
                            RelativePath: new JsonPathExpression(
                                "$.primaryType",
                                [new JsonPathSegment.Property("primaryType")]
                            ),
                            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                            PresenceColumn: new DbColumnName("PrimaryType_Present"),
                            PresenceBindingIndex: 3,
                            PresenceIsSynthetic: true
                        ),
                        new KeyUnificationMemberWritePlan.ScalarMember(
                            MemberPathColumn: new DbColumnName("SecondaryType"),
                            RelativePath: new JsonPathExpression(
                                "$.secondaryType",
                                [new JsonPathSegment.Property("secondaryType")]
                            ),
                            ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                            PresenceColumn: new DbColumnName("SecondaryType_Present"),
                            PresenceBindingIndex: 4,
                            PresenceIsSynthetic: true
                        ),
                    ]
                ),
            ]
        );
    }

    /// <summary>
    /// Simple root table with 2 columns: DocumentId, Name — used for extension fixture tests.
    /// </summary>
    private static TableWritePlan CreateSimpleRootPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"Name\" = @Name where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    /// <summary>
    /// Root extension with 3 columns: School_DocumentId (DocumentId), ExtValue (Scalar "$.extValue"),
    /// HiddenExt (Scalar "$.hiddenExt").
    /// JsonScope is "$._ext.sample".
    /// </summary>
    private static TableWritePlan CreateRootExtensionPlanWithHiddenColumn()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtension"),
            new JsonPathExpression(
                "$._ext.sample",
                [new JsonPathSegment.Property("_ext"), new JsonPathSegment.Property("sample")]
            ),
            new TableKey(
                "PK_SchoolExtension",
                [new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("ExtValue"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.extValue", [new JsonPathSegment.Property("extValue")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("HiddenExt"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.hiddenExt", [new JsonPathSegment.Property("hiddenExt")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.RootExtension,
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into sample.\"SchoolExtension\" values (@School_DocumentId, @ExtValue, @HiddenExt)",
            UpdateSql: "update sample.\"SchoolExtension\" set \"ExtValue\" = @ExtValue, \"HiddenExt\" = @HiddenExt where \"School_DocumentId\" = @School_DocumentId",
            DeleteByParentSql: "delete from sample.\"SchoolExtension\" where \"School_DocumentId\" = @School_DocumentId",
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.extValue", [new JsonPathSegment.Property("extValue")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "ExtValue"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.hiddenExt", [new JsonPathSegment.Property("hiddenExt")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "HiddenExt"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    // --- Current state builders ---

    private static RelationalWriteCurrentState CreateCurrentState(
        SimpleFixture fixture,
        IReadOnlyList<object?[]>? rootRows = null
    )
    {
        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [new HydratedTableRows(fixture.RootPlan.TableModel, rootRows ?? [])]
        );
    }

    private static RelationalWriteCurrentState CreateCurrentStateWithExtension(
        ExtensionFixture fixture,
        IReadOnlyList<object?[]>? rootRows = null,
        IReadOnlyList<object?[]>? rootExtensionRows = null
    )
    {
        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(fixture.RootPlan.TableModel, rootRows ?? []),
                new HydratedTableRows(fixture.RootExtensionPlan.TableModel, rootExtensionRows ?? []),
            ]
        );
    }

    private static RelationalWriteCurrentState CreateCurrentStateWithExtensionAndChildCollection(
        ExtensionWithChildCollectionFixture fixture,
        IReadOnlyList<object?[]>? rootRows = null,
        IReadOnlyList<object?[]>? rootExtensionRows = null,
        IReadOnlyList<object?[]>? extensionChildCollectionRows = null
    )
    {
        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(fixture.RootPlan.TableModel, rootRows ?? []),
                new HydratedTableRows(fixture.RootExtensionPlan.TableModel, rootExtensionRows ?? []),
                new HydratedTableRows(
                    fixture.ExtensionChildCollectionPlan.TableModel,
                    extensionChildCollectionRows ?? []
                ),
            ]
        );
    }

    private static RelationalWriteCurrentState CreateCurrentStateWithExtensionAndTwoChildCollections(
        ExtensionWithTwoChildCollectionsFixture fixture,
        IReadOnlyList<object?[]>? rootRows = null,
        IReadOnlyList<object?[]>? rootExtensionRows = null,
        IReadOnlyList<object?[]>? visibleChildCollectionRows = null,
        IReadOnlyList<object?[]>? hiddenChildCollectionRows = null
    )
    {
        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(fixture.RootPlan.TableModel, rootRows ?? []),
                new HydratedTableRows(fixture.RootExtensionPlan.TableModel, rootExtensionRows ?? []),
                new HydratedTableRows(
                    fixture.VisibleChildCollectionPlan.TableModel,
                    visibleChildCollectionRows ?? []
                ),
                new HydratedTableRows(
                    fixture.HiddenChildCollectionPlan.TableModel,
                    hiddenChildCollectionRows ?? []
                ),
            ]
        );
    }

    private static RelationalWriteCurrentState CreateCurrentStateWithCollectionAndAlignedExtensionScope(
        CollectionWithAlignedExtensionScopeFixture fixture,
        IReadOnlyList<object?[]>? rootRows = null,
        IReadOnlyList<object?[]>? collectionRows = null,
        IReadOnlyList<object?[]>? alignedExtensionScopeRows = null
    )
    {
        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(fixture.RootPlan.TableModel, rootRows ?? []),
                new HydratedTableRows(fixture.CollectionPlan.TableModel, collectionRows ?? []),
                new HydratedTableRows(
                    fixture.AlignedExtensionScopePlan.TableModel,
                    alignedExtensionScopeRows ?? []
                ),
            ]
        );
    }

    // --- Profile helper builders ---

    private static ProfileAppliedWriteRequest CreateProfileRequest(
        ImmutableArray<RequestScopeState> scopeStates
    )
    {
        return new ProfileAppliedWriteRequest(
            WritableRequestBody: System.Text.Json.Nodes.JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates: scopeStates,
            VisibleRequestCollectionItems: []
        );
    }

    private static ProfileAppliedWriteContext CreateProfileContext(
        ProfileAppliedWriteRequest request,
        ImmutableArray<StoredScopeState> storedScopeStates
    )
    {
        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: System.Text.Json.Nodes.JsonNode.Parse("{}")!,
            StoredScopeStates: storedScopeStates,
            VisibleStoredCollectionRows: []
        );
    }

    private static ScopeInstanceAddress RootAddress() => new("$", []);

    private static ScopeInstanceAddress ScopeAddress(string jsonScope) => new(jsonScope, []);

    private static CollectionFixture CreateCollectionFixture()
    {
        var rootPlan = CreateSimpleRootPlan();
        var collectionPlan = CreateCollectionPlanWithHiddenColumn();
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, collectionPlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new CollectionFixture(
            new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan]),
            rootPlan,
            collectionPlan
        );
    }

    private static CollectionFixture CreateDateIdentityCollectionFixture()
    {
        var rootPlan = CreateSimpleRootPlan();
        var collectionPlan = CreateDateIdentityCollectionPlan();
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, collectionPlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new CollectionFixture(
            new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan]),
            rootPlan,
            collectionPlan
        );
    }

    private static CollectionFixture CreateTimeIdentityCollectionFixture()
    {
        var rootPlan = CreateSimpleRootPlan();
        var collectionPlan = CreateTimeIdentityCollectionPlan();
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, collectionPlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new CollectionFixture(
            new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan]),
            rootPlan,
            collectionPlan
        );
    }

    private static CollectionWithInlinedScopeFixture CreateCollectionWithInlinedScopeFixture()
    {
        var rootPlan = CreateSimpleRootPlan();
        var collectionPlan = CreateCollectionPlanWithInlinedScope();
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, collectionPlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new CollectionWithInlinedScopeFixture(
            new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan]),
            rootPlan,
            collectionPlan
        );
    }

    /// <summary>
    /// Fixture: Root + Collection ($.classPeriods) + Collection-aligned extension scope
    /// ($.classPeriods._ext.sample) that has an inlined non-table-backed child scope
    /// ($.classPeriods._ext.sample.someRef). The aligned extension scope table has 4 columns:
    /// 0: School_DocumentId (ParentKeyPart), 1: BaseCollectionItemId (ParentKeyPart),
    /// 2: ExtValue (Scalar "$.extValue"), 3: InlinedRefId (Scalar "$.someRef.refId")
    /// </summary>
    private static CollectionWithAlignedExtensionScopeFixture CreateCollectionWithAlignedExtensionScopeFixture()
    {
        var rootPlan = CreateSimpleRootPlan();
        var collectionPlan = CreateCollectionPlanForAlignedExtension();
        var alignedExtensionScopePlan = CreateCollectionAlignedExtensionScopePlanWithInlinedScope();
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder:
            [
                rootPlan.TableModel,
                collectionPlan.TableModel,
                alignedExtensionScopePlan.TableModel,
            ],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new CollectionWithAlignedExtensionScopeFixture(
            new ResourceWritePlan(resourceModel, [rootPlan, collectionPlan, alignedExtensionScopePlan]),
            rootPlan,
            collectionPlan,
            alignedExtensionScopePlan
        );
    }

    /// <summary>
    /// Collection table "SchoolClassPeriod" with 5 columns for use with aligned extension scope tests:
    /// 0: School_DocumentId (ParentKeyPart), 1: CollectionItemId (Precomputed/StableRowIdentity),
    /// 2: Ordinal, 3: PeriodName (Scalar, semantic identity, path $.classPeriodName),
    /// 4: Value (Scalar, visible, path $.value)
    /// JsonScope is "$.classPeriods".
    /// </summary>
    private static TableWritePlan CreateCollectionPlanForAlignedExtension()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolClassPeriod"),
            new JsonPathExpression("$.classPeriods", [new JsonPathSegment.Property("classPeriods")]),
            new TableKey(
                "PK_SchoolClassPeriod",
                [
                    new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64, MaxLength: null),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32, MaxLength: null),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("PeriodName"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                    false,
                    new JsonPathExpression(
                        "$.classPeriodName",
                        [new JsonPathSegment.Property("classPeriodName")]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Value"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.value", [new JsonPathSegment.Property("value")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("School_DocumentId"), new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.classPeriodName",
                            [new JsonPathSegment.Property("classPeriodName")]
                        ),
                        new DbColumnName("PeriodName")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolClassPeriod\" values (@p)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.classPeriodName",
                            [new JsonPathSegment.Property("classPeriodName")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "PeriodName"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.value", [new JsonPathSegment.Property("value")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Value"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                BindingIndex: 1
            ),
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.classPeriodName",
                            [new JsonPathSegment.Property("classPeriodName")]
                        ),
                        BindingIndex: 3
                    ),
                ],
                StableRowIdentityBindingIndex: 1,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolClassPeriod\" set @p where CollectionItemId = @id",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolClassPeriod\" where CollectionItemId = @id",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4]
            )
        );
    }

    /// <summary>
    /// Collection-aligned extension scope table "SchoolClassPeriodExtension" with 4 columns:
    /// 0: School_DocumentId (ParentKeyPart), 1: BaseCollectionItemId (ParentKeyPart),
    /// 2: ExtValue (Scalar "$.extValue"), 3: InlinedRefId (Scalar "$.someRef.refId")
    /// JsonScope is "$.classPeriods._ext.sample". DbTableKind is CollectionExtensionScope.
    /// The InlinedRefId column belongs to inlined scope "$.classPeriods._ext.sample.someRef".
    /// </summary>
    private static TableWritePlan CreateCollectionAlignedExtensionScopePlanWithInlinedScope()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolClassPeriodExtension"),
            new JsonPathExpression(
                "$.classPeriods._ext.sample",
                [
                    new JsonPathSegment.Property("classPeriods"),
                    new JsonPathSegment.Property("_ext"),
                    new JsonPathSegment.Property("sample"),
                ]
            ),
            new TableKey(
                "PK_SchoolClassPeriodExtension",
                [
                    new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("BaseCollectionItemId"), ColumnKind.ParentKeyPart),
                ]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("BaseCollectionItemId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("ExtValue"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.extValue", [new JsonPathSegment.Property("extValue")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("InlinedRefId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression(
                        "$.someRef.refId",
                        [new JsonPathSegment.Property("someRef"), new JsonPathSegment.Property("refId")]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.CollectionExtensionScope,
                [new DbColumnName("School_DocumentId"), new DbColumnName("BaseCollectionItemId")],
                [new DbColumnName("School_DocumentId"), new DbColumnName("BaseCollectionItemId")],
                [new DbColumnName("School_DocumentId"), new DbColumnName("BaseCollectionItemId")],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into sample.\"SchoolClassPeriodExtension\" values (@School_DocumentId, @BaseCollectionItemId, @ExtValue, @InlinedRefId)",
            UpdateSql: "update sample.\"SchoolClassPeriodExtension\" set @p where \"School_DocumentId\" = @School_DocumentId AND \"BaseCollectionItemId\" = @BaseCollectionItemId",
            DeleteByParentSql: "delete from sample.\"SchoolClassPeriodExtension\" where \"School_DocumentId\" = @School_DocumentId AND \"BaseCollectionItemId\" = @BaseCollectionItemId",
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 4, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.ParentKeyPart(1),
                    "BaseCollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.extValue", [new JsonPathSegment.Property("extValue")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "ExtValue"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.someRef.refId",
                            [new JsonPathSegment.Property("someRef"), new JsonPathSegment.Property("refId")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "InlinedRefId"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    /// <summary>
    /// Collection table "SchoolClassPeriod" with 6 columns:
    /// 0: School_DocumentId (ParentKeyPart), 1: CollectionItemId (Precomputed/StableRowIdentity),
    /// 2: Ordinal, 3: PeriodName (Scalar, semantic identity, path $.classPeriodName),
    /// 4: Value (Scalar, visible, path $.value),
    /// 5: CalRefId (Scalar, visible, path $.calendarReference.calendarReferenceId — inlined scope member)
    /// JsonScope is "$.classPeriods".
    /// The CalRefId column belongs to inlined scope "$.classPeriods.calendarReference".
    /// </summary>
    private static TableWritePlan CreateCollectionPlanWithInlinedScope()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolClassPeriod"),
            new JsonPathExpression("$.classPeriods", [new JsonPathSegment.Property("classPeriods")]),
            new TableKey(
                "PK_SchoolClassPeriod",
                [
                    new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64, MaxLength: null),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32, MaxLength: null),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("PeriodName"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                    false,
                    new JsonPathExpression(
                        "$.classPeriodName",
                        [new JsonPathSegment.Property("classPeriodName")]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Value"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.value", [new JsonPathSegment.Property("value")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("CalRefId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression(
                        "$.calendarReference.calendarReferenceId",
                        [
                            new JsonPathSegment.Property("calendarReference"),
                            new JsonPathSegment.Property("calendarReferenceId"),
                        ]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("School_DocumentId"), new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.classPeriodName",
                            [new JsonPathSegment.Property("classPeriodName")]
                        ),
                        new DbColumnName("PeriodName")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolClassPeriod\" values (@p)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 6, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.classPeriodName",
                            [new JsonPathSegment.Property("classPeriodName")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "PeriodName"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.value", [new JsonPathSegment.Property("value")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Value"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[5],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.calendarReference.calendarReferenceId",
                            [
                                new JsonPathSegment.Property("calendarReference"),
                                new JsonPathSegment.Property("calendarReferenceId"),
                            ]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "CalRefId"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                BindingIndex: 1
            ),
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.classPeriodName",
                            [new JsonPathSegment.Property("classPeriodName")]
                        ),
                        BindingIndex: 3
                    ),
                ],
                StableRowIdentityBindingIndex: 1,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolClassPeriod\" set @p where CollectionItemId = @id",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolClassPeriod\" where CollectionItemId = @id",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4, 5]
            )
        );
    }

    /// <summary>
    /// Collection table "SchoolClassPeriod" with 6 columns:
    /// 0: School_DocumentId (ParentKeyPart), 1: CollectionItemId (Precomputed/StableRowIdentity),
    /// 2: Ordinal, 3: PeriodName (Scalar, semantic identity), 4: Value (Scalar, visible),
    /// 5: HiddenField (Scalar, hidden)
    /// JsonScope is "$.classPeriods".
    /// </summary>
    private static TableWritePlan CreateCollectionPlanWithHiddenColumn()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolClassPeriod"),
            new JsonPathExpression("$.classPeriods", [new JsonPathSegment.Property("classPeriods")]),
            new TableKey(
                "PK_SchoolClassPeriod",
                [
                    new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64, MaxLength: null),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32, MaxLength: null),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("PeriodName"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 60),
                    false,
                    new JsonPathExpression(
                        "$.classPeriodName",
                        [new JsonPathSegment.Property("classPeriodName")]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Value"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.value", [new JsonPathSegment.Property("value")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("HiddenField"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.hiddenField", [new JsonPathSegment.Property("hiddenField")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("School_DocumentId"), new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.classPeriodName",
                            [new JsonPathSegment.Property("classPeriodName")]
                        ),
                        new DbColumnName("PeriodName")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolClassPeriod\" values (@p)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 6, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.classPeriodName",
                            [new JsonPathSegment.Property("classPeriodName")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 60)
                    ),
                    "PeriodName"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.value", [new JsonPathSegment.Property("value")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Value"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[5],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.hiddenField",
                            [new JsonPathSegment.Property("hiddenField")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "HiddenField"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                BindingIndex: 1
            ),
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.classPeriodName",
                            [new JsonPathSegment.Property("classPeriodName")]
                        ),
                        BindingIndex: 3
                    ),
                ],
                StableRowIdentityBindingIndex: 1,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolClassPeriod\" set @p where CollectionItemId = @id",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolClassPeriod\" where CollectionItemId = @id",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4, 5]
            )
        );
    }

    /// <summary>
    /// Collection table "SessionPeriod" with 5 columns:
    /// 0: School_DocumentId (ParentKeyPart), 1: CollectionItemId (Precomputed/StableRowIdentity),
    /// 2: Ordinal, 3: BeginDate (Scalar, ScalarKind.Date, semantic identity), 4: Value (Scalar)
    /// JsonScope is "$.sessionPeriods".
    /// </summary>
    private static TableWritePlan CreateDateIdentityCollectionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SessionPeriod"),
            new JsonPathExpression("$.sessionPeriods", [new JsonPathSegment.Property("sessionPeriods")]),
            new TableKey(
                "PK_SessionPeriod",
                [
                    new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64, MaxLength: null),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32, MaxLength: null),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("BeginDate"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date, MaxLength: null),
                    false,
                    new JsonPathExpression("$.beginDate", [new JsonPathSegment.Property("beginDate")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Value"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.value", [new JsonPathSegment.Property("value")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("School_DocumentId"), new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.beginDate", [new JsonPathSegment.Property("beginDate")]),
                        new DbColumnName("BeginDate")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SessionPeriod\" values (@p)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.beginDate", [new JsonPathSegment.Property("beginDate")]),
                        new RelationalScalarType(ScalarKind.Date, MaxLength: null)
                    ),
                    "BeginDate"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.value", [new JsonPathSegment.Property("value")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Value"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                BindingIndex: 1
            ),
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression("$.beginDate", [new JsonPathSegment.Property("beginDate")]),
                        BindingIndex: 3
                    ),
                ],
                StableRowIdentityBindingIndex: 1,
                UpdateByStableRowIdentitySql: "update edfi.\"SessionPeriod\" set @p where CollectionItemId = @id",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SessionPeriod\" where CollectionItemId = @id",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4]
            )
        );
    }

    /// <summary>
    /// Collection table "ClassPeriodTime" with 5 columns:
    /// 0: School_DocumentId (ParentKeyPart), 1: CollectionItemId (Precomputed/StableRowIdentity),
    /// 2: Ordinal, 3: StartTime (Scalar, ScalarKind.Time, semantic identity), 4: Value (Scalar)
    /// JsonScope is "$.classPeriods".
    /// </summary>
    private static TableWritePlan CreateTimeIdentityCollectionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "ClassPeriodTime"),
            new JsonPathExpression("$.classPeriods", [new JsonPathSegment.Property("classPeriods")]),
            new TableKey(
                "PK_ClassPeriodTime",
                [
                    new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart),
                    new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.Scalar),
                ]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int64, MaxLength: null),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Int32, MaxLength: null),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("StartTime"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Time, MaxLength: null),
                    false,
                    new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Value"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.value", [new JsonPathSegment.Property("value")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("School_DocumentId"), new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                        new DbColumnName("StartTime")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"ClassPeriodTime\" values (@p)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.ParentKeyPart(0),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                        new RelationalScalarType(ScalarKind.Time, MaxLength: null)
                    ),
                    "StartTime"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.value", [new JsonPathSegment.Property("value")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Value"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                BindingIndex: 1
            ),
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                        BindingIndex: 3
                    ),
                ],
                StableRowIdentityBindingIndex: 1,
                UpdateByStableRowIdentitySql: "update edfi.\"ClassPeriodTime\" set @p where CollectionItemId = @id",
                DeleteByStableRowIdentitySql: "delete from edfi.\"ClassPeriodTime\" where CollectionItemId = @id",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [3, 4]
            )
        );
    }

    private static RelationalWriteCurrentState CreateCurrentStateWithCollection(
        CollectionFixture fixture,
        IReadOnlyList<object?[]>? rootRows = null,
        IReadOnlyList<object?[]>? collectionRows = null
    )
    {
        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(fixture.RootPlan.TableModel, rootRows ?? []),
                new HydratedTableRows(fixture.CollectionPlan.TableModel, collectionRows ?? []),
            ]
        );
    }

    private static CollectionWriteCandidate CreateCollectionCandidate(
        TableWritePlan collectionPlan,
        int requestOrder,
        IReadOnlyList<object?> semanticIdentityValues,
        IReadOnlyList<FlattenedWriteValue> values,
        IReadOnlyList<CandidateAttachedAlignedScopeData>? attachedAlignedScopeData = null
    )
    {
        return new CollectionWriteCandidate(
            collectionPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values: values,
            semanticIdentityValues: semanticIdentityValues,
            attachedAlignedScopeData: attachedAlignedScopeData
        );
    }

    private static ProfileAppliedWriteRequest CreateProfileRequestWithCollectionItems(
        ImmutableArray<RequestScopeState> scopeStates,
        ImmutableArray<VisibleRequestCollectionItem> visibleRequestCollectionItems
    )
    {
        return new ProfileAppliedWriteRequest(
            WritableRequestBody: System.Text.Json.Nodes.JsonNode.Parse("{}")!,
            RootResourceCreatable: true,
            RequestScopeStates: scopeStates,
            VisibleRequestCollectionItems: visibleRequestCollectionItems
        );
    }

    private static ProfileAppliedWriteContext CreateProfileContextWithCollectionRows(
        ProfileAppliedWriteRequest request,
        ImmutableArray<StoredScopeState> storedScopeStates,
        ImmutableArray<VisibleStoredCollectionRow> visibleStoredCollectionRows
    )
    {
        return new ProfileAppliedWriteContext(
            Request: request,
            VisibleStoredBody: System.Text.Json.Nodes.JsonNode.Parse("{}")!,
            StoredScopeStates: storedScopeStates,
            VisibleStoredCollectionRows: visibleStoredCollectionRows
        );
    }

    private static VisibleStoredCollectionRow CreateVisibleStoredCollectionRow(
        string jsonScope,
        string semanticIdentityValue,
        ImmutableArray<string> hiddenMemberPaths
    )
    {
        return new VisibleStoredCollectionRow(
            new CollectionRowAddress(
                jsonScope,
                new ScopeInstanceAddress("$", []),
                [
                    new SemanticIdentityPart(
                        "$.classPeriodName",
                        System.Text.Json.Nodes.JsonValue.Create(semanticIdentityValue),
                        IsPresent: true
                    ),
                ]
            ),
            hiddenMemberPaths
        );
    }

    private static VisibleRequestCollectionItem CreateVisibleRequestCollectionItem(
        string jsonScope,
        string semanticIdentityValue
    )
    {
        return new VisibleRequestCollectionItem(
            new CollectionRowAddress(
                jsonScope,
                new ScopeInstanceAddress("$", []),
                [
                    new SemanticIdentityPart(
                        "$.classPeriodName",
                        System.Text.Json.Nodes.JsonValue.Create(semanticIdentityValue),
                        IsPresent: true
                    ),
                ]
            ),
            Creatable: true,
            RequestJsonPath: $"$.classPeriods[0]"
        );
    }

    private static VisibleStoredCollectionRow CreateVisibleStoredCollectionRowForScope(
        string jsonScope,
        string semanticIdentityValue,
        string semanticIdentityPath,
        ImmutableArray<string> hiddenMemberPaths
    )
    {
        return new VisibleStoredCollectionRow(
            new CollectionRowAddress(
                jsonScope,
                new ScopeInstanceAddress("$", []),
                [
                    new SemanticIdentityPart(
                        semanticIdentityPath,
                        System.Text.Json.Nodes.JsonValue.Create(semanticIdentityValue),
                        IsPresent: true
                    ),
                ]
            ),
            hiddenMemberPaths
        );
    }

    private static VisibleRequestCollectionItem CreateVisibleRequestCollectionItemForScope(
        string jsonScope,
        string semanticIdentityValue
    )
    {
        return new VisibleRequestCollectionItem(
            new CollectionRowAddress(
                jsonScope,
                new ScopeInstanceAddress("$", []),
                [
                    new SemanticIdentityPart(
                        "$.visibleItemName",
                        System.Text.Json.Nodes.JsonValue.Create(semanticIdentityValue),
                        IsPresent: true
                    ),
                ]
            ),
            Creatable: true,
            RequestJsonPath: $"{jsonScope}[0]"
        );
    }

    // --- Type-aware semantic identity comparison tests ---

    [Test]
    public void It_updates_matched_collection_row_when_semantic_identity_is_date_type()
    {
        // Exercises the bug: DateOnly.ToString() produces culture-dependent output (e.g. "8/20/2026")
        // while JsonNode containing "2026-08-20" serializes to "\"2026-08-20\"" via ToString().
        // Both must produce the same canonical form for semantic identity matching.
        var fixture = CreateDateIdentityCollectionFixture();

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: [new DateOnly(2026, 8, 20)],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal(new DateOnly(2026, 8, 20)),
                            Literal("Updated Value"),
                        ]
                    ),
                ]
            )
        );

        // Current row has DateOnly (simulating DB hydration)
        var currentState = CreateCurrentStateWithCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 100L, 0, new DateOnly(2026, 8, 20), "Original Value"],
            ]
        );

        // Profile metadata uses JsonNode for semantic identity (Core emits these from JSON)
        var profileRequest = CreateProfileRequestWithCollectionItems(
            [new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true)],
            [
                new VisibleRequestCollectionItem(
                    new CollectionRowAddress(
                        "$.sessionPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.beginDate",
                                System.Text.Json.Nodes.JsonValue.Create("2026-08-20"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    Creatable: true,
                    RequestJsonPath: "$.sessionPeriods[0]"
                ),
            ]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            [
                new VisibleStoredCollectionRow(
                    new CollectionRowAddress(
                        "$.sessionPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.beginDate",
                                System.Text.Json.Nodes.JsonValue.Create("2026-08-20"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    HiddenMemberPaths: []
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var collectionState = result.TablesInDependencyOrder[1];

        // The key assertion: DateOnly identity must match JsonNode identity,
        // producing an UPDATE (not delete+insert)
        collectionState.Updates.Should().ContainSingle();
        collectionState.Inserts.Should().BeEmpty();
        collectionState.Deletes.Should().BeEmpty();

        // Verify the update carries the stable row identity from the matched current row
        collectionState.Updates[0].StableRowIdentityValue.Should().Be(100L);
        LiteralValue(collectionState.Updates[0].Values[4]).Should().Be("Updated Value");
    }

    [Test]
    public void It_updates_matched_collection_row_when_semantic_identity_is_time_type()
    {
        var fixture = CreateTimeIdentityCollectionFixture();

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: [new TimeOnly(14, 30, 0)],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal(new TimeOnly(14, 30, 0)),
                            Literal("Updated Value"),
                        ]
                    ),
                ]
            )
        );

        var currentState = CreateCurrentStateWithCollection(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 100L, 0, new TimeOnly(14, 30, 0), "Original Value"],
            ]
        );

        var profileRequest = CreateProfileRequestWithCollectionItems(
            [new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true)],
            [
                new VisibleRequestCollectionItem(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.startTime",
                                System.Text.Json.Nodes.JsonValue.Create("14:30:00"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    Creatable: true,
                    RequestJsonPath: "$.classPeriods[0]"
                ),
            ]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            [
                new VisibleStoredCollectionRow(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.startTime",
                                System.Text.Json.Nodes.JsonValue.Create("14:30:00"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    HiddenMemberPaths: []
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var collectionState = result.TablesInDependencyOrder[1];

        collectionState.Updates.Should().ContainSingle();
        collectionState.Inserts.Should().BeEmpty();
        collectionState.Deletes.Should().BeEmpty();
        collectionState.Updates[0].StableRowIdentityValue.Should().Be(100L);
    }

    // --- Per-instance inlined scope path tests ---

    [Test]
    public void It_clears_inlined_scope_columns_per_collection_item_based_on_instance_visibility()
    {
        // Scenario: collection table $.classPeriods has an inlined non-collection child scope
        // $.classPeriods.calendarReference. Two collection items exist:
        //   "Period1" — inlined scope is VisiblePresent (columns preserved)
        //   "Period2" — inlined scope is VisibleAbsent (columns cleared to null)
        var fixture = CreateCollectionWithInlinedScopeFixture();

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: ["Period1"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("Period1"),
                            Literal("UpdatedValue1"),
                            Literal("REQUEST_CAL_REF_1"),
                        ]
                    ),
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 1,
                        semanticIdentityValues: ["Period2"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(1),
                            Literal("Period2"),
                            Literal("UpdatedValue2"),
                            Literal("REQUEST_CAL_REF_2"),
                        ]
                    ),
                ]
            )
        );

        var currentState = CreateCurrentStateWithCollection(
            new CollectionFixture(fixture.WritePlan, fixture.RootPlan, fixture.CollectionPlan),
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 100L, 0, "Period1", "OrigValue1", "STORED_CAL_REF_1"],
                [345L, 101L, 1, "Period2", "OrigValue2", "STORED_CAL_REF_2"],
            ]
        );

        // Build ancestor context keys for the two items (matches ExtendAncestorContextKey output)
        // Key format: {jsonScope}\0{isPresent}\0{identityValue}
        var period1Ancestor = new AncestorCollectionInstance(
            "$.classPeriods",
            [
                new SemanticIdentityPart(
                    "$.classPeriodName",
                    System.Text.Json.Nodes.JsonValue.Create("Period1"),
                    IsPresent: true
                ),
            ]
        );
        var period2Ancestor = new AncestorCollectionInstance(
            "$.classPeriods",
            [
                new SemanticIdentityPart(
                    "$.classPeriodName",
                    System.Text.Json.Nodes.JsonValue.Create("Period2"),
                    IsPresent: true
                ),
            ]
        );

        // Request scope states:
        // - Root: VisiblePresent
        // - Inlined scope for Period1: VisiblePresent (keep columns)
        // - Inlined scope for Period2: VisibleAbsent (clear columns)
        var profileRequest = CreateProfileRequestWithCollectionItems(
            [
                new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    new ScopeInstanceAddress("$.classPeriods.calendarReference", [period1Ancestor]),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    new ScopeInstanceAddress("$.classPeriods.calendarReference", [period2Ancestor]),
                    ProfileVisibilityKind.VisibleAbsent,
                    Creatable: false
                ),
            ],
            [
                new VisibleRequestCollectionItem(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.classPeriodName",
                                System.Text.Json.Nodes.JsonValue.Create("Period1"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    Creatable: true,
                    RequestJsonPath: "$.classPeriods[0]"
                ),
                new VisibleRequestCollectionItem(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.classPeriodName",
                                System.Text.Json.Nodes.JsonValue.Create("Period2"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    Creatable: true,
                    RequestJsonPath: "$.classPeriods[1]"
                ),
            ]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    new ScopeInstanceAddress("$.classPeriods.calendarReference", [period1Ancestor]),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    new ScopeInstanceAddress("$.classPeriods.calendarReference", [period2Ancestor]),
                    ProfileVisibilityKind.VisibleAbsent,
                    HiddenMemberPaths: []
                ),
            ],
            [
                new VisibleStoredCollectionRow(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.classPeriodName",
                                System.Text.Json.Nodes.JsonValue.Create("Period1"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    HiddenMemberPaths: []
                ),
                new VisibleStoredCollectionRow(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.classPeriodName",
                                System.Text.Json.Nodes.JsonValue.Create("Period2"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    HiddenMemberPaths: []
                ),
            ]
        );

        // CompiledScopeCatalog must include the inlined scope descriptor
        var compiledScopeCatalog = new CompiledScopeDescriptor[]
        {
            new(
                JsonScope: "$.classPeriods.calendarReference",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.classPeriods",
                CollectionAncestorsInOrder: ["$.classPeriods"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["calendarReferenceId"]
            ),
        };

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                compiledScopeCatalog
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        result.TablesInDependencyOrder.Should().HaveCount(2);
        var collectionState = result.TablesInDependencyOrder[1];

        // Both items should be matched updates (no inserts/deletes)
        collectionState.Updates.Should().HaveCount(2);
        collectionState.Inserts.Should().BeEmpty();
        collectionState.Deletes.Should().BeEmpty();

        // Find the updates by their stable row identity (Period1=100, Period2=101)
        var period1Update = collectionState.Updates.First(u => u.StableRowIdentityValue == 100L);
        var period2Update = collectionState.Updates.First(u => u.StableRowIdentityValue == 101L);

        // Period1: inlined scope is VisiblePresent → calRef column (index 5) preserved from request
        LiteralValue(period1Update.Values[4]).Should().Be("UpdatedValue1");
        LiteralValue(period1Update.Values[5]).Should().Be("REQUEST_CAL_REF_1");

        // Period2: inlined scope is VisibleAbsent → calRef column (index 5) cleared to null
        LiteralValue(period2Update.Values[4]).Should().Be("UpdatedValue2");
        LiteralValue(period2Update.Values[5]).Should().BeNull("inlined scope is VisibleAbsent for Period2");
    }

    // --- Collection-aligned extension scope with inlined child scope tests ---

    [Test]
    public void It_clears_inlined_scope_columns_per_collection_item_on_aligned_extension_scope()
    {
        // Scenario: collection table $.classPeriods has a collection-aligned extension scope
        // $.classPeriods._ext.sample, which owns an inlined non-table-backed child scope
        // $.classPeriods._ext.sample.someRef. Two collection items exist:
        //   "Period1" — inlined scope is VisiblePresent (InlinedRefId preserved from request)
        //   "Period2" — inlined scope is VisibleAbsent (InlinedRefId cleared to null)
        // This exercises the fix for per-instance inlined scope path computation on
        // collection-aligned extension scope tables.
        var fixture = CreateCollectionWithAlignedExtensionScopeFixture();

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: ["Period1"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("Period1"),
                            Literal("UpdatedValue1"),
                        ],
                        attachedAlignedScopeData:
                        [
                            new CandidateAttachedAlignedScopeData(
                                fixture.AlignedExtensionScopePlan,
                                [Literal(345L), Literal(100L), Literal("ext-val-1"), Literal("REQ_REF_1")]
                            ),
                        ]
                    ),
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 1,
                        semanticIdentityValues: ["Period2"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(1),
                            Literal("Period2"),
                            Literal("UpdatedValue2"),
                        ],
                        attachedAlignedScopeData:
                        [
                            new CandidateAttachedAlignedScopeData(
                                fixture.AlignedExtensionScopePlan,
                                [Literal(345L), Literal(101L), Literal("ext-val-2"), Literal("REQ_REF_2")]
                            ),
                        ]
                    ),
                ]
            )
        );

        var currentState = CreateCurrentStateWithCollectionAndAlignedExtensionScope(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 100L, 0, "Period1", "OrigValue1"],
                [345L, 101L, 1, "Period2", "OrigValue2"],
            ],
            alignedExtensionScopeRows:
            [
                [345L, 100L, "stored-ext-1", "STORED_REF_1"],
                [345L, 101L, "stored-ext-2", "STORED_REF_2"],
            ]
        );

        // Ancestor collection instances for per-instance scope addresses
        var period1Ancestor = new AncestorCollectionInstance(
            "$.classPeriods",
            [
                new SemanticIdentityPart(
                    "$.classPeriodName",
                    System.Text.Json.Nodes.JsonValue.Create("Period1"),
                    IsPresent: true
                ),
            ]
        );
        var period2Ancestor = new AncestorCollectionInstance(
            "$.classPeriods",
            [
                new SemanticIdentityPart(
                    "$.classPeriodName",
                    System.Text.Json.Nodes.JsonValue.Create("Period2"),
                    IsPresent: true
                ),
            ]
        );

        // Request scope states: aligned extension scope is VisiblePresent for both items,
        // but the inlined child scope differs: VisiblePresent for Period1, VisibleAbsent for Period2.
        var profileRequest = CreateProfileRequestWithCollectionItems(
            [
                new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
                new RequestScopeState(
                    new ScopeInstanceAddress("$.classPeriods._ext.sample", [period1Ancestor]),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    new ScopeInstanceAddress("$.classPeriods._ext.sample", [period2Ancestor]),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    new ScopeInstanceAddress("$.classPeriods._ext.sample.someRef", [period1Ancestor]),
                    ProfileVisibilityKind.VisiblePresent,
                    Creatable: true
                ),
                new RequestScopeState(
                    new ScopeInstanceAddress("$.classPeriods._ext.sample.someRef", [period2Ancestor]),
                    ProfileVisibilityKind.VisibleAbsent,
                    Creatable: false
                ),
            ],
            [
                new VisibleRequestCollectionItem(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.classPeriodName",
                                System.Text.Json.Nodes.JsonValue.Create("Period1"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    Creatable: true,
                    RequestJsonPath: "$.classPeriods[0]"
                ),
                new VisibleRequestCollectionItem(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.classPeriodName",
                                System.Text.Json.Nodes.JsonValue.Create("Period2"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    Creatable: true,
                    RequestJsonPath: "$.classPeriods[1]"
                ),
            ]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    new ScopeInstanceAddress("$.classPeriods._ext.sample", [period1Ancestor]),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    new ScopeInstanceAddress("$.classPeriods._ext.sample", [period2Ancestor]),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    new ScopeInstanceAddress("$.classPeriods._ext.sample.someRef", [period1Ancestor]),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    new ScopeInstanceAddress("$.classPeriods._ext.sample.someRef", [period2Ancestor]),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            [
                new VisibleStoredCollectionRow(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.classPeriodName",
                                System.Text.Json.Nodes.JsonValue.Create("Period1"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    HiddenMemberPaths: []
                ),
                new VisibleStoredCollectionRow(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.classPeriodName",
                                System.Text.Json.Nodes.JsonValue.Create("Period2"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    HiddenMemberPaths: []
                ),
            ]
        );

        // Compiled scope catalog: the inlined child scope
        var compiledScopeCatalog = new CompiledScopeDescriptor[]
        {
            new(
                JsonScope: "$.classPeriods._ext.sample.someRef",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.classPeriods._ext.sample",
                CollectionAncestorsInOrder: ["$.classPeriods"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["refId"]
            ),
        };

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                compiledScopeCatalog
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        // Root + Collection + AlignedExtensionScope = 3 tables
        result.TablesInDependencyOrder.Should().HaveCount(3);

        // The aligned extension scope table is index 2 (dependency order)
        var extScopeState = result.TablesInDependencyOrder[2];

        // Both items produce operations on the aligned scope table (2 merged rows).
        // Whether they are inserts or updates depends on current-state matching; the
        // critical assertion is that the inlined scope columns are classified correctly.
        var allExtOps = extScopeState
            .Inserts.Select(ins => ins.Values)
            .Concat(extScopeState.Updates.Select(upd => upd.Values))
            .ToList();
        allExtOps.Should().HaveCount(2);

        // Period1 aligned scope: InlinedRefId (index 3) preserved from request
        var period1Values = allExtOps.First(v => LiteralValue(v[2]) is "ext-val-1");
        LiteralValue(period1Values[3]).Should().Be("REQ_REF_1");

        // Period2 aligned scope: InlinedRefId (index 3) cleared to null
        var period2Values = allExtOps.First(v => LiteralValue(v[2]) is "ext-val-2");
        LiteralValue(period2Values[3])
            .Should()
            .BeNull("inlined scope is VisibleAbsent for Period2's aligned extension scope");
    }

    [Test]
    public void It_returns_contract_mismatch_when_inlined_scope_under_collection_has_no_per_instance_request_scope_state()
    {
        // Scenario: collection table $.classPeriods has an inlined non-table-backed child scope
        // $.classPeriods.calendarReference. One collection item exists, but the per-instance
        // RequestScopeState for the inlined scope is missing (only the stored state exists).
        // Merge surfaces this as a deterministic ContractMismatch outcome rather than throwing.
        var fixture = CreateCollectionWithInlinedScopeFixture();

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                collectionCandidates:
                [
                    CreateCollectionCandidate(
                        fixture.CollectionPlan,
                        requestOrder: 0,
                        semanticIdentityValues: ["Period1"],
                        values:
                        [
                            Literal(345L),
                            FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                            Literal(0),
                            Literal("Period1"),
                            Literal("UpdatedValue1"),
                            Literal("REQUEST_CAL_REF_1"),
                        ]
                    ),
                ]
            )
        );

        var currentState = CreateCurrentStateWithCollection(
            new CollectionFixture(fixture.WritePlan, fixture.RootPlan, fixture.CollectionPlan),
            rootRows:
            [
                [345L, "School Name"],
            ],
            collectionRows:
            [
                [345L, 100L, 0, "Period1", "OrigValue1", "STORED_CAL_REF_1"],
            ]
        );

        var period1Ancestor = new AncestorCollectionInstance(
            "$.classPeriods",
            [
                new SemanticIdentityPart(
                    "$.classPeriodName",
                    System.Text.Json.Nodes.JsonValue.Create("Period1"),
                    IsPresent: true
                ),
            ]
        );

        // Request scope states: root and collection item are present,
        // but the per-instance RequestScopeState for the inlined scope is MISSING.
        var profileRequest = CreateProfileRequestWithCollectionItems(
            [
                new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
                // Deliberately omitted: RequestScopeState for $.classPeriods.calendarReference
            ],
            [
                new VisibleRequestCollectionItem(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.classPeriodName",
                                System.Text.Json.Nodes.JsonValue.Create("Period1"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    Creatable: true,
                    RequestJsonPath: "$.classPeriods[0]"
                ),
            ]
        );

        var profileContext = CreateProfileContextWithCollectionRows(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                // Stored scope state for the inlined scope exists (scope is not hidden)
                new StoredScopeState(
                    new ScopeInstanceAddress("$.classPeriods.calendarReference", [period1Ancestor]),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
            ],
            [
                new VisibleStoredCollectionRow(
                    new CollectionRowAddress(
                        "$.classPeriods",
                        new ScopeInstanceAddress("$", []),
                        [
                            new SemanticIdentityPart(
                                "$.classPeriodName",
                                System.Text.Json.Nodes.JsonValue.Create("Period1"),
                                IsPresent: true
                            ),
                        ]
                    ),
                    HiddenMemberPaths: []
                ),
            ]
        );

        var compiledScopeCatalog = new CompiledScopeDescriptor[]
        {
            new(
                JsonScope: "$.classPeriods.calendarReference",
                ScopeKind: ScopeKind.NonCollection,
                ImmediateParentJsonScope: "$.classPeriods",
                CollectionAncestorsInOrder: ["$.classPeriods"],
                SemanticIdentityRelativePathsInOrder: [],
                CanonicalScopeRelativeMemberPaths: ["calendarReferenceId"]
            ),
        };

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: compiledScopeCatalog
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.ContractMismatch>();
        var mismatch = (RelationalWriteMergeSynthesisOutcome.ContractMismatch)outcome;
        mismatch.Messages.Should().HaveCount(1);
        mismatch
            .Messages[0]
            .Should()
            .Contain("'$.classPeriods.calendarReference'")
            .And.Contain("no per-instance RequestScopeState");
    }

    // --- Guarded no-op detection tests ---

    [Test]
    public void It_detects_no_op_when_profile_merge_is_unchanged()
    {
        // Directly construct a profile merge result where comparable current and merged rowsets
        // are identical — simulating the case where the document values are unchanged.
        var fixture = CreateFixture();

        var sharedRow = new MergeTableRow(
            [Literal(345L), Literal("Same Name"), Literal("HIDDEN_CODE")],
            [Literal(345L), Literal("Same Name"), Literal("HIDDEN_CODE")]
        );

        var result = new RelationalWriteMergeResult([
            new RelationalWriteMergeTableState(
                fixture.RootPlan,
                inserts: [],
                updates: [new MergeRowUpdate(sharedRow.Values, StableRowIdentityValue: null)],
                deletes: [],
                preservedRows: [],
                comparableCurrentRowset: [sharedRow],
                comparableMergedRowset: [sharedRow]
            ),
        ]);

        RelationalWriteGuardedNoOp.IsNoOpCandidate(result).Should().BeTrue();
    }

    [Test]
    public void It_detects_change_when_profile_merge_differs()
    {
        // Directly construct a profile merge result where the visible value differs between
        // current and merged rowsets.
        var fixture = CreateFixture();

        var currentRow = new MergeTableRow(
            [Literal(345L), Literal("Original Name"), Literal("HIDDEN_CODE")],
            [Literal(345L), Literal("Original Name"), Literal("HIDDEN_CODE")]
        );
        var mergedRow = new MergeTableRow(
            [Literal(345L), Literal("Updated Name"), Literal("HIDDEN_CODE")],
            [Literal(345L), Literal("Updated Name"), Literal("HIDDEN_CODE")]
        );

        var result = new RelationalWriteMergeResult([
            new RelationalWriteMergeTableState(
                fixture.RootPlan,
                inserts: [],
                updates: [new MergeRowUpdate(mergedRow.Values, StableRowIdentityValue: null)],
                deletes: [],
                preservedRows: [],
                comparableCurrentRowset: [currentRow],
                comparableMergedRowset: [mergedRow]
            ),
        ]);

        RelationalWriteGuardedNoOp.IsNoOpCandidate(result).Should().BeFalse();
    }

    [Test]
    public void It_detects_change_when_row_count_differs()
    {
        // Directly construct a profile merge result where the collection table's current rowset
        // has 1 row but the merged rowset has 2 — simulating a newly inserted visible collection item.
        var fixture = CreateCollectionFixture();

        var rootRow = new MergeTableRow(
            [Literal(345L), Literal("School Name")],
            [Literal(345L), Literal("School Name")]
        );
        var collectionRow1 = new MergeTableRow(
            [
                Literal(345L),
                Literal(100L),
                Literal(0),
                Literal("Period1"),
                Literal("Value1"),
                Literal("HIDDEN1"),
            ],
            [Literal(345L), Literal(0), Literal("Period1"), Literal("Value1")]
        );
        var collectionRow2 = new MergeTableRow(
            [Literal(345L), Literal(101L), Literal(1), Literal("Period2"), Literal("Value2"), Literal(null)],
            [Literal(345L), Literal(1), Literal("Period2"), Literal("Value2")]
        );

        var result = new RelationalWriteMergeResult([
            new RelationalWriteMergeTableState(
                fixture.RootPlan,
                inserts: [],
                updates: [new MergeRowUpdate(rootRow.Values, StableRowIdentityValue: null)],
                deletes: [],
                preservedRows: [],
                comparableCurrentRowset: [rootRow],
                comparableMergedRowset: [rootRow]
            ),
            new RelationalWriteMergeTableState(
                fixture.CollectionPlan,
                inserts: [],
                updates:
                [
                    new MergeRowUpdate(collectionRow1.Values, StableRowIdentityValue: 100L),
                    new MergeRowUpdate(collectionRow2.Values, StableRowIdentityValue: 101L),
                ],
                deletes: [],
                preservedRows: [],
                comparableCurrentRowset: [collectionRow1],
                comparableMergedRowset: [collectionRow1, collectionRow2]
            ),
        ]);

        RelationalWriteGuardedNoOp.IsNoOpCandidate(result).Should().BeFalse();
    }

    [Test]
    public void It_populates_semantic_identity_presence_flags_with_fallback_for_absent_values()
    {
        // When semanticIdentityPresenceFlags is not provided, the fallback derives
        // presence from non-null values: null -> absent (false), non-null -> present (true).
        var collectionPlan = CreateCollectionPlanWithHiddenColumn();
        var candidate = CreateCollectionCandidate(
            collectionPlan,
            requestOrder: 0,
            semanticIdentityValues: [null],
            values:
            [
                Literal(345L),
                FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                Literal(0),
                Literal(null),
                Literal("Value1"),
                Literal(null),
            ]
        );

        // Absent identity member: presence flag should be false
        candidate.SemanticIdentityPresenceFlags[0].Should().BeFalse();

        // Non-null identity member
        var candidate2 = CreateCollectionCandidate(
            collectionPlan,
            requestOrder: 0,
            semanticIdentityValues: ["Period1"],
            values:
            [
                Literal(345L),
                FlattenedWriteValue.UnresolvedCollectionItemId.Create(),
                Literal(0),
                Literal("Period1"),
                Literal("Value1"),
                Literal(null),
            ]
        );

        candidate2.SemanticIdentityPresenceFlags[0].Should().BeTrue();
    }

    // --- StoredScopeStates second-pass tests ---

    [Test]
    public void It_emits_delete_for_StoredScopeState_VisibleAbsent_separate_table_scope_not_in_buffer()
    {
        // Scenario: extension scope is VisibleAbsent in StoredScopeStates but NOT in the
        // flattened buffer (the flattener dropped it because it was omitted from the request).
        // Current state has data for the extension table. The second pass should emit a delete.
        var fixture = CreateFixtureWithExtension();

        // No extension row in the flattened write set (scope was omitted from request)
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")]
            )
        );

        var currentState = CreateCurrentStateWithExtension(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows:
            [
                [345L, "stored-ext", "STORED_HIDDEN"],
            ]
        );

        var extensionScope = "$._ext.sample";
        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
            // Extension scope NOT in request scope states — it was omitted from the request body
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                // VisibleAbsent in stored scope states — scope exists in current state but
                // was omitted from the request body, signaling intentional deletion.
                new StoredScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisibleAbsent,
                    HiddenMemberPaths: []
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        // Extension table should have exactly one delete (from the second pass)
        var extensionState = result.TablesInDependencyOrder[1];
        extensionState.Inserts.Should().BeEmpty();
        extensionState.Updates.Should().BeEmpty();
        extensionState.Deletes.Should().ContainSingle();
        extensionState.PreservedRows.Should().BeEmpty();
    }

    [Test]
    public void It_does_not_emit_duplicate_delete_when_buffer_iteration_already_processed_scope()
    {
        // Scenario: extension scope is VisibleAbsent and IS in the flattened buffer.
        // The buffer iteration already emits a delete. The second pass should NOT emit
        // a second delete because the scope key is already in visitedScopeKeys.
        var fixture = CreateFixtureWithExtension();

        // Extension row IS present in the flattened write set buffer
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("ignored-ext"), Literal("ignored-hidden")]
                    ),
                ]
            )
        );

        var currentState = CreateCurrentStateWithExtension(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows:
            [
                [345L, "stored-ext", "STORED_HIDDEN"],
            ]
        );

        var extensionScope = "$._ext.sample";
        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
            new RequestScopeState(
                ScopeAddress(extensionScope),
                ProfileVisibilityKind.VisibleAbsent,
                Creatable: false
            ),
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                new StoredScopeState(
                    ScopeAddress(extensionScope),
                    ProfileVisibilityKind.VisibleAbsent,
                    HiddenMemberPaths: []
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog: []
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        // Extension table should have exactly ONE delete (from buffer iteration, not duplicated)
        var extensionState = result.TablesInDependencyOrder[1];
        extensionState.Deletes.Should().ContainSingle();
    }

    [Test]
    public void It_returns_ContractMismatch_when_StoredScopeState_references_unknown_scope()
    {
        // Scenario: StoredScopeStates contains a VisibleAbsent entry for a scope that
        // is not in the CompiledScopeCatalog and has no table plan. The second pass
        // should return a ContractMismatch outcome.
        var fixture = CreateFixtureWithExtension();

        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, Literal("School Name")]
            )
        );

        var currentState = CreateCurrentStateWithExtension(
            fixture,
            rootRows:
            [
                [345L, "School Name"],
            ],
            rootExtensionRows:
            [
                [345L, "stored-ext", "STORED_HIDDEN"],
            ]
        );

        var unknownScope = "$._ext.nonexistent";
        var profileRequest = CreateProfileRequest([
            new RequestScopeState(RootAddress(), ProfileVisibilityKind.VisiblePresent, Creatable: true),
        ]);
        var profileContext = CreateProfileContext(
            profileRequest,
            [
                new StoredScopeState(
                    RootAddress(),
                    ProfileVisibilityKind.VisiblePresent,
                    HiddenMemberPaths: []
                ),
                // Unknown scope in StoredScopeStates
                new StoredScopeState(
                    ScopeAddress(unknownScope),
                    ProfileVisibilityKind.VisibleAbsent,
                    HiddenMemberPaths: []
                ),
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                profileRequest,
                profileContext,
                CompiledScopeCatalog:
                [
                    new CompiledScopeDescriptor("$", ScopeKind.Root, null, [], [], []),
                    new CompiledScopeDescriptor(
                        "$._ext.sample",
                        ScopeKind.NonCollection,
                        ImmediateParentJsonScope: "$",
                        CollectionAncestorsInOrder: [],
                        SemanticIdentityRelativePathsInOrder: [],
                        CanonicalScopeRelativeMemberPaths: []
                    ),
                ]
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.ContractMismatch>();
        var mismatch = (RelationalWriteMergeSynthesisOutcome.ContractMismatch)outcome;
        mismatch.Messages.Should().HaveCount(1);
        mismatch.Messages[0].Should().Contain(unknownScope).And.Contain("CompiledScopeCatalog");
    }

    // --- Value helpers ---

    private static FlattenedWriteValue Literal(object? value) => new FlattenedWriteValue.Literal(value);

    private static object? LiteralValue(FlattenedWriteValue value) =>
        value is FlattenedWriteValue.Literal literalValue
            ? literalValue.Value
            : throw new AssertionException($"Expected a literal value but found '{value.GetType().Name}'.");
}

/// <summary>
/// End-to-end test: null-profile callers route through
/// <see cref="NoProfileSyntheticProfileAdapter"/> inside the synthesizer.
/// A root + root-extension resource where the current state has an extension row
/// but the request body omits the extension.  With null profile inputs the
/// synthesizer should route through the adapter, classify the extension scope as
/// VisibleAbsent, and emit a delete for it via the second-pass iteration.
/// </summary>
[TestFixture]
[Parallelizable]
public sealed class Given_a_null_profile_request_with_omitted_root_extension
{
    private RelationalWriteMergeSynthesisOutcome _outcome = null!;

    [SetUp]
    public void Setup()
    {
        // Root plan: School with a single Name column.
        var rootPlan = PlanBuilder.CreateTablePlan(
            tableName: "School",
            jsonScope: "$",
            tableKind: DbTableKind.Root,
            columns: [("Name", "$.name")]
        );

        // Extension plan: SchoolExt with a single ExtField column.
        var extensionPlan = PlanBuilder.CreateTablePlan(
            tableName: "SchoolExt",
            jsonScope: "$._ext.SchoolExt",
            tableKind: DbTableKind.RootExtension,
            columns: [("ExtField", "$.extField")]
        );

        var plan = PlanBuilder.BuildPlan([rootPlan, extensionPlan]);

        // Flattened request has only the root row — the extension is omitted.
        var flattenedWriteSet = AdapterTestHelpers.BuildFlattenedWriteSet(
            rootPlan,
            rootExtensionRows: [],
            collectionCandidates: []
        );

        // Current state has both root and extension rows.
        var currentState = AdapterTestHelpers.BuildCurrentState([
            (rootPlan.TableModel, 1),
            (extensionPlan.TableModel, 1),
        ]);

        var requestBodyJson = System.Text.Json.Nodes.JsonNode.Parse("""{"name":"Updated Name"}""")!;

        var request = new RelationalWriteMergeRequest(
            WritePlan: plan,
            FlattenedWriteSet: flattenedWriteSet,
            CurrentState: currentState,
            ProfileRequest: null,
            ProfileContext: null,
            CompiledScopeCatalog: null,
            SelectedBody: requestBodyJson
        );

        _outcome = new RelationalWriteMergeSynthesizer().Synthesize(request);
    }

    [Test]
    public void It_emits_a_delete_for_the_omitted_root_extension()
    {
        var success = _outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>().Which;
        var extTable = success.MergeResult.TablesInDependencyOrder.Single(t =>
            t.TableWritePlan.TableModel.Table.Name == "SchoolExt"
        );
        extTable.Deletes.Should().HaveCount(1);
    }

    [Test]
    public void It_leaves_PreservedRows_empty_in_every_table()
    {
        var success = _outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>().Which;
        success
            .MergeResult.TablesInDependencyOrder.Should()
            .AllSatisfy(t => t.PreservedRows.Should().BeEmpty());
    }
}

/// <summary>
/// Ports the 5 scenarios from <c>Given_Relational_Write_No_Profile_Merge_Synthesizer</c>
/// (which drives the legacy <c>RelationalWriteNoProfileMergeSynthesizer</c>) to the unified
/// <c>RelationalWriteMergeSynthesizer</c> with null-profile inputs. The adapter routes these
/// through <c>NoProfileSyntheticProfileAdapter</c> internally.
///
/// Assertion translations from the legacy no-profile result shape:
/// - <c>CurrentRows</c>     → <c>ComparableCurrentRowset</c>
/// - <c>MergedRows</c>      → <c>ComparableMergedRowset</c>
/// - Implicit diff-based deletes → explicit <c>Deletes</c> actions
/// - Implicit inserts           → explicit <c>Inserts</c> actions
/// - Implicit updates           → explicit <c>Updates</c> actions
/// - Added assertion: <c>PreservedRows.IsEmpty</c> for every table (null-profile never preserves).
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_Unified_Merge_Synthesizer_Under_Null_Profile
{
    private RelationalWriteMergeSynthesizer _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new RelationalWriteMergeSynthesizer();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Ported from Given_Relational_Write_No_Profile_Merge_Synthesizer
    // ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void It_projects_non_collection_rows_into_the_shared_compare_space()
    {
        var fixture = CreateNoProfileFixture();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal("Lincoln Updated")],
                rootExtensionRows:
                [
                    new RootExtensionWriteRowBuffer(
                        fixture.RootExtensionPlan,
                        [Literal(345L), Literal("sample-2")]
                    ),
                ]
            )
        );
        var currentState = CreateNoProfileCurrentState(
            fixture,
            rootRows:
            [
                [345L, "Lincoln High"],
            ],
            rootExtensionRows:
            [
                [345L, "sample-1"],
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                ProfileRequest: null,
                ProfileContext: null,
                CompiledScopeCatalog: null
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        result.TablesInDependencyOrder.Should().HaveCount(4);

        var rootState = result.TablesInDependencyOrder[0];
        rootState.ComparableCurrentRowset.Should().ContainSingle();
        rootState.ComparableMergedRowset.Should().ContainSingle();
        rootState.PreservedRows.Should().BeEmpty();
        LiteralValue(rootState.ComparableCurrentRowset[0].Values[0]).Should().Be(345L);
        LiteralValue(rootState.ComparableCurrentRowset[0].Values[1]).Should().Be("Lincoln High");
        LiteralValue(rootState.ComparableMergedRowset[0].Values[0]).Should().Be(345L);
        LiteralValue(rootState.ComparableMergedRowset[0].Values[1]).Should().Be("Lincoln Updated");
        rootState
            .ComparableCurrentRowset[0]
            .ComparableValues.Should()
            .BeEquivalentTo(rootState.ComparableCurrentRowset[0].Values);
        rootState
            .ComparableMergedRowset[0]
            .ComparableValues.Should()
            .BeEquivalentTo(rootState.ComparableMergedRowset[0].Values);

        var rootExtensionState = result.TablesInDependencyOrder[1];
        rootExtensionState.ComparableCurrentRowset.Should().ContainSingle();
        rootExtensionState.ComparableMergedRowset.Should().ContainSingle();
        rootExtensionState.PreservedRows.Should().BeEmpty();
        LiteralValue(rootExtensionState.ComparableCurrentRowset[0].Values[1]).Should().Be("sample-1");
        LiteralValue(rootExtensionState.ComparableMergedRowset[0].Values[1]).Should().Be("sample-2");
        rootExtensionState
            .ComparableCurrentRowset[0]
            .ComparableValues.Should()
            .BeEquivalentTo(rootExtensionState.ComparableCurrentRowset[0].Values);
        rootExtensionState
            .ComparableMergedRowset[0]
            .ComparableValues.Should()
            .BeEquivalentTo(rootExtensionState.ComparableMergedRowset[0].Values);

        result.TablesInDependencyOrder[2].ComparableCurrentRowset.Should().BeEmpty();
        result.TablesInDependencyOrder[2].ComparableMergedRowset.Should().BeEmpty();
        result.TablesInDependencyOrder[2].PreservedRows.Should().BeEmpty();
        result.TablesInDependencyOrder[3].ComparableCurrentRowset.Should().BeEmpty();
        result.TablesInDependencyOrder[3].ComparableMergedRowset.Should().BeEmpty();
        result.TablesInDependencyOrder[3].PreservedRows.Should().BeEmpty();
    }

    [Test]
    public void It_merges_collection_candidates_using_compare_order_and_request_order()
    {
        var fixture = CreateNoProfileFixture();
        var homeCollectionItemId = NewCollectionItemId();
        var physicalCollectionItemId = NewCollectionItemId();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal("Lincoln High")],
                collectionCandidates:
                [
                    CreateNoProfileAddressCandidate(
                        fixture,
                        requestOrder: 0,
                        collectionItemId: homeCollectionItemId,
                        addressType: "Home",
                        city: "Oak Updated"
                    ),
                    CreateNoProfileAddressCandidate(
                        fixture,
                        requestOrder: 1,
                        collectionItemId: physicalCollectionItemId,
                        addressType: "Physical",
                        city: "New"
                    ),
                ]
            )
        );
        var currentState = CreateNoProfileCurrentState(
            fixture,
            rootRows:
            [
                [345L, "Lincoln High"],
            ],
            addressRows:
            [
                [10L, 345L, 0, "Mailing", "Old"],
                [11L, 345L, 1, "Home", "Oak"],
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                ProfileRequest: null,
                ProfileContext: null,
                CompiledScopeCatalog: null
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var addressState = result.TablesInDependencyOrder[2];
        addressState.PreservedRows.Should().BeEmpty();

        // The "Mailing" current row has no matching merged candidate — it becomes a delete
        addressState.Deletes.Should().ContainSingle();

        // ComparableCurrentRowset is in compare (ordinal) order
        addressState.ComparableCurrentRowset.Should().HaveCount(2);
        addressState
            .ComparableCurrentRowset[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(0, "Mailing", "Old");
        addressState
            .ComparableCurrentRowset[1]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(1, "Home", "Oak");

        // ComparableMergedRowset: Home matched (compare order 0) and Physical new (compare order 1)
        addressState.ComparableMergedRowset.Should().HaveCount(2);
        addressState
            .ComparableMergedRowset[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(0, "Home", "Oak Updated");
        addressState
            .ComparableMergedRowset[1]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(1, "Physical", "New");

        // Home matched → update; stable id is 11L from current row
        addressState.Updates.Should().ContainSingle();
        addressState.Updates[0].StableRowIdentityValue.Should().Be(11L);
        LiteralValue(addressState.Updates[0].Values[2]).Should().Be(0);
        LiteralValue(addressState.Updates[0].Values[3]).Should().Be("Home");
        LiteralValue(addressState.Updates[0].Values[4]).Should().Be("Oak Updated");

        // Physical has no current row match → insert; carries new collection item id
        addressState.Inserts.Should().ContainSingle();
        addressState.Inserts[0].Values[0].Should().BeSameAs(physicalCollectionItemId);
        LiteralValue(addressState.Inserts[0].Values[2]).Should().Be(1);
        LiteralValue(addressState.Inserts[0].Values[3]).Should().Be("Physical");
        LiteralValue(addressState.Inserts[0].Values[4]).Should().Be("New");
    }

    [Test]
    public void It_rewrites_nested_parent_scope_keys_from_matched_collection_rows()
    {
        var fixture = CreateNoProfileFixture();
        var addressCollectionItemId = NewCollectionItemId();
        var periodCollectionItemId = NewCollectionItemId();
        var nestedPeriodCandidate = CreateNoProfilePeriodCandidate(
            fixture,
            requestOrder: 0,
            collectionItemId: periodCollectionItemId,
            parentCollectionItemId: addressCollectionItemId,
            beginDate: "2026-09-01",
            room: "Updated Room"
        );
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal("Lincoln High")],
                collectionCandidates:
                [
                    CreateNoProfileAddressCandidate(
                        fixture,
                        requestOrder: 0,
                        collectionItemId: addressCollectionItemId,
                        addressType: "Home",
                        city: "Oak Updated",
                        periods: [nestedPeriodCandidate]
                    ),
                ]
            )
        );
        var currentState = CreateNoProfileCurrentState(
            fixture,
            rootRows:
            [
                [345L, "Lincoln High"],
            ],
            addressRows:
            [
                [11L, 345L, 0, "Home", "Oak"],
            ],
            periodRows:
            [
                [101L, 345L, 11L, 0, "2026-09-01", "Morning"],
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                ProfileRequest: null,
                ProfileContext: null,
                CompiledScopeCatalog: null
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var addressState = result.TablesInDependencyOrder[2];
        var periodState = result.TablesInDependencyOrder[3];

        addressState.PreservedRows.Should().BeEmpty();
        periodState.PreservedRows.Should().BeEmpty();

        // Address matched → update, stable id 11L
        addressState.ComparableMergedRowset.Should().ContainSingle();
        LiteralValue(addressState.Updates[0].Values[0]).Should().Be(11L);

        // Period matched → update; parent scope key rewritten to matched address stable id (11L)
        periodState.ComparableCurrentRowset.Should().ContainSingle();
        periodState.ComparableMergedRowset.Should().ContainSingle();
        LiteralValue(periodState.Updates[0].Values[0]).Should().Be(101L);
        LiteralValue(periodState.Updates[0].Values[2]).Should().Be(11L);
        LiteralValue(periodState.Updates[0].Values[4]).Should().Be("2026-09-01");
        LiteralValue(periodState.Updates[0].Values[5]).Should().Be("Updated Room");
        periodState
            .ComparableMergedRowset[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(0, "2026-09-01", "Updated Room");
    }

    [Test]
    public void It_normalizes_sql_server_date_and_time_root_values_into_the_shared_compare_space()
    {
        var fixture = CreateNoProfileDateAndTimeFixture();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal(new DateOnly(2026, 8, 20)), Literal(new TimeOnly(14, 5, 7))]
            )
        );
        var currentState = CreateNoProfileDateAndTimeCurrentState(
            fixture,
            rootRows:
            [
                [345L, new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(14, 5, 7)],
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                ProfileRequest: null,
                ProfileContext: null,
                CompiledScopeCatalog: null
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var rootState = result.TablesInDependencyOrder[0];
        rootState.PreservedRows.Should().BeEmpty();

        // Current rows: DateTime/TimeSpan normalized to DateOnly/TimeOnly
        LiteralValue(rootState.ComparableCurrentRowset[0].Values[1]).Should().Be(new DateOnly(2026, 8, 20));
        LiteralValue(rootState.ComparableCurrentRowset[0].Values[2]).Should().Be(new TimeOnly(14, 5, 7));
        rootState
            .ComparableCurrentRowset[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(345L, new DateOnly(2026, 8, 20), new TimeOnly(14, 5, 7));
        rootState
            .ComparableMergedRowset[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(345L, new DateOnly(2026, 8, 20), new TimeOnly(14, 5, 7));

        // Comparable rowsets match → no-op candidate
        RelationalWriteGuardedNoOp.IsNoOpCandidate(result).Should().BeTrue();
    }

    [Test]
    public void It_uses_normalized_sql_server_date_and_time_values_for_collection_semantic_identity_matching()
    {
        var fixture = CreateNoProfileDateAndTimeFixture();
        var scheduleCollectionItemId = NewCollectionItemId();
        var flattenedWriteSet = new FlattenedWriteSet(
            new RootWriteRowBuffer(
                fixture.RootPlan,
                [Literal(345L), Literal(new DateOnly(2026, 8, 20)), Literal(new TimeOnly(14, 5, 7))],
                collectionCandidates:
                [
                    CreateNoProfileScheduleCandidate(
                        fixture,
                        requestOrder: 0,
                        collectionItemId: scheduleCollectionItemId,
                        sessionDate: new DateOnly(2026, 9, 1),
                        startTime: new TimeOnly(8, 15),
                        room: "Updated Room"
                    ),
                ]
            )
        );
        var currentState = CreateNoProfileDateAndTimeCurrentState(
            fixture,
            rootRows:
            [
                [345L, new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(14, 5, 7)],
            ],
            scheduleRows:
            [
                [
                    77L,
                    345L,
                    0,
                    new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Unspecified),
                    new TimeSpan(8, 15, 0),
                    "Morning Room",
                ],
            ]
        );

        var outcome = _sut.Synthesize(
            new RelationalWriteMergeRequest(
                fixture.WritePlan,
                flattenedWriteSet,
                currentState,
                ProfileRequest: null,
                ProfileContext: null,
                CompiledScopeCatalog: null
            )
        );

        outcome.Should().BeOfType<RelationalWriteMergeSynthesisOutcome.Success>();
        var result = ((RelationalWriteMergeSynthesisOutcome.Success)outcome).MergeResult;

        var scheduleState = result.TablesInDependencyOrder[1];
        scheduleState.PreservedRows.Should().BeEmpty();

        // Current row: DateTime/TimeSpan stored values normalized to DateOnly/TimeOnly
        LiteralValue(scheduleState.ComparableCurrentRowset[0].Values[3])
            .Should()
            .Be(new DateOnly(2026, 9, 1));
        LiteralValue(scheduleState.ComparableCurrentRowset[0].Values[4]).Should().Be(new TimeOnly(8, 15));

        // Match found by semantic identity → update; stable id 77L
        scheduleState.Updates.Should().ContainSingle();
        LiteralValue(scheduleState.Updates[0].Values[0]).Should().Be(77L);

        scheduleState
            .ComparableCurrentRowset[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(0, new DateOnly(2026, 9, 1), new TimeOnly(8, 15), "Morning Room");
        scheduleState
            .ComparableMergedRowset[0]
            .ComparableValues.Select(LiteralValue)
            .Should()
            .Equal(0, new DateOnly(2026, 9, 1), new TimeOnly(8, 15), "Updated Room");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fixture helpers (ported from Given_Relational_Write_No_Profile_Merge_Synthesizer)
    // Self-contained here to avoid coupling to the legacy test class that Task 6
    // will delete along with RelationalWriteNoProfileMergeSynthesizer itself.
    // ─────────────────────────────────────────────────────────────────────────

    private static NoProfileWritePlanFixture CreateNoProfileFixture()
    {
        var rootPlan = CreateNoProfileRootPlan();
        var rootExtensionPlan = CreateNoProfileRootExtensionPlan();
        var addressPlan = CreateNoProfileAddressPlan();
        var periodPlan = CreateNoProfilePeriodPlan();
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder:
            [
                rootPlan.TableModel,
                rootExtensionPlan.TableModel,
                addressPlan.TableModel,
                periodPlan.TableModel,
            ],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new NoProfileWritePlanFixture(
            new ResourceWritePlan(resourceModel, [rootPlan, rootExtensionPlan, addressPlan, periodPlan]),
            rootPlan,
            rootExtensionPlan,
            addressPlan,
            periodPlan
        );
    }

    private static NoProfileDateAndTimeWritePlanFixture CreateNoProfileDateAndTimeFixture()
    {
        var rootPlan = CreateNoProfileDateAndTimeRootPlan();
        var schedulePlan = CreateNoProfileSchedulePlan();
        var resourceModel = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "School"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootPlan.TableModel,
            TablesInDependencyOrder: [rootPlan.TableModel, schedulePlan.TableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        return new NoProfileDateAndTimeWritePlanFixture(
            new ResourceWritePlan(resourceModel, [rootPlan, schedulePlan]),
            rootPlan,
            schedulePlan
        );
    }

    private static RelationalWriteCurrentState CreateNoProfileCurrentState(
        NoProfileWritePlanFixture fixture,
        IReadOnlyList<object?[]>? rootRows = null,
        IReadOnlyList<object?[]>? rootExtensionRows = null,
        IReadOnlyList<object?[]>? addressRows = null,
        IReadOnlyList<object?[]>? periodRows = null
    )
    {
        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(fixture.RootPlan.TableModel, rootRows ?? []),
                new HydratedTableRows(fixture.RootExtensionPlan.TableModel, rootExtensionRows ?? []),
                new HydratedTableRows(fixture.AddressPlan.TableModel, addressRows ?? []),
                new HydratedTableRows(fixture.PeriodPlan.TableModel, periodRows ?? []),
            ]
        );
    }

    private static RelationalWriteCurrentState CreateNoProfileDateAndTimeCurrentState(
        NoProfileDateAndTimeWritePlanFixture fixture,
        IReadOnlyList<object?[]>? rootRows = null,
        IReadOnlyList<object?[]>? scheduleRows = null
    )
    {
        return new RelationalWriteCurrentState(
            new DocumentMetadataRow(
                345L,
                Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"),
                44L,
                44L,
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 4, 2, 12, 0, 0, TimeSpan.Zero)
            ),
            [
                new HydratedTableRows(fixture.RootPlan.TableModel, rootRows ?? []),
                new HydratedTableRows(fixture.SchedulePlan.TableModel, scheduleRows ?? []),
            ]
        );
    }

    private static FlattenedWriteValue.UnresolvedCollectionItemId NewCollectionItemId() =>
        FlattenedWriteValue.UnresolvedCollectionItemId.Create();

    private static CollectionWriteCandidate CreateNoProfileAddressCandidate(
        NoProfileWritePlanFixture fixture,
        int requestOrder,
        FlattenedWriteValue collectionItemId,
        string addressType,
        string city,
        IReadOnlyList<CollectionWriteCandidate>? periods = null
    )
    {
        return new CollectionWriteCandidate(
            fixture.AddressPlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values:
            [
                collectionItemId,
                Literal(345L),
                Literal(requestOrder),
                Literal(addressType),
                Literal(city),
            ],
            semanticIdentityValues: [addressType],
            collectionCandidates: periods ?? []
        );
    }

    private static CollectionWriteCandidate CreateNoProfilePeriodCandidate(
        NoProfileWritePlanFixture fixture,
        int requestOrder,
        FlattenedWriteValue collectionItemId,
        FlattenedWriteValue parentCollectionItemId,
        string beginDate,
        string room
    )
    {
        return new CollectionWriteCandidate(
            fixture.PeriodPlan,
            ordinalPath: [0, requestOrder],
            requestOrder: requestOrder,
            values:
            [
                collectionItemId,
                Literal(345L),
                parentCollectionItemId,
                Literal(requestOrder),
                Literal(beginDate),
                Literal(room),
            ],
            semanticIdentityValues: [beginDate]
        );
    }

    private static CollectionWriteCandidate CreateNoProfileScheduleCandidate(
        NoProfileDateAndTimeWritePlanFixture fixture,
        int requestOrder,
        FlattenedWriteValue collectionItemId,
        DateOnly sessionDate,
        TimeOnly startTime,
        string room
    )
    {
        return new CollectionWriteCandidate(
            fixture.SchedulePlan,
            ordinalPath: [requestOrder],
            requestOrder: requestOrder,
            values:
            [
                collectionItemId,
                Literal(345L),
                Literal(requestOrder),
                Literal(sessionDate),
                Literal(startTime),
                Literal(room),
            ],
            semanticIdentityValues: [sessionDate, startTime]
        );
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Table plan builders (mirrors the legacy no-profile test file exactly)
    // ─────────────────────────────────────────────────────────────────────────

    private static TableWritePlan CreateNoProfileRootPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"Name\" = @Name where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", [new JsonPathSegment.Property("name")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateNoProfileDateAndTimeRootPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SessionDate"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    false,
                    new JsonPathExpression("$.sessionDate", [new JsonPathSegment.Property("sessionDate")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("StartTime"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Time),
                    false,
                    new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @SessionDate, @StartTime)",
            UpdateSql: "update edfi.\"School\" set \"SessionDate\" = @SessionDate, \"StartTime\" = @StartTime where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 3, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.sessionDate",
                            [new JsonPathSegment.Property("sessionDate")]
                        ),
                        new RelationalScalarType(ScalarKind.Date)
                    ),
                    "SessionDate"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                        new RelationalScalarType(ScalarKind.Time)
                    ),
                    "StartTime"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateNoProfileRootExtensionPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("sample"), "SchoolExtension"),
            new JsonPathExpression(
                "$._ext.sample",
                [new JsonPathSegment.Property("_ext"), new JsonPathSegment.Property("sample")]
            ),
            new TableKey(
                "PK_SchoolExtension",
                [new DbKeyColumn(new DbColumnName("School_DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("ExtensionCode"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression(
                        "$._ext.sample.extensionCode",
                        [
                            new JsonPathSegment.Property("_ext"),
                            new JsonPathSegment.Property("sample"),
                            new JsonPathSegment.Property("extensionCode"),
                        ]
                    ),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.RootExtension,
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into sample.\"SchoolExtension\" values (@School_DocumentId, @ExtensionCode)",
            UpdateSql: "update sample.\"SchoolExtension\" set \"ExtensionCode\" = @ExtensionCode where \"School_DocumentId\" = @School_DocumentId",
            DeleteByParentSql: "delete from sample.\"SchoolExtension\" where \"School_DocumentId\" = @School_DocumentId",
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.extensionCode",
                            [new JsonPathSegment.Property("extensionCode")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "ExtensionCode"
                ),
            ],
            KeyUnificationPlans: []
        );
    }

    private static TableWritePlan CreateNoProfileAddressPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolAddress"),
            new JsonPathExpression(
                "$.addresses[*]",
                [new JsonPathSegment.Property("addresses"), new JsonPathSegment.AnyArrayElement()]
            ),
            new TableKey(
                "PK_SchoolAddress",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("AddressType"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    false,
                    new JsonPathExpression("$.addressType", [new JsonPathSegment.Property("addressType")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("City"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.city", [new JsonPathSegment.Property("city")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.addressType",
                            [new JsonPathSegment.Property("addressType")]
                        ),
                        new DbColumnName("AddressType")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolAddress\" values (@CollectionItemId, @School_DocumentId, @Ordinal, @AddressType, @City)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 5, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DocumentId(),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.addressType",
                            [new JsonPathSegment.Property("addressType")]
                        ),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 50)
                    ),
                    "AddressType"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.city", [new JsonPathSegment.Property("city")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "City"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.addressType",
                            [new JsonPathSegment.Property("addressType")]
                        ),
                        BindingIndex: 3
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolAddress\" set \"Ordinal\" = @Ordinal, \"AddressType\" = @AddressType, \"City\" = @City where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolAddress\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2, 3, 4]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateNoProfilePeriodPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolAddressPeriod"),
            new JsonPathExpression(
                "$.addresses[*].periods[*]",
                [
                    new JsonPathSegment.Property("addresses"),
                    new JsonPathSegment.AnyArrayElement(),
                    new JsonPathSegment.Property("periods"),
                    new JsonPathSegment.AnyArrayElement(),
                ]
            ),
            new TableKey(
                "PK_SchoolAddressPeriod",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("ParentCollectionItemId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("BeginDate"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 20),
                    false,
                    new JsonPathExpression("$.beginDate", [new JsonPathSegment.Property("beginDate")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Room"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 50),
                    false,
                    new JsonPathExpression("$.room", [new JsonPathSegment.Property("room")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("ParentCollectionItemId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.beginDate", [new JsonPathSegment.Property("beginDate")]),
                        new DbColumnName("BeginDate")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolAddressPeriod\" values (@CollectionItemId, @School_DocumentId, @ParentCollectionItemId, @Ordinal, @BeginDate, @Room)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 6, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DocumentId(),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[2],
                    new WriteValueSource.ParentKeyPart(0),
                    "ParentCollectionItemId"
                ),
                new WriteColumnBinding(tableModel.Columns[3], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.beginDate", [new JsonPathSegment.Property("beginDate")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 20)
                    ),
                    "BeginDate"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[5],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.room", [new JsonPathSegment.Property("room")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 50)
                    ),
                    "Room"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression("$.beginDate", [new JsonPathSegment.Property("beginDate")]),
                        BindingIndex: 4
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolAddressPeriod\" set \"Ordinal\" = @Ordinal, \"BeginDate\" = @BeginDate, \"Room\" = @Room where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolAddressPeriod\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 3,
                CompareBindingIndexesInOrder: [3, 4, 5]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static TableWritePlan CreateNoProfileSchedulePlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "SchoolSchedule"),
            new JsonPathExpression(
                "$.schedules[*]",
                [new JsonPathSegment.Property("schedules"), new JsonPathSegment.AnyArrayElement()]
            ),
            new TableKey(
                "PK_SchoolSchedule",
                [new DbKeyColumn(new DbColumnName("CollectionItemId"), ColumnKind.CollectionKey)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("CollectionItemId"),
                    ColumnKind.CollectionKey,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("School_DocumentId"),
                    ColumnKind.ParentKeyPart,
                    new RelationalScalarType(ScalarKind.Int64),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Ordinal"),
                    ColumnKind.Ordinal,
                    new RelationalScalarType(ScalarKind.Int32),
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("SessionDate"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Date),
                    false,
                    new JsonPathExpression("$.sessionDate", [new JsonPathSegment.Property("sessionDate")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("StartTime"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.Time),
                    false,
                    new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Room"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.room", [new JsonPathSegment.Property("room")]),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Collection,
                [new DbColumnName("CollectionItemId")],
                [new DbColumnName("School_DocumentId")],
                [new DbColumnName("School_DocumentId")],
                [
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.sessionDate",
                            [new JsonPathSegment.Property("sessionDate")]
                        ),
                        new DbColumnName("SessionDate")
                    ),
                    new CollectionSemanticIdentityBinding(
                        new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                        new DbColumnName("StartTime")
                    ),
                ]
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"SchoolSchedule\" values (@CollectionItemId, @School_DocumentId, @Ordinal, @SessionDate, @StartTime, @Room)",
            UpdateSql: null,
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 6, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.Precomputed(),
                    "CollectionItemId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.DocumentId(),
                    "School_DocumentId"
                ),
                new WriteColumnBinding(tableModel.Columns[2], new WriteValueSource.Ordinal(), "Ordinal"),
                new WriteColumnBinding(
                    tableModel.Columns[3],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression(
                            "$.sessionDate",
                            [new JsonPathSegment.Property("sessionDate")]
                        ),
                        new RelationalScalarType(ScalarKind.Date)
                    ),
                    "SessionDate"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[4],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                        new RelationalScalarType(ScalarKind.Time)
                    ),
                    "StartTime"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[5],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.room", [new JsonPathSegment.Property("room")]),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Room"
                ),
            ],
            KeyUnificationPlans: [],
            CollectionMergePlan: new CollectionMergePlan(
                SemanticIdentityBindings:
                [
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression(
                            "$.sessionDate",
                            [new JsonPathSegment.Property("sessionDate")]
                        ),
                        BindingIndex: 3
                    ),
                    new CollectionMergeSemanticIdentityBinding(
                        new JsonPathExpression("$.startTime", [new JsonPathSegment.Property("startTime")]),
                        BindingIndex: 4
                    ),
                ],
                StableRowIdentityBindingIndex: 0,
                UpdateByStableRowIdentitySql: "update edfi.\"SchoolSchedule\" set \"Ordinal\" = @Ordinal, \"SessionDate\" = @SessionDate, \"StartTime\" = @StartTime, \"Room\" = @Room where \"CollectionItemId\" = @CollectionItemId",
                DeleteByStableRowIdentitySql: "delete from edfi.\"SchoolSchedule\" where \"CollectionItemId\" = @CollectionItemId",
                OrdinalBindingIndex: 2,
                CompareBindingIndexesInOrder: [2, 3, 4, 5]
            ),
            CollectionKeyPreallocationPlan: new CollectionKeyPreallocationPlan(
                new DbColumnName("CollectionItemId"),
                0
            )
        );
    }

    private static FlattenedWriteValue Literal(object? value) => new FlattenedWriteValue.Literal(value);

    private static object? LiteralValue(FlattenedWriteValue value) =>
        value is FlattenedWriteValue.Literal literalValue
            ? literalValue.Value
            : throw new AssertionException($"Expected a literal value but found '{value.GetType().Name}'.");

    private sealed record NoProfileWritePlanFixture(
        ResourceWritePlan WritePlan,
        TableWritePlan RootPlan,
        TableWritePlan RootExtensionPlan,
        TableWritePlan AddressPlan,
        TableWritePlan PeriodPlan
    );

    private sealed record NoProfileDateAndTimeWritePlanFixture(
        ResourceWritePlan WritePlan,
        TableWritePlan RootPlan,
        TableWritePlan SchedulePlan
    );
}
