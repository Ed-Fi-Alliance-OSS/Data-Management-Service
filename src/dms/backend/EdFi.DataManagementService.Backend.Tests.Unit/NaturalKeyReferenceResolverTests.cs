// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Unit.NaturalKeyLookupTestMappingSets;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_Natural_Key_Reference_Resolver
{
    private const string CareerUri = "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education";
    private const long SchoolDocumentId = 1001;
    private const long StudentDocumentId = 2002;
    private const long ProgramDocumentId = 3003;
    private const long AssociationDocumentId = 4004;
    private const long CareerDescriptorDocumentId = 900001;
    private const int CareerDescriptorId = 7;

    private static readonly QualifiedResourceName _student = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _studentProgramAssociation = new(
        "Ed-Fi",
        "StudentProgramAssociation"
    );

    private MappingSet _mappingSet = null!;
    private RecordingLookupCommandBuilder _builder = null!;
    private FakeLookupExecutor _executor = null!;
    private NaturalKeyReferenceResolver _resolver = null!;

    [SetUp]
    public void Setup()
    {
        _mappingSet = Ds52(SqlDialect.Pgsql);
        _builder = new RecordingLookupCommandBuilder();
        _executor = new FakeLookupExecutor(_builder, FindStoredRow);
        _resolver = new NaturalKeyReferenceResolver(_builder, new ResolvedDocumentReferenceMapFactory());
    }

    [Test]
    public async Task It_looks_up_equal_identities_on_separate_arrays_once_per_request()
    {
        var result = await ResolveAsync(
            [
                SchoolReference("255901", "$.schoolReference"),
                SchoolReference("255901", "$.schools[0].schoolReference"),
            ],
            []
        );
        var second = await ResolveAsync([SchoolReference("255901", "$.otherSchoolReference")], []);

        _builder.Requests.Should().ContainSingle();
        _builder.Requests[0].Groups.Should().ContainSingle().Which.Entries.Should().ContainSingle();
        _executor.CommandCount.Should().Be(1);
        result.SuccessfulDocumentReferencesByPath.Should().HaveCount(2);
        second
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.otherSchoolReference")]
            .DocumentId.Should()
            .Be(SchoolDocumentId);
    }

    [Test]
    public void It_builds_no_lookup_when_every_reference_is_memoized()
    {
        var request = Request([SchoolReference("255901")], [DescriptorReference(CareerUri)]);
        var batch = _resolver.TryBuildLookupRequest(request)!;
        _resolver.ResolveFromRows(request, batch, LookUp(batch.LookupRequest!));

        _resolver
            .TryBuildLookupRequest(Request([SchoolReference("255901")], [DescriptorReference(CareerUri)]))
            .Should()
            .BeNull();
    }

    [Test]
    public async Task It_groups_targets_in_first_seen_order_with_descriptor_targets_after_documents()
    {
        await ResolveAsync(
            [
                ProgramReference("Career Prep", CareerUri),
                SchoolReference("255901"),
                ProgramReference("Robotics", CareerUri),
            ],
            [DescriptorReference(CareerUri)]
        );

        var groups = _builder.Requests.Single().Groups;
        groups
            .Select(group => (group.GroupOrdinal, group.Target.Resource, group.Entries.Count))
            .Should()
            .Equal((1, Program, 2), (2, School, 1), (3, ProgramTypeDescriptor, 1));
    }

    [Test]
    public async Task It_binds_typed_values_in_probe_order_and_descriptor_uris_unchanged()
    {
        const string mixedCaseUri = "uri://ED-FI.org/ProgramTypeDescriptor#Career And Technical Education";

        await ResolveAsync(
            [SchoolReference("255901"), AssociationReference(mixedCaseUri)],
            [DescriptorReference(mixedCaseUri)]
        );

        var groups = _builder.Requests.Single().Groups;
        groups[0].Entries[0].KeyValues.Should().Equal(255901L);
        groups[1]
            .Entries[0]
            .KeyValues.Should()
            .Equal(new DateOnly(2026, 8, 20), 255901L, 255901L, "Career Prep", mixedCaseUri, "S-1");
        groups[2].Entries[0].KeyValues.Should().Equal(mixedCaseUri);
    }

    [Test]
    public async Task It_classifies_unstorable_values_as_missing_without_binding_them()
    {
        var result = await ResolveAsync(
            [
                SchoolReference("not-a-number", "$.badSchoolReference"),
                ProgramReference(new string('x', 61), CareerUri, "$.longProgramReference"),
                SchoolReference("255901"),
            ],
            [DescriptorReference("uri://ed-fi.org/" + new string('x', 291), "$.longDescriptor")]
        );

        result
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.badSchoolReference", DocumentReferenceFailureReason.Missing),
                ("$.longProgramReference", DocumentReferenceFailureReason.Missing)
            );
        result
            .InvalidDescriptorReferences.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(DescriptorReferenceFailureReason.Missing);
        _builder.Requests.Single().Groups.Should().ContainSingle().Which.Entries.Should().ContainSingle();
        _builder.Requests.Single().Groups[0].Entries[0].KeyValues.Should().Equal(255901L);
    }

    [Test]
    public async Task It_sends_no_command_when_every_uncached_value_is_unstorable()
    {
        var result = await ResolveAsync([SchoolReference("not-a-number")], []);

        _executor.CommandCount.Should().Be(0);
        result.InvalidDocumentReferences.Should().ContainSingle();
    }

    [Test]
    public void It_rejects_an_identity_with_a_path_the_probe_does_not_declare()
    {
        var reference = Reference(School, "$.schoolReference", ("$.schoolId", "255901"), ("$.extra", "1"));

        var act = () => _resolver.TryBuildLookupRequest(Request([reference], []));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*'Ed-Fi.School' carries identity paths*");
    }

    [Test]
    public void It_rejects_an_identity_missing_a_probe_path()
    {
        var reference = Reference(School, "$.schoolReference", ("$.id", "255901"));

        var act = () => _resolver.TryBuildLookupRequest(Request([reference], []));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*'Ed-Fi.School' is missing identity path '$.schoolId'*");
    }

    [Test]
    public void It_rejects_a_target_without_a_compiled_probe()
    {
        var reference = Reference(
            new QualifiedResourceName("Ed-Fi", "Unknown"),
            "$.unknownReference",
            ("$.id", "1")
        );

        var act = () => _resolver.TryBuildLookupRequest(Request([reference], []));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*'Ed-Fi.Unknown' has no compiled natural-key probe*");
    }

    [Test]
    public async Task It_classifies_a_match_outside_the_reference_target_as_incompatible()
    {
        var result = await ResolveAsync(
            [
                Reference(
                    EducationOrganization,
                    "$.educationOrganizationReference",
                    ("$.educationOrganizationId", "255902")
                ),
            ],
            []
        );

        result.SuccessfulDocumentReferencesByPath.Should().BeEmpty();
        result
            .InvalidDocumentReferences.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(DocumentReferenceFailureReason.IncompatibleTargetType);
    }

    [Test]
    public async Task It_classifies_unknown_references_as_missing()
    {
        var result = await ResolveAsync(
            [SchoolReference("999999")],
            [DescriptorReference("uri://ed-fi.org/ProgramTypeDescriptor#Unknown")]
        );

        result
            .InvalidDocumentReferences.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(DocumentReferenceFailureReason.Missing);
        result
            .InvalidDescriptorReferences.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(DescriptorReferenceFailureReason.Missing);
        result.HasFailures.Should().BeTrue();
    }

    [Test]
    public async Task It_keeps_unequal_descriptor_and_document_ids_in_their_roles()
    {
        var result = await ResolveAsync(
            [ProgramReference("Career Prep", CareerUri)],
            [DescriptorReference(CareerUri.ToUpperInvariant())]
        );

        var descriptor = result.SuccessfulDescriptorReferencesByPath[new JsonPath("$.programTypeDescriptor")];
        descriptor.DescriptorId.Should().Be(CareerDescriptorId);
        descriptor.DocumentId.Should().Be(CareerDescriptorDocumentId);
        descriptor.ResourceKeyId.Should().Be(_mappingSet.ResourceKeyIdByResource[ProgramTypeDescriptor]);
        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.programReference")]
            .DocumentId.Should()
            .Be(ProgramDocumentId);
    }

    [Test]
    public async Task It_resolves_an_abstract_reference_to_the_concrete_member()
    {
        var result = await ResolveAsync([AssociationReference(CareerUri)], []);

        var resolved = result.SuccessfulDocumentReferencesByPath[
            new JsonPath("$.generalStudentProgramAssociationReference")
        ];
        resolved.DocumentId.Should().Be(AssociationDocumentId);
        resolved.ResourceKeyId.Should().Be(_mappingSet.ResourceKeyIdByResource[_studentProgramAssociation]);
    }

    [Test]
    public async Task It_builds_a_document_map_that_agrees_with_the_by_path_results()
    {
        var result = await ResolveAsync(
            [
                SchoolReference("255901"),
                ProgramReference("Career Prep", CareerUri),
                AssociationReference(CareerUri),
            ],
            []
        );

        result.SuccessfulDocumentReferencesByPath.Should().HaveCount(3);

        foreach (var resolved in result.SuccessfulDocumentReferencesByPath.Values)
        {
            var target = RelationalWriteSupport.ToQualifiedResourceName(resolved.Reference.ResourceInfo);
            var copiedIdentity = new DocumentIdentity([
                .. resolved.Reference.DocumentIdentity.DocumentIdentityElements,
            ]);

            result
                .DocumentReferences.TryGetDocumentId(target, copiedIdentity, out var documentId)
                .Should()
                .BeTrue();
            documentId.Should().Be(resolved.DocumentId);
        }
    }

    [Test]
    public async Task It_classifies_decoded_rows_the_same_way_as_the_standalone_command()
    {
        DocumentReference[] documents =
        [
            SchoolReference("255901"),
            SchoolReference("999999", "$.missingSchoolReference"),
            ProgramReference("Career Prep", CareerUri),
            Reference(
                EducationOrganization,
                "$.educationOrganizationReference",
                ("$.educationOrganizationId", "255902")
            ),
        ];
        DescriptorReference[] descriptors =
        [
            DescriptorReference(CareerUri),
            DescriptorReference("uri://ed-fi.org/ProgramTypeDescriptor#Unknown", "$.unknownDescriptor"),
        ];

        var standalone = await ResolveAsync(documents, descriptors);

        var replayResolver = new NaturalKeyReferenceResolver(
            _builder,
            new ResolvedDocumentReferenceMapFactory()
        );
        var request = Request(documents, descriptors);
        var batch = replayResolver.TryBuildLookupRequest(request)!;
        var replayed = replayResolver.ResolveFromRows(request, batch, LookUp(batch.LookupRequest!));

        replayed
            .SuccessfulDocumentReferencesByPath.Should()
            .BeEquivalentTo(standalone.SuccessfulDocumentReferencesByPath);
        replayed
            .SuccessfulDescriptorReferencesByPath.Should()
            .BeEquivalentTo(standalone.SuccessfulDescriptorReferencesByPath);
        replayed.InvalidDocumentReferences.Should().Equal(standalone.InvalidDocumentReferences);
        replayed.InvalidDescriptorReferences.Should().Equal(standalone.InvalidDescriptorReferences);
    }

    [Test]
    public void It_rejects_rows_that_were_not_decoded_for_the_request()
    {
        var request = Request([SchoolReference("255901")], []);
        var batch = _resolver.TryBuildLookupRequest(request)!;
        var otherRequest = Request([SchoolReference("255902")], []);

        var act = () => _resolver.ResolveFromRows(otherRequest, batch, LookUp(batch.LookupRequest!));

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*no lookup for a reference to 'Ed-Fi.School'*");
    }

    private Task<NaturalKeyResolvedReferences> ResolveAsync(
        IReadOnlyList<DocumentReference> documents,
        IReadOnlyList<DescriptorReference> descriptors
    ) => _resolver.ResolveAsync(Request(documents, descriptors), _executor);

    private ReferenceResolverRequest Request(
        IReadOnlyList<DocumentReference> documents,
        IReadOnlyList<DescriptorReference> descriptors
    ) => new(_mappingSet, _studentProgramAssociation, documents, descriptors);

    /// <summary>
    /// Answers every entry of <paramref name="request"/> from the fake store, as a decoded match table.
    /// </summary>
    private NaturalKeyLookupMatch?[][] LookUp(NaturalKeyLookupRequest request) =>
        [
            .. request.Groups.Select(group =>
                group.Entries.Select(entry => FindStoredRow(group, entry)).ToArray()
            ),
        ];

    /// <summary>
    /// A tiny stand-in for the database: one School, one Student whose key sits behind an EducationOrganization
    /// identity row, one Program, one association, and one descriptor with unequal descriptor and document ids.
    /// Descriptor URIs fold case, as the probes do.
    /// </summary>
    private NaturalKeyLookupMatch? FindStoredRow(NaturalKeyLookupGroup group, NaturalKeyLookupEntry entry)
    {
        var resource = group.Target.Resource;
        var values = entry.KeyValues;

        if ((resource == School || resource == EducationOrganization) && values[0] is 255901L)
        {
            return new(SchoolDocumentId, null, Key(School));
        }

        if (resource == EducationOrganization && values[0] is 255902L)
        {
            return new(StudentDocumentId, null, Key(_student));
        }

        if (resource == Program && values[1] is "Career Prep" && IsCareerUri(values[2]))
        {
            return new(ProgramDocumentId, null, Key(Program));
        }

        if (resource == GeneralStudentProgramAssociation && IsCareerUri(values[4]))
        {
            return new(AssociationDocumentId, null, Key(_studentProgramAssociation));
        }

        if (resource == ProgramTypeDescriptor && IsCareerUri(values[0]))
        {
            return new(CareerDescriptorDocumentId, CareerDescriptorId, Key(ProgramTypeDescriptor));
        }

        return null;
    }

    private static bool IsCareerUri(object value) =>
        value is string uri && string.Equals(uri, CareerUri, StringComparison.OrdinalIgnoreCase);

    private short Key(QualifiedResourceName resource) => _mappingSet.ResourceKeyIdByResource[resource];

    private static DocumentReference SchoolReference(string schoolId, string path = "$.schoolReference") =>
        Reference(School, path, ("$.schoolId", schoolId));

    private static DocumentReference ProgramReference(
        string programName,
        string programTypeUri,
        string path = "$.programReference"
    ) =>
        Reference(
            Program,
            path,
            ("$.educationOrganizationReference.educationOrganizationId", "255901"),
            ("$.programName", programName),
            ("$.programTypeDescriptor", programTypeUri)
        );

    private static DocumentReference AssociationReference(string programTypeUri) =>
        Reference(
            GeneralStudentProgramAssociation,
            "$.generalStudentProgramAssociationReference",
            ("$.beginDate", "2026-08-20"),
            ("$.educationOrganizationReference.educationOrganizationId", "255901"),
            ("$.programReference.educationOrganizationId", "255901"),
            ("$.programReference.programName", "Career Prep"),
            ("$.programReference.programTypeDescriptor", programTypeUri),
            ("$.studentReference.studentUniqueId", "S-1")
        );

    private static DocumentReference Reference(
        QualifiedResourceName target,
        string path,
        params (string Path, string Value)[] elements
    ) =>
        new(
            ResourceInfo(target, isDescriptor: false),
            Identity(elements),
            new ReferentialId(Guid.NewGuid()),
            new JsonPath(path)
        );

    private static DescriptorReference DescriptorReference(
        string uri,
        string path = "$.programTypeDescriptor"
    ) =>
        new(
            ResourceInfo(ProgramTypeDescriptor, isDescriptor: true),
            Identity((DocumentIdentity.DescriptorIdentityJsonPath.Value, uri)),
            new ReferentialId(Guid.NewGuid()),
            new JsonPath(path)
        );

    private static BaseResourceInfo ResourceInfo(QualifiedResourceName resource, bool isDescriptor) =>
        new(new ProjectName(resource.ProjectName), new ResourceName(resource.ResourceName), isDescriptor);

    private static DocumentIdentity Identity(params (string Path, string Value)[] elements) =>
        new([
            .. elements.Select(element => new DocumentIdentityElement(
                new JsonPath(element.Path),
                element.Value
            )),
        ]);

    /// <summary>
    /// Records every lookup request it is asked to build, and returns a placeholder command.
    /// </summary>
    private sealed class RecordingLookupCommandBuilder : INaturalKeyLookupCommandBuilder
    {
        public List<NaturalKeyLookupRequest> Requests { get; } = [];

        public RelationalCommand BuildCommand(NaturalKeyLookupRequest request)
        {
            Requests.Add(request);
            return new RelationalCommand("SELECT 1");
        }

        public RelationalCommand BuildEmbeddableCommand(NaturalKeyLookupRequest request) =>
            throw new InvalidOperationException(
                "The standalone resolver path never builds the embeddable form."
            );
    }

    /// <summary>
    /// Executes the last recorded lookup against the fake store, one result set per group, so the real result
    /// reader decodes it.
    /// </summary>
    private sealed class FakeLookupExecutor(
        RecordingLookupCommandBuilder builder,
        Func<NaturalKeyLookupGroup, NaturalKeyLookupEntry, NaturalKeyLookupMatch?> findStoredRow
    ) : IRelationalCommandExecutor
    {
        public int CommandCount { get; private set; }

        public SqlDialect Dialect => SqlDialect.Pgsql;

        public Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            CommandCount++;

            var resultSets = builder
                .Requests[^1]
                .Groups.Select(group =>
                    InMemoryRelationalResultSet.Create([
                        .. group
                            .Entries.Select(
                                (entry, index) => (Match: findStoredRow(group, entry), Ordinal: index + 1)
                            )
                            .Where(row => row.Match is not null)
                            .Select(row => Row(group.GroupOrdinal, row.Ordinal, row.Match!)),
                    ])
                )
                .ToArray();

            return readAsync(new InMemoryRelationalCommandReader(resultSets), cancellationToken);
        }

        private static Dictionary<string, object?> Row(
            int groupOrdinal,
            int ordinal,
            NaturalKeyLookupMatch match
        ) =>
            new()
            {
                [NaturalKeyLookupColumns.GroupOrdinal] = groupOrdinal,
                [NaturalKeyLookupColumns.Ordinal] = ordinal,
                [NaturalKeyLookupColumns.DocumentId] = match.DocumentId,
                [NaturalKeyLookupColumns.DescriptorId] = match.DescriptorId,
                [NaturalKeyLookupColumns.ResourceKeyId] = match.ResourceKeyId,
            };
    }
}
