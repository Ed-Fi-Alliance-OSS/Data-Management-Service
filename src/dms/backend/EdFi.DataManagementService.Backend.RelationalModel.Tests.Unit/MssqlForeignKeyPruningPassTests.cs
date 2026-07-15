// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Test-only pass that captures the pruning candidates the production pass collects, so candidate
/// collection is observable without serializing pass-local state into the relational model.
/// </summary>
internal sealed class PruningCandidateProbePass : IRelationalModelSetPass
{
    public IReadOnlyList<MssqlForeignKeyPruningPass.PruningCandidate> Candidates { get; private set; } = [];

    public void Execute(RelationalModelSetBuilderContext context)
    {
        Candidates = MssqlForeignKeyPruningPass.CollectCandidatesInStableOrder(context);
    }
}

/// <summary>
/// Test fixture for SQL Server pruning candidate collection over concrete reference targets.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_Candidate_Collection
{
    private IReadOnlyList<MssqlForeignKeyPruningPass.PruningCandidate> _candidates = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var probe = new PruningCandidateProbePass();
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new TransitiveIdentityMutabilityPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            probe,
        ]);

        builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());

        _candidates = probe.Candidates;
    }

    /// <summary>
    /// It should collect only document-reference FKs with mutable targets: the School reference
    /// (allowIdentityUpdates) is a candidate; the Student reference (immutable) is not.
    /// </summary>
    [Test]
    public void It_should_collect_only_mutable_document_reference_candidates()
    {
        _candidates.Should().HaveCount(1);

        var candidate = _candidates.Single();
        candidate.ReceiverTable.Name.Should().Be("Enrollment");
        candidate.TargetTable.Name.Should().Be("School");
    }

    /// <summary>
    /// It should capture the full-composite column pairs, the local DocumentId column, its
    /// requiredness, and the delete action needed by the structural safe-cut test.
    /// </summary>
    [Test]
    public void It_should_capture_full_composite_columns_and_document_id_requiredness()
    {
        var candidate = _candidates.Single(entry => entry.TargetTable.Name == "School");

        candidate
            .LocalColumns.Select(column => column.Value)
            .Should()
            .Equal("School_EducationOrganizationId", "School_SchoolId", "School_DocumentId");
        candidate
            .TargetColumns.Select(column => column.Value)
            .Should()
            .Equal("EducationOrganizationId", "SchoolId", "DocumentId");
        candidate.LocalDocumentIdColumn.Value.Should().Be("School_DocumentId");
        candidate.LocalDocumentIdIsRequired.Should().BeTrue();
        candidate.OnDelete.Should().Be(ReferentialAction.NoAction);
    }
}

/// <summary>
/// Test fixture for SQL Server pruning candidate collection over abstract identity targets.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_Candidate_Collection_With_Abstract_Target
{
    private IReadOnlyList<MssqlForeignKeyPruningPass.PruningCandidate> _candidates = default!;

    /// <summary>
    /// Sets up the test fixture.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = ConstraintDerivationTestSchemaBuilder.BuildAbstractReferenceProjectSchema();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var probe = new PruningCandidateProbePass();
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new AbstractIdentityTableAndUnionViewDerivationPass(),
            new ReferenceBindingPass(),
            new RootIdentityConstraintPass(),
            new ReferenceConstraintPass(),
            probe,
        ]);

        builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());

        _candidates = probe.Candidates;
    }

    /// <summary>
    /// It should collect the abstract-target reference as a candidate with its full-composite
    /// columns, without requiring transitive mutability derivation.
    /// </summary>
    [Test]
    public void It_should_collect_abstract_target_reference_candidates()
    {
        var candidate = _candidates.Single(entry =>
            entry.TargetTable.Name == "EducationOrganizationIdentity"
        );

        candidate.ReceiverTable.Name.Should().Be("Enrollment");
        candidate
            .LocalColumns.Select(column => column.Value)
            .Should()
            .Equal("EducationOrganization_EducationOrganizationId", "EducationOrganization_DocumentId");
        candidate
            .TargetColumns.Select(column => column.Value)
            .Should()
            .Equal("EducationOrganizationId", "DocumentId");
    }
}

/// <summary>
/// Test fixture for stable pruning edge key identity and ordering.
/// </summary>
[TestFixture]
public class Given_Pruning_Edge_Key_Ordering
{
    private static readonly DbTableName _receiverA = new(new DbSchemaName("edfi"), "AReceiver");
    private static readonly DbTableName _receiverB = new(new DbSchemaName("edfi"), "BReceiver");
    private static readonly DbTableName _targetA = new(new DbSchemaName("edfi"), "ATarget");
    private static readonly DbTableName _targetB = new(new DbSchemaName("edfi"), "BTarget");

    private static TableConstraint.ForeignKey BuildForeignKey(
        string name,
        string[] columns,
        DbTableName targetTable,
        string[] targetColumns,
        ReferentialAction onDelete = ReferentialAction.NoAction,
        ReferentialAction onUpdate = ReferentialAction.Cascade
    )
    {
        return new TableConstraint.ForeignKey(
            name,
            columns.Select(column => new DbColumnName(column)).ToArray(),
            targetTable,
            targetColumns.Select(column => new DbColumnName(column)).ToArray(),
            onDelete,
            onUpdate
        );
    }

    /// <summary>
    /// It should exclude the selected ON UPDATE action and the rendered constraint name from
    /// candidate identity: two FKs differing only in those produce the same edge key.
    /// </summary>
    [Test]
    public void It_should_be_independent_of_update_action_and_constraint_name()
    {
        var cascade = BuildForeignKey(
            "FK_AReceiver_ATarget",
            ["ATarget_Key", "ATarget_DocumentId"],
            _targetA,
            ["Key", "DocumentId"],
            onUpdate: ReferentialAction.Cascade
        );
        var renamedNoAction = BuildForeignKey(
            "FK_AReceiver_ATarget_Renamed",
            ["ATarget_Key", "ATarget_DocumentId"],
            _targetA,
            ["Key", "DocumentId"],
            onUpdate: ReferentialAction.NoAction
        );

        var first = MssqlForeignKeyPruningPass.PruningEdgeKey.From(_receiverA, cascade);
        var second = MssqlForeignKeyPruningPass.PruningEdgeKey.From(_receiverA, renamedNoAction);

        first.Should().Be(second);
        first.CompareTo(second).Should().Be(0);
    }

