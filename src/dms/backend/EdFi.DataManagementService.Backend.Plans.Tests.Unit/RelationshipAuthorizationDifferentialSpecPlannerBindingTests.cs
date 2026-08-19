// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

/// <summary>
/// Binds <see cref="RelationshipAuthorizationDifferentialSpecs"/> to what the planner actually derives from the
/// authoritative DS 5.2 schema (DMS-1331).
/// </summary>
/// <remarks>
/// <para>
/// Those specs hard-code a person path kind and an anchor column per resource, and every AC2/AC3/AC4 claim on
/// both engines is compiled from them: the row-set differential, the EXPLAIN plan-shape evidence, and the
/// before/after timings all take a spec as their subject. Nothing else asserts that the planner resolves those
/// same paths, so without this test a reclassification — a data-standard bump that inserts a hop, or a change to
/// the shortest-path rule in <c>SecurableElementColumnPathResolver</c> — would leave every one of those fixtures
/// green while measuring a predicate shape the product had stopped emitting.
/// </para>
/// <para>
/// The multi-hop golden covers part of this incidentally, because Grade and CourseTranscript appear in
/// <c>Fixtures/authoritative/ds-5.2/expected/multi-hop-person-auth-paths-pgsql.json</c>. It cannot cover the
/// rest: that enumeration skips single-step paths, which is exactly the three direct-column resources here.
/// </para>
/// <para>
/// Run for both dialects, because the specs are built per dialect and the SQL Server fixtures consume the
/// SQL Server ones. The two currently resolve to identical paths, but that is an outcome to check rather than a
/// premise to rely on: column naming is dialect-dependent in this codebase, which is why
/// <c>MultiHopPersonAuthPathEnumerationTests</c> keeps a golden per dialect instead of asserting they agree.
/// </para>
/// </remarks>
[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_The_Relationship_Authorization_Differential_Specs(SqlDialect dialect)
{
    private static readonly DbColumnName _documentIdColumn = new("DocumentId");

    private DerivedRelationalModelSet _modelSet = null!;
    private MappingSet _mappingSet = null!;
    private IReadOnlyList<RelationshipAuthorizationDifferentialSpec> _specs = null!;

    /// <summary>
    /// Every spec whose subject reaches the person through a resolved join chain, taken from the spec list itself
    /// rather than written out, so a subject added to
    /// <see cref="RelationshipAuthorizationDifferentialSpecs"/> is bound here automatically instead of silently
    /// escaping the very drift check this fixture exists to provide.
    /// </summary>
    public static IEnumerable<string> PathBackedResourceNames =>
        ResourceNamesWithPathKind(kind =>
            kind != RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId
        );

    /// <summary>The Self subjects, which reach the person with no chain at all.</summary>
    public static IEnumerable<string> SelfAnchoredResourceNames =>
        ResourceNamesWithPathKind(kind =>
            kind == RelationshipAuthorizationPersonSubjectPathKind.SelfRootDocumentId
        );

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        (_modelSet, _mappingSet) = Ds52FixtureHelper.BuildAndCompile(dialect);
        _specs = CreateSpecs(dialect);
    }

    /// <summary>
    /// The whole premise of the differential: the ordered join chain a spec hard-codes is the one the planner
    /// resolves for that resource, step for step. Full-chain equality subsumes the anchor column, the terminal
    /// person column, and every hop between them.
    /// </summary>
    [TestCaseSource(nameof(PathBackedResourceNames))]
    public void It_should_carry_the_planner_derived_student_path(string resourceName)
    {
        var specSteps = PersonSubjectFor(resourceName).PersonMetadata.Path.Steps;

        ResolvedStudentPathFor(resourceName)
            .Steps.Should()
            .Equal(
                specSteps,
                $"the differential spec for {resourceName} must describe the path the planner resolves, or "
                    + "every fixture built on it measures a shape the product no longer emits"
            );
    }

    /// <summary>
    /// The path kind follows from the chain, by the same rule <c>RelationalPeopleAuthorizationSubjectSelector</c>
    /// applies: a single step off the root is direct, anything longer is transitive. Pinning it separately is what
    /// makes a reclassification fail here rather than silently pick a different emitter branch.
    /// </summary>
    [TestCaseSource(nameof(PathBackedResourceNames))]
    public void It_should_carry_the_path_kind_the_resolved_chain_implies(string resourceName)
    {
        var spec = SpecFor(resourceName);
        var resolvedSteps = ResolvedStudentPathFor(resourceName).Steps;

        var expectedKind =
            resolvedSteps.Count == 1 && resolvedSteps[0].SourceTable.Equals(spec.RootTable)
                ? RelationshipAuthorizationPersonSubjectPathKind.DirectRootColumn
                : RelationshipAuthorizationPersonSubjectPathKind.TransitiveJoinPath;

        PersonSubjectFor(resourceName).PersonMetadata.Path.Kind.Should().Be(expectedKind);
    }

    /// <summary>
    /// The property DMS-1331 exists to establish, stated against planner output: the column the compiler emits
    /// after <c>r.</c> is the first resolved step's source column, and that step starts on the root table — so the
    /// anchor is a column the root row itself carries rather than a primary key reached by reopening the table.
    /// </summary>
    [TestCaseSource(nameof(PathBackedResourceNames))]
    public void It_should_anchor_on_a_first_step_source_column_the_root_row_carries(string resourceName)
    {
        ResolvedStudentPathFor(resourceName)
            .Steps[0]
            .SourceTable.Should()
            .Be(SpecFor(resourceName).RootTable, "the anchor must be a column the root row itself carries");
    }

    /// <summary>
    /// The Self shape, and the planner expresses it by resolving no person path for the resource at all: the root
    /// row <em>is</em> the person, so the anchor is its own DocumentId rather than a chain. Asserting the absence
    /// is what keeps the spec's zero-hop path honest — if a future schema gave the root a resolved person path,
    /// the Self spec would no longer describe how the planner reaches it.
    /// </summary>
    [TestCaseSource(nameof(SelfAnchoredResourceNames))]
    public void It_should_model_a_self_root_as_a_zero_hop_anchor_on_its_own_document_id(string resourceName)
    {
        var hasResolvedStudentPath =
            _mappingSet.SecurableElementColumnPathsByResource.TryGetValue(
                ResourceFor(resourceName),
                out var resolvedPaths
            ) && resolvedPaths.Any(path => path.Kind == SecurableElementKind.Student);

        hasResolvedStudentPath
            .Should()
            .BeFalse(
                $"the {resourceName} root reaches the person without a join chain, which is what Self means"
            );

        var personMetadata = PersonSubjectFor(resourceName).PersonMetadata;

        personMetadata.Path.Steps.Should().BeEmpty();
        personMetadata
            .StoredAnchor.Should()
            .Be(
                new RelationshipAuthorizationPersonStoredAnchor(
                    SpecFor(resourceName).RootTable,
                    _documentIdColumn
                )
            );
    }

    /// <summary>
    /// Resource names carrying a given path kind. Enumerated from the PostgreSQL specs because
    /// <see cref="RelationshipAuthorizationDifferentialSpecs.Create"/> varies only the claim parameterization by
    /// dialect — the subject set, its tables and its paths are the same literals either way — while each fixture
    /// instance still resolves and asserts against its own dialect.
    /// </summary>
    private static IEnumerable<string> ResourceNamesWithPathKind(
        Func<RelationshipAuthorizationPersonSubjectPathKind, bool> predicate
    ) =>
        CreateSpecs(SqlDialect.Pgsql)
            .Where(spec => predicate(PersonSubject(spec).PersonMetadata.Path.Kind))
            .Select(static spec => spec.ResourceName)
            .ToArray();

    private static IReadOnlyList<RelationshipAuthorizationDifferentialSpec> CreateSpecs(
        SqlDialect specDialect
    ) =>
        RelationshipAuthorizationDifferentialSpecs.Create(
            specDialect,
            [RelationshipAuthorizationVolumeIdentifiers.ClaimEducationOrganizationId]
        );

    private static PageDocumentIdAuthorizationPersonSubject PersonSubject(
        RelationshipAuthorizationDifferentialSpec spec
    ) =>
        (PageDocumentIdAuthorizationPersonSubject)
            spec.QuerySpec.Authorization!.Strategies.Single().Subjects.Single();

    private RelationshipAuthorizationDifferentialSpec SpecFor(string resourceName) =>
        _specs.Single(spec => spec.ResourceName == resourceName);

    private PageDocumentIdAuthorizationPersonSubject PersonSubjectFor(string resourceName) =>
        PersonSubject(SpecFor(resourceName));

    private ResolvedSecurableElementPath ResolvedStudentPathFor(string resourceName) =>
        _mappingSet
            .SecurableElementColumnPathsByResource[ResourceFor(resourceName)]
            .Single(path => path.Kind == SecurableElementKind.Student);

    private QualifiedResourceName ResourceFor(string resourceName) =>
        _modelSet
            .ConcreteResourcesInNameOrder.Select(static concreteResource =>
                concreteResource.RelationalModel.Resource
            )
            .Single(resource => resource.ResourceName == resourceName);
}