    /// <summary>
    /// It should order deterministically by receiver table, local columns, target table, target
    /// columns, then delete action, regardless of input order.
    /// </summary>
    [Test]
    public void It_should_order_by_receiver_local_columns_target_and_delete_action()
    {
        var byReceiver = MssqlForeignKeyPruningPass.PruningEdgeKey.From(
            _receiverA,
            BuildForeignKey("FK_1", ["ATarget_Key", "ATarget_DocumentId"], _targetA, ["Key", "DocumentId"])
        );
        var byLocalColumns = MssqlForeignKeyPruningPass.PruningEdgeKey.From(
            _receiverB,
            BuildForeignKey("FK_2", ["ATarget_Key2", "ATarget_DocumentId"], _targetA, ["Key", "DocumentId"])
        );
        var byTargetTable = MssqlForeignKeyPruningPass.PruningEdgeKey.From(
            _receiverB,
            BuildForeignKey("FK_3", ["ATarget_Key2", "ATarget_DocumentId"], _targetB, ["Key", "DocumentId"])
        );
        var byTargetColumns = MssqlForeignKeyPruningPass.PruningEdgeKey.From(
            _receiverB,
            BuildForeignKey("FK_4", ["ATarget_Key2", "ATarget_DocumentId"], _targetB, ["Key2", "DocumentId"])
        );
        var byDeleteAction = MssqlForeignKeyPruningPass.PruningEdgeKey.From(
            _receiverB,
            BuildForeignKey(
                "FK_5",
                ["ATarget_Key2", "ATarget_DocumentId"],
                _targetB,
                ["Key2", "DocumentId"],
                onDelete: ReferentialAction.Cascade
            )
        );

        var expectedOrder = new[]
        {
            byReceiver,
            byLocalColumns,
            byTargetTable,
            byTargetColumns,
            byDeleteAction,
        };

        var shuffled = expectedOrder.Reverse().ToList();
        shuffled.Sort();

        shuffled.Should().Equal(expectedOrder);
    }
}

/// <summary>
/// Shared helpers for pruning topology fixtures: builds an MSSQL model set through the FK-producing
/// passes plus the pruning pass, and locates reference FKs by their DocumentId column.
/// </summary>
internal static class PruningTopologyTestHelpers
{
    public static DerivedRelationalModelSet BuildMssqlModelSet(
        System.Text.Json.Nodes.JsonObject projectSchema
    )
    {
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new RootIdentityConstraintPass(),
            new TransitiveIdentityMutabilityPass(),
            new ReferenceConstraintPass(),
            new MssqlForeignKeyPruningPass(),
        ]);

        return builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());
    }

    public static TableConstraint.ForeignKey FindReferenceForeignKey(
        DerivedRelationalModelSet result,
        string resourceName,
        string documentIdColumn
    )
    {
        var rootTable = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == resourceName
            )
            .RelationalModel.Root;

        return rootTable
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns.Any(column => column.Value == documentIdColumn));
    }

    public static DerivedRelationalModelSet BuildPgsqlModelSet(
        System.Text.Json.Nodes.JsonObject projectSchema
    )
    {
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new RootIdentityConstraintPass(),
            new TransitiveIdentityMutabilityPass(),
            new ReferenceConstraintPass(),
            new MssqlForeignKeyPruningPass(),
        ]);

        return builder.Build(schemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
    }

    public static TableConstraint.ForeignKey FindForeignKeyOnTable(
        DerivedRelationalModelSet result,
        string resourceName,
        DbTableName table,
        string documentIdColumn
    )
    {
        var tableModel = result
            .ConcreteResourcesInNameOrder.Single(model =>
                model.ResourceKey.Resource.ResourceName == resourceName
            )
            .RelationalModel.TablesInDependencyOrder.Single(candidate => candidate.Table.Equals(table));

        return tableModel
            .Constraints.OfType<TableConstraint.ForeignKey>()
            .Single(constraint => constraint.Columns.Any(column => column.Value == documentIdColumn));
    }
}

/// <summary>
/// Design verification case 1: a chain with no convergence keeps every candidate cascade.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_Of_A_Chain_Without_Convergence
{
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture: Origin -> Middle (part of identity) -> Leaf.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "Origin",
                AllowIdentityUpdates: true,
                IdentityScalars: ["originKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Middle",
                IdentityScalars: ["middleKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Origin", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Leaf",
                IdentityScalars: ["leafKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Middle")]
            )
        );

        _result = PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should keep every chain edge cascading: a single path per origin has no duplicate to prune.
    /// </summary>
    [Test]
    public void It_should_keep_all_chain_edges_cascading()
    {
        var originFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Middle",
            "Origin_DocumentId"
        );
        var middleFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Leaf",
            "Middle_DocumentId"
        );

        originFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        middleFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
    }
}

/// <summary>
/// Design verification case 2: independent parents into one receiver are not a diamond; raw
/// receiver in-degree is not a pruning test.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_Of_Independent_Parents
{
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture: mutable ParentA -> Receiver and mutable ParentB -> Receiver,
    /// with no origin reaching both parents.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "ParentA",
                AllowIdentityUpdates: true,
                IdentityScalars: ["parentAKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "ParentB",
                AllowIdentityUpdates: true,
                IdentityScalars: ["parentBKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("ParentA"),
                    new PruningTestSchemaBuilder.ReferenceSpec("ParentB"),
                ]
            )
        );

        _result = PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should keep both independent parent references cascading.
    /// </summary>
    [Test]
    public void It_should_keep_both_independent_parent_references_cascading()
    {
        var parentAFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "ParentA_DocumentId"
        );
        var parentBFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "ParentB_DocumentId"
        );

        parentAFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        parentBFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
    }
}

/// <summary>
/// Design verification case 3: a covered direct-versus-indirect diamond is pruned at its
/// convergence with one safe full-composite NO ACTION cut.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_Of_A_Covered_Diamond
{
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture: Origin -> Receiver directly and Origin -> Bridge -> Receiver,
    /// with the origin key unified across both receiver references.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "Origin",
                AllowIdentityUpdates: true,
                IdentityScalars: ["originKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Bridge",
                IdentityScalars: ["bridgeKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Origin", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("Origin"),
                    new PruningTestSchemaBuilder.ReferenceSpec("Bridge"),
                ],
                EqualityConstraints: [("$.originReference.originKey", "$.bridgeReference.originKey")]
            )
        );

        _result = PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should retain the first stable survivor (the Bridge edge) and cut the direct Origin
    /// edge to full-composite NO ACTION, keeping the upstream Origin -> Bridge edge cascading.
    /// </summary>
    [Test]
    public void It_should_cut_exactly_one_incoming_edge_at_the_convergence()
    {
        var originFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "Origin_DocumentId"
        );
        var bridgeFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "Bridge_DocumentId"
        );
        var upstreamFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Bridge",
            "Origin_DocumentId"
        );

        bridgeFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        originFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
        upstreamFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
    }

    /// <summary>
    /// It should keep the cut full composite: identity storage columns first, DocumentId last.
    /// </summary>
    [Test]
    public void It_should_keep_the_cut_foreign_key_full_composite()
    {
        var originFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "Origin_DocumentId"
        );

        originFk.Columns.Should().HaveCount(2);
        originFk.Columns[^1].Value.Should().Be("Origin_DocumentId");
        originFk.TargetColumns[^1].Value.Should().Be("DocumentId");
    }
}

/// <summary>
/// Design verification case 4: a convergence with three distinct incoming candidate edges from
/// one origin retains one survivor and cuts the other two.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_Of_A_Three_Way_Convergence
{
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture: Origin -> Receiver directly plus Origin -> BridgeOne -> Receiver
    /// and Origin -> BridgeTwo -> Receiver, with the origin key unified across all three.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "Origin",
                AllowIdentityUpdates: true,
                IdentityScalars: ["originKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeOne",
                IdentityScalars: ["bridgeOneKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Origin", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeTwo",
                IdentityScalars: ["bridgeTwoKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Origin", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("Origin"),
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeOne"),
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeTwo"),
                ],
                EqualityConstraints:
                [
                    ("$.originReference.originKey", "$.bridgeOneReference.originKey"),
                    ("$.originReference.originKey", "$.bridgeTwoReference.originKey"),
                ]
            )
        );

        _result = PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should retain exactly one survivor (the first in stable order) and cut the other two
    /// incoming edges, keeping both upstream bridge edges cascading.
    /// </summary>
    [Test]
    public void It_should_retain_one_survivor_and_cut_the_other_two_incoming_edges()
    {
        var originFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "Origin_DocumentId"
        );
        var bridgeOneFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "BridgeOne_DocumentId"
        );
        var bridgeTwoFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "BridgeTwo_DocumentId"
        );

        bridgeOneFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        bridgeTwoFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
        originFk.OnUpdate.Should().Be(ReferentialAction.NoAction);

        PruningTopologyTestHelpers
            .FindReferenceForeignKey(_result, "BridgeOne", "Origin_DocumentId")
            .OnUpdate.Should()
            .Be(ReferentialAction.Cascade);
        PruningTopologyTestHelpers
            .FindReferenceForeignKey(_result, "BridgeTwo", "Origin_DocumentId")
            .OnUpdate.Should()
            .Be(ReferentialAction.Cascade);
    }
}

/// <summary>
/// Design verification case 5: when the first stable survivor is unsafe (an optional carrier),
/// the second stable survivor is retained instead.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_An_Unsafe_First_Survivor
{
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture: the Bridge reference on Receiver is optional and sorts first in
    /// stable order; retaining it would leave shared columns without required native coverage, so
    /// the required Origin reference must survive instead.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "Origin",
                AllowIdentityUpdates: true,
                IdentityScalars: ["originKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Bridge",
                IdentityScalars: ["bridgeKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Origin", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("Origin"),
                    new PruningTestSchemaBuilder.ReferenceSpec("Bridge", Required: false),
                ],
                EqualityConstraints: [("$.originReference.originKey", "$.bridgeReference.originKey")]
            )
        );

        _result = PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should skip the unsafe optional first survivor and retain the required Origin edge,
    /// cutting the optional Bridge edge.
    /// </summary>
    [Test]
    public void It_should_retain_the_second_stable_survivor_when_the_first_is_unsafe()
    {
        var originFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "Origin_DocumentId"
        );
        var bridgeFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "Bridge_DocumentId"
        );

        originFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        bridgeFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }
}

/// <summary>
/// Design verification case 6: paths arriving through the same incoming edge converge upstream;
/// that receiver supplies no local pruning choice.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_An_Upstream_Convergence
{
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture: Origin -> BridgeOne/BridgeTwo -> Junction -> Receiver. The two
    /// paths converge at Junction; Receiver is reached through the single Junction edge only.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "Origin",
                AllowIdentityUpdates: true,
                IdentityScalars: ["originKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeOne",
                IdentityScalars: ["bridgeOneKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Origin", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeTwo",
                IdentityScalars: ["bridgeTwoKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Origin", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Junction",
                IdentityScalars: ["junctionKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeOne", PartOfIdentity: true),
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeTwo", PartOfIdentity: true),
                ],
                EqualityConstraints: [("$.bridgeOneReference.originKey", "$.bridgeTwoReference.originKey")]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Junction")]
            )
        );

        _result = PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should prune at the upstream Junction convergence and leave the single downstream
    /// Junction -> Receiver edge cascading.
    /// </summary>
    [Test]
    public void It_should_prune_at_the_upstream_convergence_only()
    {
        var bridgeOneFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Junction",
            "BridgeOne_DocumentId"
        );
        var bridgeTwoFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Junction",
            "BridgeTwo_DocumentId"
        );
        var junctionFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "Junction_DocumentId"
        );

        bridgeOneFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        bridgeTwoFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
        junctionFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
    }
}

/// <summary>
/// Design verification case 7: an interacting cross-origin overlapping-diamond case in which a
/// later convergence has no locally safe survivor and retries the earlier decision that selected
/// an action for the exact same physical FK.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_A_Cross_Origin_Shared_Fk_Retry
{
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture. OriginAlpha converges at Receiver through the BridgeOne and
    /// BridgeTwo edges; OriginBeta converges at the same Receiver through the BridgeOne and
    /// BridgeThree edges. An equality constraint ties BridgeOne's own shadow key to BridgeThree's
    /// beta key, so for OriginBeta the shared column is static on the BridgeOne side but
    /// origin-affected on the BridgeThree side: neither of OriginBeta's survivor choices is
    /// locally safe. OriginAlpha's earlier decision (retain BridgeOne, cut BridgeTwo) assigned
    /// actions to the same physical FKs, so it is retried with its next stable survivor, cutting
    /// the BridgeOne edge instead and dissolving OriginBeta's convergence.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "OriginAlpha",
                AllowIdentityUpdates: true,
                IdentityScalars: ["alphaKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "OriginBeta",
                AllowIdentityUpdates: true,
                IdentityScalars: ["betaKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeOne",
                IdentityScalars: ["aaKey", "shadowKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("OriginAlpha", PartOfIdentity: true),
                    new PruningTestSchemaBuilder.ReferenceSpec("OriginBeta", PartOfIdentity: true),
                ]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeTwo",
                IdentityScalars: ["bridgeTwoKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("OriginAlpha", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeThree",
                IdentityScalars: ["bridgeThreeKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("OriginBeta", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeOne"),
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeTwo"),
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeThree"),
                ],
                EqualityConstraints:
                [
                    ("$.bridgeOneReference.alphaKey", "$.bridgeTwoReference.alphaKey"),
                    ("$.bridgeOneReference.shadowKey", "$.bridgeThreeReference.betaKey"),
                ]
            )
        );

        _result = PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should retry the earlier same-physical-FK decision: the BridgeOne edge (OriginAlpha's
    /// initially retained survivor) ends cut, and the BridgeTwo edge (initially cut by the
    /// abandoned decision) ends restored to cascade.
    /// </summary>
    [Test]
    public void It_should_retry_the_earlier_decision_on_the_same_physical_fk()
    {
        var bridgeOneFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "BridgeOne_DocumentId"
        );
        var bridgeTwoFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "BridgeTwo_DocumentId"
        );
        var bridgeThreeFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "BridgeThree_DocumentId"
        );

        bridgeOneFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
        bridgeTwoFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        bridgeThreeFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
    }

    /// <summary>
    /// It should leave every upstream origin-to-bridge edge cascading; retry decisions never
    /// touch edges outside the shared receiver.
    /// </summary>
    [Test]
    public void It_should_leave_upstream_edges_cascading()
    {
        PruningTopologyTestHelpers
            .FindReferenceForeignKey(_result, "BridgeOne", "OriginAlpha_DocumentId")
            .OnUpdate.Should()
            .Be(ReferentialAction.Cascade);
        PruningTopologyTestHelpers
            .FindReferenceForeignKey(_result, "BridgeOne", "OriginBeta_DocumentId")
            .OnUpdate.Should()
            .Be(ReferentialAction.Cascade);
        PruningTopologyTestHelpers
            .FindReferenceForeignKey(_result, "BridgeTwo", "OriginAlpha_DocumentId")
            .OnUpdate.Should()
            .Be(ReferentialAction.Cascade);
        PruningTopologyTestHelpers
            .FindReferenceForeignKey(_result, "BridgeThree", "OriginBeta_DocumentId")
            .OnUpdate.Should()
            .Be(ReferentialAction.Cascade);
    }
}

/// <summary>
/// Design verification case 8: a convergence with no safe survivor and no retryable earlier
/// decision fails derivation with the NoSafeSqlServerForeignKeyPruning diagnostic.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_No_Safe_Survivor
{
    /// <summary>
    /// Builds the failing topology: OriginBeta converges at Receiver through the BridgeOne and
    /// BridgeThree edges, and the equality constraint ties BridgeOne's own shadow key (static
    /// under an OriginBeta rename) to BridgeThree's beta key (origin-affected), so neither
    /// survivor choice passes the structural safe-cut test.
    /// </summary>
    private static DerivedRelationalModelSet BuildFailingTopology()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "OriginBeta",
                AllowIdentityUpdates: true,
                IdentityScalars: ["betaKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeOne",
                IdentityScalars: ["aaKey", "shadowKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("OriginBeta", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeThree",
                IdentityScalars: ["bridgeThreeKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("OriginBeta", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeOne"),
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeThree"),
                ],
                EqualityConstraints: [("$.bridgeOneReference.shadowKey", "$.bridgeThreeReference.betaKey")]
            )
        );

        return PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should fail derivation with the stable diagnostic token, the mutable origin, the
    /// convergence receiver, the conflicting constraints in stable survivor order, and the first
    /// failed safety condition.
    /// </summary>
    [Test]
    public void It_should_fail_with_the_no_safe_pruning_diagnostic()
    {
        var act = BuildFailingTopology;

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "NoSafeSqlServerForeignKeyPruning:*"
                    + "mutable origin 'edfi.OriginBeta'*"
                    + "convergence receiver 'edfi.Receiver'*"
                    + "'FK_Receiver_BridgeOne_RefKey'*"
                    + "'FK_Receiver_BridgeThree_RefKey'*"
                    + "First failed condition: canonical columns*"
            );
    }
}

/// <summary>
/// Shared-FK action conflict: a later convergence whose only safe survivor would cut an edge
/// retained by an earlier decision must resolve through retry, never by silently reversing the
/// earlier retained cascade (one physical constraint has one action).
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_A_Shared_Fk_Action_Conflict
{
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture. OriginAlpha's convergence at Receiver retains the optional
    /// BridgeOne edge (its columns are disjoint from BridgeTwo's, so the cut is trivially safe)
    /// and cuts BridgeTwo. OriginBeta's convergence then finds its only safety-passing survivor
    /// (BridgeThree) would cut the retained BridgeOne edge — a shared-FK action conflict — so
    /// OriginAlpha's decision is retried, cutting BridgeOne and restoring BridgeTwo instead.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "OriginAlpha",
                AllowIdentityUpdates: true,
                IdentityScalars: ["alphaKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "OriginBeta",
                AllowIdentityUpdates: true,
                IdentityScalars: ["betaKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeOne",
                IdentityScalars: ["bridgeOneKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("OriginAlpha", PartOfIdentity: true),
                    new PruningTestSchemaBuilder.ReferenceSpec("OriginBeta", PartOfIdentity: true),
                ]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeTwo",
                IdentityScalars: ["bridgeTwoKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("OriginAlpha", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeThree",
                IdentityScalars: ["bridgeThreeKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("OriginBeta", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeOne", Required: false),
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeTwo"),
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeThree"),
                ],
                EqualityConstraints: [("$.bridgeOneReference.betaKey", "$.bridgeThreeReference.betaKey")]
            )
        );

        _result = PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should resolve the conflict through retry: BridgeOne (the earlier retained survivor)
    /// ends cut, and BridgeTwo (cut by the abandoned decision) ends restored to cascade. Without
    /// the conflict rule, BridgeTwo would remain incorrectly cut alongside BridgeOne.
    /// </summary>
    [Test]
    public void It_should_resolve_the_conflict_through_retry_instead_of_reversing_the_retained_edge()
    {
        var bridgeOneFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "BridgeOne_DocumentId"
        );
        var bridgeTwoFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "BridgeTwo_DocumentId"
        );
        var bridgeThreeFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "BridgeThree_DocumentId"
        );

        bridgeOneFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
        bridgeTwoFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        bridgeThreeFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
    }
}

/// <summary>
/// Design verification case 9 (self-loop): a mutable resource referencing itself forms a
/// candidate self-loop and fails derivation with a stable cycle witness.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_Of_A_Self_Loop
{
    private static DerivedRelationalModelSet BuildSelfLoopTopology()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "Looper",
                AllowIdentityUpdates: true,
                IdentityScalars: ["loopKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Looper", RoleName: "ParentLooper")]
            )
        );

        return PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should fail with the cycle diagnostic and a self-loop witness; DMS does not choose an
    /// arbitrary cycle cut.
    /// </summary>
    [Test]
    public void It_should_fail_with_a_self_loop_cycle_witness()
    {
        var act = BuildSelfLoopTopology;

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("SqlServerCascadeCycleNotSupported:*edfi.Looper -> edfi.Looper*");
    }
}

/// <summary>
/// Design verification case 9 (multi-table cycle): mutually referencing mutable resources form a
/// candidate cycle and fail derivation with a stable cycle witness.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_Of_A_Multi_Table_Cycle
{
    private static DerivedRelationalModelSet BuildCycleTopology()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "CycleA",
                AllowIdentityUpdates: true,
                IdentityScalars: ["aKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("CycleB")]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "CycleB",
                IdentityScalars: ["bKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("CycleA", PartOfIdentity: true)]
            )
        );

        return PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should fail with the cycle diagnostic naming both tables in the witness.
    /// </summary>
    [Test]
    public void It_should_fail_with_a_multi_table_cycle_witness()
    {
        var act = BuildCycleTopology;

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("SqlServerCascadeCycleNotSupported:*edfi.CycleA*edfi.CycleB*");
    }
}

/// <summary>
/// Design verification case 10: a canonical-column mismatch — the shared column pairs to two
/// different origin identity columns — is rejected.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_A_Canonical_Column_Mismatch
{
    private static DerivedRelationalModelSet BuildMismatchTopology()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "Origin",
                AllowIdentityUpdates: true,
                IdentityScalars: ["keyOne", "keyTwo"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeA",
                IdentityScalars: ["bridgeAKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Origin", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeB",
                IdentityScalars: ["bridgeBKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Origin", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeA"),
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeB"),
                ],
                EqualityConstraints: [("$.bridgeAReference.keyOne", "$.bridgeBReference.keyTwo")]
            )
        );

        return PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should fail with the canonical-columns condition naming both mismatched origin columns.
    /// </summary>
    [Test]
    public void It_should_fail_when_the_shared_column_pairs_to_different_origin_columns()
    {
        var act = BuildMismatchTopology;

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "NoSafeSqlServerForeignKeyPruning:*canonical columns*edfi.Origin.Key*edfi.Origin.Key*"
            );
    }
}

/// <summary>
/// Design verification case 12: when every survivor choice is an optional (presence-sensitive)
/// carrier, the convergence is unsupported and derivation fails on the required-binding condition.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_Only_Optional_Carriers
{
    private static DerivedRelationalModelSet BuildOptionalCarrierTopology()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "Origin",
                AllowIdentityUpdates: true,
                IdentityScalars: ["originKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Bridge",
                IdentityScalars: ["bridgeKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Origin", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("Origin", Required: false),
                    new PruningTestSchemaBuilder.ReferenceSpec("Bridge", Required: false),
                ],
                EqualityConstraints: [("$.originReference.originKey", "$.bridgeReference.originKey")]
            )
        );

        return PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should fail on the required-binding condition: an optional carrier cannot supply native
    /// coverage, and DMS does not reason about presence implication.
    /// </summary>
    [Test]
    public void It_should_fail_when_every_survivor_is_an_optional_carrier()
    {
        var act = BuildOptionalCarrierTopology;

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("NoSafeSqlServerForeignKeyPruning:*required binding*");
    }
}

/// <summary>
/// Design verification case 11: a pairing whose trace steps through a local reference DocumentId
/// anchor depends on a reference replacement no native cascade can carry, and is rejected. The
/// shape cannot arise from well-formed resource schemas, so the selector is exercised directly
/// with hand-built candidates.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_A_Document_Id_Dependent_Pairing
{
    private static MssqlForeignKeyPruningPass.PruningCandidate BuildCandidate(
        DbTableName receiver,
        string name,
        string[] localColumns,
        DbTableName target,
        string[] targetColumns
    )
    {
        var foreignKey = new TableConstraint.ForeignKey(
            name,
            localColumns.Select(column => new DbColumnName(column)).ToArray(),
            target,
            targetColumns.Select(column => new DbColumnName(column)).ToArray(),
            ReferentialAction.NoAction,
            ReferentialAction.Cascade
        );

        return new MssqlForeignKeyPruningPass.PruningCandidate(
            MssqlForeignKeyPruningPass.PruningEdgeKey.From(receiver, foreignKey),
            name,
            receiver,
            target,
            foreignKey.Columns,
            foreignKey.Columns[^1],
            LocalDocumentIdIsRequired: true,
            foreignKey.TargetColumns,
            ReferentialAction.NoAction
        );
    }

    /// <summary>
    /// It should reject both survivor choices when the shared column's pairing walks through a
    /// DocumentId anchor, failing with the DocumentId condition.
    /// </summary>
    [Test]
    public void It_should_reject_a_pairing_that_depends_on_a_document_id_move()
    {
        var origin = new DbTableName(new DbSchemaName("edfi"), "Origin");
        var via = new DbTableName(new DbSchemaName("edfi"), "Via");
        var receiver = new DbTableName(new DbSchemaName("edfi"), "Receiver");

        var upstream = BuildCandidate(
            via,
            "FK_Via_Origin",
            ["Origin_OriginKey", "Origin_DocumentId"],
            origin,
            ["OriginKey", "DocumentId"]
        );
        // Via pathologically exposes its local reference DocumentId as a referenced identity
        // column, so the shared column's pairing must walk through that DocumentId anchor.
        var viaEdge = BuildCandidate(
            receiver,
            "FK_Receiver_Via",
            ["SharedKey", "Via_DocumentId"],
            via,
            ["Origin_DocumentId", "DocumentId"]
        );
        var originEdge = BuildCandidate(
            receiver,
            "FK_Receiver_Origin",
            ["SharedKey", "Receiver_Origin_DocumentId"],
            origin,
            ["OriginKey", "DocumentId"]
        );

        var act = () =>
            MssqlForeignKeyPruningPass.SelectCutEdges(
                ["edfi.Origin"],
                [upstream, viaEdge, originEdge],
                new HashSet<string>()
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("NoSafeSqlServerForeignKeyPruning:*DocumentId*");
    }
}

/// <summary>
/// An immutable abstract-identity origin (an abstract identity table whose concrete subclasses are
/// all immutable, e.g. <c>EducationOrganizationIdentity</c> in standard Ed-Fi) is still walked so
/// its multi-cascade-path topology is pruned, but a convergence discovered from it guards a rename
/// that never happens. When no locally safe survivor exists — the shape that fails for a genuinely
/// mutable origin — the selector rescues the immutable origin by retaining the first candidate and
/// cutting the rest. This is DS 6.1 <c>Goal</c>, which references <c>EvaluationElement</c> and
/// <c>EvaluationObjective</c> optionally while both share the education-organization canonical
/// column.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_An_Immutable_Abstract_Origin
{
    private static readonly DbTableName _origin = new(new DbSchemaName("edfi"), "Origin");
    private static readonly DbTableName _receiver = new(new DbSchemaName("edfi"), "Receiver");

    private static MssqlForeignKeyPruningPass.PruningCandidate BuildOptionalCarrier(
        string name,
        string documentIdColumn
    )
    {
        // Two edges from the same origin into one receiver, sharing the canonical "SharedKey"
        // local column paired to the origin's identity, each with its own optional (nullable)
        // local reference DocumentId anchor.
        var foreignKey = new TableConstraint.ForeignKey(
            name,
            [new DbColumnName("SharedKey"), new DbColumnName(documentIdColumn)],
            _origin,
            [new DbColumnName("OriginKey"), new DbColumnName("DocumentId")],
            ReferentialAction.NoAction,
            ReferentialAction.Cascade
        );

        return new MssqlForeignKeyPruningPass.PruningCandidate(
            MssqlForeignKeyPruningPass.PruningEdgeKey.From(_receiver, foreignKey),
            name,
            _receiver,
            _origin,
            foreignKey.Columns,
            foreignKey.Columns[^1],
            LocalDocumentIdIsRequired: false,
            foreignKey.TargetColumns,
            ReferentialAction.NoAction
        );
    }

    /// <summary>
    /// It should fail with the required-binding diagnostic when the origin is genuinely mutable:
    /// optional carriers supply no native coverage and DMS does not reason about presence.
    /// </summary>
    [Test]
    public void It_should_fail_when_the_origin_is_mutable()
    {
        var first = BuildOptionalCarrier("FK_Receiver_First", "First_DocumentId");
        var second = BuildOptionalCarrier("FK_Receiver_Second", "Second_DocumentId");

        var act = () =>
            MssqlForeignKeyPruningPass.SelectCutEdges(
                ["edfi.Origin"],
                [first, second],
                new HashSet<string>()
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("NoSafeSqlServerForeignKeyPruning:*required binding*");
    }

    /// <summary>
    /// It should rescue the same topology when the origin is an immutable abstract identity:
    /// retain the first stable survivor and cut the other incoming edge to NO ACTION.
    /// </summary>
    [Test]
    public void It_should_retain_the_first_survivor_and_cut_the_rest_when_the_origin_is_immutable()
    {
        var first = BuildOptionalCarrier("FK_Receiver_First", "First_DocumentId");
        var second = BuildOptionalCarrier("FK_Receiver_Second", "Second_DocumentId");

        var cutEdges = MssqlForeignKeyPruningPass.SelectCutEdges(
            ["edfi.Origin"],
            [first, second],
            new HashSet<string> { "edfi.Origin" }
        );

        cutEdges.Should().ContainSingle().Which.Should().Be(second.EdgeKey);
        cutEdges.Should().NotContain(first.EdgeKey);
    }
}

/// <summary>
/// Design verification case 13 (parallel FKs): two distinct physical FKs between the same tables
/// are a convergence in the multigraph; one survives and the other is cut.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_Of_Parallel_Physical_Fks
{
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture: Receiver references the mutable Origin twice under different
    /// roles with disjoint storage columns.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "Origin",
                AllowIdentityUpdates: true,
                IdentityScalars: ["originKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("Origin", RoleName: "FirstOrigin"),
                    new PruningTestSchemaBuilder.ReferenceSpec("Origin", RoleName: "SecondOrigin"),
                ]
            )
        );

        _result = PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should retain the first parallel edge in stable order and cut the second; the disjoint
    /// cut carries no coverage obligation.
    /// </summary>
    [Test]
    public void It_should_retain_the_first_parallel_edge_and_cut_the_second()
    {
        var firstFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "FirstOrigin_DocumentId"
        );
        var secondFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "SecondOrigin_DocumentId"
        );

        firstFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        secondFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }
}

/// <summary>
/// Design verification case 13 (collapse): bindings that resolve to the same physical constraint
/// after key unification collapse into a single candidate, because one physical constraint has
/// one action.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_Unified_Same_Target_References
{
    private IReadOnlyList<MssqlForeignKeyPruningPass.PruningCandidate> _candidates = default!;
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture: Receiver references the mutable Origin twice with the identity
    /// value tied by an equality constraint, unifying the reference storage.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "Origin",
                AllowIdentityUpdates: true,
                IdentityScalars: ["originKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("Origin", RoleName: "FirstOrigin"),
                    new PruningTestSchemaBuilder.ReferenceSpec("Origin", RoleName: "SecondOrigin"),
                ],
                EqualityConstraints:
                [
                    ("$.firstOriginReference.originKey", "$.secondOriginReference.originKey"),
                ]
            )
        );

        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var probe = new PruningCandidateProbePass();
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new RootIdentityConstraintPass(),
            new TransitiveIdentityMutabilityPass(),
            new ReferenceConstraintPass(),
            probe,
            new MssqlForeignKeyPruningPass(),
        ]);

        _result = builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());
        _candidates = probe.Candidates;
    }

    /// <summary>
    /// It should keep candidate identity physical: distinct DocumentId anchors remain distinct
    /// physical constraints, and the value-unified convergence still prunes to a single cascade.
    /// </summary>
    [Test]
    public void It_should_collapse_identical_physical_constraints_and_prune_the_rest()
    {
        _candidates.Should().HaveCount(2);

        var firstFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "FirstOrigin_DocumentId"
        );
        var secondFk = PruningTopologyTestHelpers.FindReferenceForeignKey(
            _result,
            "Receiver",
            "SecondOrigin_DocumentId"
        );

        firstFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
        secondFk.OnUpdate.Should().Be(ReferentialAction.NoAction);
    }
}

/// <summary>
/// Design verification case 13 (determinism): declaration order of resources and references does
/// not change the pruning outcome.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_Reversed_Declaration_Order
{
    private static DerivedRelationalModelSet BuildDiamond(bool reversed)
    {
        var origin = new PruningTestSchemaBuilder.ResourceSpec(
            "Origin",
            AllowIdentityUpdates: true,
            IdentityScalars: ["originKey"]
        );
        var bridge = new PruningTestSchemaBuilder.ResourceSpec(
            "Bridge",
            IdentityScalars: ["bridgeKey"],
            References: [new PruningTestSchemaBuilder.ReferenceSpec("Origin", PartOfIdentity: true)]
        );
        var receiverReferences = new[]
        {
            new PruningTestSchemaBuilder.ReferenceSpec("Origin"),
            new PruningTestSchemaBuilder.ReferenceSpec("Bridge"),
        };
        var receiver = new PruningTestSchemaBuilder.ResourceSpec(
            "Receiver",
            IdentityScalars: ["receiverKey"],
            References: reversed ? [.. receiverReferences.Reverse()] : receiverReferences,
            EqualityConstraints: [("$.originReference.originKey", "$.bridgeReference.originKey")]
        );

        var projectSchema = reversed
            ? PruningTestSchemaBuilder.BuildProjectSchema(receiver, bridge, origin)
            : PruningTestSchemaBuilder.BuildProjectSchema(origin, bridge, receiver);

        return PruningTopologyTestHelpers.BuildMssqlModelSet(projectSchema);
    }

    /// <summary>
    /// It should produce identical final actions regardless of declaration order.
    /// </summary>
    [Test]
    public void It_should_produce_identical_actions_regardless_of_declaration_order()
    {
        var forward = BuildDiamond(reversed: false);
        var reversed = BuildDiamond(reversed: true);

        foreach (var result in new[] { forward, reversed })
        {
            PruningTopologyTestHelpers
                .FindReferenceForeignKey(result, "Receiver", "Bridge_DocumentId")
                .OnUpdate.Should()
                .Be(ReferentialAction.Cascade);
            PruningTopologyTestHelpers
                .FindReferenceForeignKey(result, "Receiver", "Origin_DocumentId")
                .OnUpdate.Should()
                .Be(ReferentialAction.NoAction);
        }
    }
}

/// <summary>
/// Design verification case 14 (child bindings): document references bound on child/collection
/// tables participate in candidate discovery and pruning.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_A_Child_Collection_Binding
{
    private IReadOnlyList<MssqlForeignKeyPruningPass.PruningCandidate> _candidates = default!;
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture with the shared child-reference schema: BusRoute stores a School
    /// reference inside its addresses collection, and School allows identity updates.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema =
            ConstraintDerivationTestSchemaBuilder.BuildReferenceConstraintProjectSchemaWithChildReference();
        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([coreProject]);
        var probe = new PruningCandidateProbePass();
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new RootIdentityConstraintPass(),
            new TransitiveIdentityMutabilityPass(),
            new ReferenceConstraintPass(),
            probe,
            new MssqlForeignKeyPruningPass(),
        ]);

        _result = builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());
        _candidates = probe.Candidates;
    }

    /// <summary>
    /// It should discover the child-table School binding as a candidate and keep it cascading
    /// (its receiver differs from the root binding's receiver, so there is no convergence).
    /// </summary>
    [Test]
    public void It_should_discover_and_prune_child_table_bindings()
    {
        var childCandidate = _candidates.Single(candidate =>
            candidate.TargetTable.Name == "School" && candidate.ReceiverTable.Name != "Enrollment"
        );

        childCandidate.ReceiverTable.Name.Should().NotBe("BusRoute");

        var childFk = PruningTopologyTestHelpers.FindForeignKeyOnTable(
            _result,
            "BusRoute",
            childCandidate.ReceiverTable,
            childCandidate.LocalDocumentIdColumn.Value
        );

        childFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
    }
}

/// <summary>
/// Design verification case 14 (extension bindings): document references bound on extension
/// tables participate in candidate discovery and pruning.
/// </summary>
[TestFixture]
public class Given_Mssql_Pruning_With_An_Extension_Binding
{
    private IReadOnlyList<MssqlForeignKeyPruningPass.PruningCandidate> _candidates = default!;
    private DerivedRelationalModelSet _result = default!;

    /// <summary>
    /// Sets up the test fixture: a Sample-project extension of Host stores a reference to the
    /// mutable core Origin resource under its _ext scope.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var coreProjectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "Origin",
                AllowIdentityUpdates: true,
                IdentityScalars: ["originKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec("Host", IdentityScalars: ["hostKey"])
        );
        var extensionProjectSchema = new System.Text.Json.Nodes.JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new System.Text.Json.Nodes.JsonObject
            {
                ["hosts"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["resourceName"] = "Host",
                    ["isDescriptor"] = false,
                    ["isResourceExtension"] = true,
                    ["isSubclass"] = false,
                    ["allowIdentityUpdates"] = false,
                    ["arrayUniquenessConstraints"] = new System.Text.Json.Nodes.JsonArray(),
                    ["identityJsonPaths"] = new System.Text.Json.Nodes.JsonArray(),
                    ["documentPathsMapping"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["Origin"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["isReference"] = true,
                            ["isDescriptor"] = false,
                            ["isRequired"] = true,
                            ["projectName"] = "Ed-Fi",
                            ["resourceName"] = "Origin",
                            ["referenceJsonPaths"] = new System.Text.Json.Nodes.JsonArray
                            {
                                new System.Text.Json.Nodes.JsonObject
                                {
                                    ["identityJsonPath"] = "$.originKey",
                                    ["referenceJsonPath"] = "$._ext.sample.originReference.originKey",
                                },
                            },
                        },
                    },
                    ["jsonSchemaForInsert"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["_ext"] = new System.Text.Json.Nodes.JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new System.Text.Json.Nodes.JsonObject
                                {
                                    ["sample"] = new System.Text.Json.Nodes.JsonObject
                                    {
                                        ["type"] = "object",
                                        ["properties"] = new System.Text.Json.Nodes.JsonObject
                                        {
                                            ["originReference"] = new System.Text.Json.Nodes.JsonObject
                                            {
                                                ["type"] = "object",
                                                ["properties"] = new System.Text.Json.Nodes.JsonObject
                                                {
                                                    ["originKey"] = new System.Text.Json.Nodes.JsonObject
                                                    {
                                                        ["type"] = "integer",
                                                    },
                                                },
                                            },
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        };

        var coreProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            coreProjectSchema,
            isExtensionProject: false
        );
        var extensionProject = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            extensionProjectSchema,
            isExtensionProject: true
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([
            coreProject,
            extensionProject,
        ]);
        var probe = new PruningCandidateProbePass();
        var builder = new DerivedRelationalModelSetBuilder([
            new BaseTraversalAndDescriptorBindingPass(),
            new DescriptorResourceMappingPass(),
            new ExtensionTableDerivationPass(),
            new ReferenceBindingPass(),
            new KeyUnificationPass(),
            new RootIdentityConstraintPass(),
            new TransitiveIdentityMutabilityPass(),
            new ReferenceConstraintPass(),
            probe,
            new MssqlForeignKeyPruningPass(),
        ]);

        _result = builder.Build(schemaSet, SqlDialect.Mssql, new MssqlDialectRules());
        _candidates = probe.Candidates;
    }

    /// <summary>
    /// It should discover the extension-table Origin binding as a candidate and keep it cascading.
    /// </summary>
    [Test]
    public void It_should_discover_and_prune_extension_table_bindings()
    {
        var extensionCandidate = _candidates.Single(candidate => candidate.TargetTable.Name == "Origin");

        extensionCandidate.ReceiverTable.Name.Should().NotBe("Host");

        var extensionFk = PruningTopologyTestHelpers.FindForeignKeyOnTable(
            _result,
            "Host",
            extensionCandidate.ReceiverTable,
            extensionCandidate.LocalDocumentIdColumn.Value
        );

        extensionFk.OnUpdate.Should().Be(ReferentialAction.Cascade);
    }
}

/// <summary>
/// PostgreSQL bypass: models that fail SQL Server pruning derive unchanged for PostgreSQL with
/// full-composite cascades and no cycle or pruning diagnostics.
/// </summary>
[TestFixture]
public class Given_Pgsql_Derivation_Of_Sql_Server_Only_Failure_Topologies
{
    /// <summary>
    /// It should derive the no-safe-survivor topology unchanged with both cascades retained.
    /// </summary>
    [Test]
    public void It_should_derive_the_no_safe_pruning_topology_with_cascades_retained()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "OriginBeta",
                AllowIdentityUpdates: true,
                IdentityScalars: ["betaKey"]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeOne",
                IdentityScalars: ["aaKey", "shadowKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("OriginBeta", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "BridgeThree",
                IdentityScalars: ["bridgeThreeKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("OriginBeta", PartOfIdentity: true)]
            ),
            new PruningTestSchemaBuilder.ResourceSpec(
                "Receiver",
                IdentityScalars: ["receiverKey"],
                References:
                [
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeOne"),
                    new PruningTestSchemaBuilder.ReferenceSpec("BridgeThree"),
                ],
                EqualityConstraints: [("$.bridgeOneReference.shadowKey", "$.bridgeThreeReference.betaKey")]
            )
        );

        var result = PruningTopologyTestHelpers.BuildPgsqlModelSet(projectSchema);

        PruningTopologyTestHelpers
            .FindReferenceForeignKey(result, "Receiver", "BridgeOne_DocumentId")
            .OnUpdate.Should()
            .Be(ReferentialAction.Cascade);
        PruningTopologyTestHelpers
            .FindReferenceForeignKey(result, "Receiver", "BridgeThree_DocumentId")
            .OnUpdate.Should()
            .Be(ReferentialAction.Cascade);
    }

    /// <summary>
    /// It should derive the self-loop topology without a cycle diagnostic.
    /// </summary>
    [Test]
    public void It_should_derive_the_cycle_topology_without_diagnostics()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec(
                "Looper",
                AllowIdentityUpdates: true,
                IdentityScalars: ["loopKey"],
                References: [new PruningTestSchemaBuilder.ReferenceSpec("Looper", RoleName: "ParentLooper")]
            )
        );

        var result = PruningTopologyTestHelpers.BuildPgsqlModelSet(projectSchema);

        PruningTopologyTestHelpers
            .FindReferenceForeignKey(result, "Looper", "ParentLooper_DocumentId")
            .OnUpdate.Should()
            .Be(ReferentialAction.Cascade);
    }
}
