// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Runs the natural-key lookup builders against a database provisioned from the authoritative DS 5.2 DDL, through
/// the builder, a real command executor and <see cref="NaturalKeyLookupResultReader"/>, and then through
/// <see cref="NaturalKeyReferenceResolver"/>.
/// </summary>
/// <remarks>
/// Seeding uses raw SQL with every trigger enabled, so the generated triggers maintain the abstract identity
/// tables. Descriptor and document keys are seeded explicitly so they never coincide, and a decoy descriptor's
/// <c>DescriptorId</c> equals the real descriptor's <c>DocumentId</c>: a join that compared a stored descriptor
/// reference to <c>DocumentId</c> would match the decoy Program instead of missing.
/// </remarks>
public abstract class NaturalKeyLookupDatabaseTests
{
    private const string FixtureRelativePath = "src/dms/backend/Fixtures/authoritative/ds-5.2";

    private const long SchoolId = 255901;
    private const long UnknownSchoolId = 999999;
    private const string ProgramName = "Career Prep";
    private const string DecoyProgramName = "Decoy Program";
    private const string StudentUniqueId = "S-1";
    private const string ProgramTypeNamespace = "uri://ed-fi.org/ProgramTypeDescriptor";
    private const string CareerCodeValue = "Career and Technical Education";
    private const string DecoyCodeValue = "Decoy";
    private const string CareerUri = $"{ProgramTypeNamespace}#{CareerCodeValue}";
    private const string DecoyUri = $"{ProgramTypeNamespace}#{DecoyCodeValue}";

    private const long CareerDescriptorDocumentId = 900001;
    private const int CareerDescriptorId = 7;
    private const long DecoyDescriptorDocumentId = 900002;

    // Equal to the career descriptor's DocumentId, so a DocumentId-based join would reach the decoy.
    private const int DecoyDescriptorId = (int)CareerDescriptorDocumentId;
    private const long OtherTypeDescriptorDocumentId = 900003;
    private const int OtherTypeDescriptorId = 8;

    // Below the explicit range: SQL Server's IDENTITY_INSERT advances the counter past 900003, so seeded documents
    // take the ids after it.
    private const long DuplicateDescriptorDocumentId = 800001;
    private const int DuplicateDescriptorId = 9;

    private static readonly DateOnly _beginDate = new(2026, 8, 20);

    private static readonly QualifiedResourceName _school = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _educationOrganization = new(
        "Ed-Fi",
        "EducationOrganization"
    );
    private static readonly QualifiedResourceName _program = new("Ed-Fi", "Program");
    private static readonly QualifiedResourceName _student = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _studentProgramAssociation = new(
        "Ed-Fi",
        "StudentProgramAssociation"
    );
    private static readonly QualifiedResourceName _generalStudentProgramAssociation = new(
        "Ed-Fi",
        "GeneralStudentProgramAssociation"
    );
    private static readonly QualifiedResourceName _programTypeDescriptor = new(
        "Ed-Fi",
        "ProgramTypeDescriptor"
    );
    private static readonly QualifiedResourceName _academicSubjectDescriptor = new(
        "Ed-Fi",
        "AcademicSubjectDescriptor"
    );

    private IAsyncDisposable _database = null!;
    private MappingSet _mappingSet = null!;
    private INaturalKeyLookupCommandBuilder _builder = null!;
    private DbConnection _connection = null!;
    private DbTransaction _transaction = null!;

    private long _schoolDocumentId;
    private long _programDocumentId;
    private long _decoyProgramDocumentId;
    private long _associationDocumentId;

    protected abstract SqlDialect Dialect { get; }

    protected abstract DbConnection CreateConnection();

    /// <summary>
    /// Provisions a dedicated database from <see cref="FixtureRelativePath"/> and returns it with the mapping set
    /// compiled from the same DDL.
    /// </summary>
    protected abstract Task<(IAsyncDisposable Database, MappingSet MappingSet)> ProvisionAsync(
        string fixtureRelativePath
    );

    private protected abstract INaturalKeyLookupCommandBuilder CreateBuilder();

    private bool IsPgsql => Dialect == SqlDialect.Pgsql;

    private string Q(string name) => IsPgsql ? $"\"{name}\"" : $"[{name}]";

    private string Table(string schema, string name) => $"{Q(schema)}.{Q(name)}";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        (_database, _mappingSet) = await ProvisionAsync(FixtureRelativePath);
        _builder = CreateBuilder();

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await SeedAsync(connection);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [SetUp]
    public async Task SetUp()
    {
        _connection = CreateConnection();
        await _connection.OpenAsync();
        _transaction = await _connection.BeginTransactionAsync();
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    [Test]
    public async Task It_seeds_descriptors_whose_descriptor_and_document_keys_differ()
    {
        var rows = await RowsAsync(
            _connection,
            _transaction,
            $"SELECT {Q("DescriptorId")}, {Q("DocumentId")} FROM {Table("dms", "Descriptor")};"
        );

        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(row => Convert.ToInt64(row[0]) != Convert.ToInt64(row[1]));
    }

    [Test]
    public async Task It_matches_a_concrete_scalar_identity_and_returns_null_for_a_miss()
    {
        var matches = await LookupAsync(Group(1, ProbeTarget(_school), Key(SchoolId), Key(UnknownSchoolId)));

        matches[0][0].Should().Be(new NaturalKeyLookupMatch(_schoolDocumentId, null, ResourceKeyId(_school)));
        matches[0][1].Should().BeNull();
    }

    [Test]
    public async Task It_projects_the_concrete_member_key_for_an_abstract_target()
    {
        var matches = await LookupAsync(Group(1, ProbeTarget(_educationOrganization), Key(SchoolId)));

        matches[0][0].Should().Be(new NaturalKeyLookupMatch(_schoolDocumentId, null, ResourceKeyId(_school)));
        ResourceKeyId(_school).Should().NotBe(ResourceKeyId(_educationOrganization));
    }

    [Test]
    public async Task It_returns_both_descriptor_keys_in_their_own_slots_and_folds_case_in_the_engine()
    {
        var matches = await LookupAsync(
            Group(
                1,
                DescriptorTarget(_programTypeDescriptor),
                Key(CareerUri),
                Key(CareerUri.ToUpperInvariant()),
                Key($"{ProgramTypeNamespace}#Missing")
            ),
            Group(2, DescriptorTarget(_academicSubjectDescriptor), Key(CareerUri))
        );

        var career = new NaturalKeyLookupMatch(
            CareerDescriptorDocumentId,
            CareerDescriptorId,
            ResourceKeyId(_programTypeDescriptor)
        );
        matches[0][0].Should().Be(career);
        matches[0][1].Should().Be(career);
        matches[0][2].Should().BeNull();

        // The same URI stored under another descriptor type resolves only through that type's key.
        matches[1]
            [0]
            .Should()
            .Be(
                new NaturalKeyLookupMatch(
                    OtherTypeDescriptorDocumentId,
                    OtherTypeDescriptorId,
                    ResourceKeyId(_academicSubjectDescriptor)
                )
            );
    }

    [Test]
    public async Task It_matches_a_descriptor_valued_part_through_the_compact_descriptor_id()
    {
        var matches = await LookupAsync(
            Group(
                1,
                ProbeTarget(_program),
                ProgramEntry(ProgramName, CareerUri),
                ProgramEntry(ProgramName, CareerUri.ToLowerInvariant()),
                ProgramEntry(DecoyProgramName, CareerUri),
                ProgramEntry(DecoyProgramName, DecoyUri)
            )
        );

        var program = new NaturalKeyLookupMatch(_programDocumentId, null, ResourceKeyId(_program));
        matches[0][0].Should().Be(program);
        matches[0][1].Should().Be(program);

        // The decoy Program stores DescriptorId 900001, which is the career descriptor's DocumentId.
        matches[0][2].Should().BeNull();
        matches[0]
            [3]
            .Should()
            .Be(new NaturalKeyLookupMatch(_decoyProgramDocumentId, null, ResourceKeyId(_program)));
    }

    [Test]
    public async Task It_matches_a_copied_descriptor_identity_through_an_abstract_target()
    {
        var matches = await LookupAsync(
            Group(
                1,
                ProbeTarget(_generalStudentProgramAssociation),
                AssociationEntry(CareerUri),
                AssociationEntry(DecoyUri)
            )
        );

        matches[0]
            [0]
            .Should()
            .Be(
                new NaturalKeyLookupMatch(
                    _associationDocumentId,
                    null,
                    ResourceKeyId(_studentProgramAssociation)
                )
            );
        matches[0][1].Should().BeNull();
    }

    [Test]
    public async Task It_returns_the_same_matches_from_the_union_and_the_multi_statement_forms()
    {
        var request = Request(
            Group(1, ProbeTarget(_school), Key(UnknownSchoolId)),
            Group(2, ProbeTarget(_program), ProgramEntry(ProgramName, CareerUri)),
            Group(3, DescriptorTarget(_programTypeDescriptor), Key(CareerUri), Key(DecoyUri)),
            Group(4, ProbeTarget(_educationOrganization), Key(SchoolId), Key(UnknownSchoolId))
        );

        var union = await ExecuteAsync(
            _builder.BuildEmbeddableCommand(request),
            async (reader, cancellationToken) =>
            {
                var matches = NaturalKeyLookupResultReader.CreateMatchTable(request);
                await NaturalKeyLookupResultReader.ReadCurrentResultSetAsync(
                    reader,
                    request,
                    matches,
                    cancellationToken
                );
                return matches;
            }
        );
        var multiStatement = await ExecuteAsync(
            _builder.BuildCommand(request),
            (reader, cancellationToken) =>
                NaturalKeyLookupResultReader.ReadAllResultSetsAsync(reader, request, cancellationToken)
        );

        union[0].Should().ContainSingle().Which.Should().BeNull();
        union[1]
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(new NaturalKeyLookupMatch(_programDocumentId, null, ResourceKeyId(_program)));
        union[2].Should().HaveCount(2).And.OnlyContain(match => match != null);
        union[3][0]!.DocumentId.Should().Be(_schoolDocumentId);
        union[3][1].Should().BeNull();
        multiStatement.Should().BeEquivalentTo(union, options => options.WithStrictOrdering());
    }

    [Test]
    public async Task It_decodes_an_embedded_lookup_and_the_statement_after_it_on_a_real_session()
    {
        var request = Request(
            Group(1, ProbeTarget(_school), Key(SchoolId), Key(UnknownSchoolId)),
            Group(2, DescriptorTarget(_programTypeDescriptor), Key(CareerUri))
        );
        IRelationalCompositeCommandDialect compositeDialect = IsPgsql
            ? PgsqlCompositeCommandDialect.Instance
            : MssqlCompositeCommandDialect.Instance;
        var compositeBuilder = new RelationalCompositeCommandBuilder(compositeDialect);
        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            _builder.BuildEmbeddableCommand(request),
            compositeBuilder.Allocator,
            compositeBuilder.NextOrdinal
        );
        var lookupOrdinal = compositeBuilder.Append(
            "natural-key-lookup",
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            (reader, cancellationToken) =>
                NaturalKeyLookupResultReader.ReadCompositeResultSetAsync(reader, request, cancellationToken)
        );
        var markerOrdinal = compositeBuilder.Append(
            "marker",
            $"SELECT 7 AS {Q("Marker")}",
            [],
            RelationalCompositeResultShape.Rows,
            static async (reader, cancellationToken) =>
                await reader.ReadAsync(cancellationToken) ? reader.GetInt32(0) : null
        );

        var outcomes = await new RelationalCompositeCommandExecution().ExecuteAsync(
            new RelationalWriteSession(_connection, _transaction),
            compositeBuilder.Seal()
        );

        var matches = (NaturalKeyLookupMatch?[][])
            outcomes.Single(outcome => outcome.Ordinal == lookupOrdinal).Value!;
        matches[0][0].Should().Be(new NaturalKeyLookupMatch(_schoolDocumentId, null, ResourceKeyId(_school)));
        matches[0][1].Should().BeNull();
        matches[1]
            [0]
            .Should()
            .Be(
                new NaturalKeyLookupMatch(
                    CareerDescriptorDocumentId,
                    CareerDescriptorId,
                    ResourceKeyId(_programTypeDescriptor)
                )
            );
        outcomes.Single(outcome => outcome.Ordinal == markerOrdinal).Value.Should().Be(7);
    }

    [Test]
    public async Task It_resolves_a_concrete_school_reference()
    {
        var result = await ResolveAsync([SchoolReference(SchoolId)], []);

        result.HasFailures.Should().BeFalse();
        var resolved = result.SuccessfulDocumentReferencesByPath[new JsonPath("$.schoolReference")];
        resolved.DocumentId.Should().Be(_schoolDocumentId);
        resolved.ResourceKeyId.Should().Be(ResourceKeyId(_school));
    }

    [Test]
    public async Task It_resolves_an_abstract_reference_to_the_concrete_member()
    {
        var result = await ResolveAsync([EducationOrganizationReference(SchoolId)], []);

        var resolved = result.SuccessfulDocumentReferencesByPath[
            new JsonPath("$.educationOrganizationReference")
        ];
        resolved.DocumentId.Should().Be(_schoolDocumentId);
        resolved.ResourceKeyId.Should().Be(ResourceKeyId(_school));
    }

    [Test]
    public async Task It_retains_unequal_descriptor_and_document_ids_for_a_descriptor_reference()
    {
        var result = await ResolveAsync(
            [],
            [DescriptorReference(_programTypeDescriptor, CareerUri.ToUpperInvariant())]
        );

        var resolved = result.SuccessfulDescriptorReferencesByPath[new JsonPath("$.programTypeDescriptor")];
        resolved.DescriptorId.Should().Be(CareerDescriptorId);
        resolved.DocumentId.Should().Be(CareerDescriptorDocumentId);
        resolved.ResourceKeyId.Should().Be(ResourceKeyId(_programTypeDescriptor));
    }

    [Test]
    public async Task It_follows_the_engine_verdict_for_a_uri_longer_than_any_stored_one()
    {
        // Longer than any stored URI only by emoji, which SQL Server's identity collation ignores.
        var aliasUri = CareerUri + string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F600), 240));

        var result = await ResolveAsync(
            [ProgramReference(ProgramName, aliasUri, "$.programReference")],
            [DescriptorReference(_programTypeDescriptor, aliasUri)]
        );

        if (IsPgsql)
        {
            result.SuccessfulDocumentReferencesByPath.Should().BeEmpty();
            result.SuccessfulDescriptorReferencesByPath.Should().BeEmpty();
            return;
        }

        result.HasFailures.Should().BeFalse();
        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.programReference")]
            .DocumentId.Should()
            .Be(_programDocumentId);
        result
            .SuccessfulDescriptorReferencesByPath[new JsonPath("$.programTypeDescriptor")]
            .DescriptorId.Should()
            .Be(CareerDescriptorId);
    }

    [Test]
    public async Task It_resolves_a_descriptor_valued_identity_through_the_compact_descriptor_id()
    {
        var result = await ResolveAsync(
            [
                ProgramReference(ProgramName, CareerUri, "$.programReference"),
                ProgramReference(DecoyProgramName, CareerUri, "$.decoyProgramReference"),
            ],
            []
        );

        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.programReference")]
            .DocumentId.Should()
            .Be(_programDocumentId);

        // The decoy stores DescriptorId 900001, the career descriptor's DocumentId, so only a DocumentId join hits.
        var failure = result.InvalidDocumentReferences.Should().ContainSingle().Subject;
        failure.Path.Value.Should().Be("$.decoyProgramReference");
        failure.Reason.Should().Be(DocumentReferenceFailureReason.Missing);
    }

    [Test]
    public async Task It_resolves_a_copied_descriptor_identity_through_an_abstract_target()
    {
        var result = await ResolveAsync([AssociationReference(CareerUri)], []);

        var resolved = result.SuccessfulDocumentReferencesByPath[
            new JsonPath("$.generalStudentProgramAssociationReference")
        ];
        resolved.DocumentId.Should().Be(_associationDocumentId);
        resolved.ResourceKeyId.Should().Be(ResourceKeyId(_studentProgramAssociation));
    }

    [Test]
    public async Task It_classifies_unknown_references_and_a_uri_under_the_wrong_type_as_missing()
    {
        var result = await ResolveAsync(
            [SchoolReference(UnknownSchoolId)],
            [
                DescriptorReference(
                    _programTypeDescriptor,
                    $"{ProgramTypeNamespace}#Unknown",
                    "$.unknownDescriptor"
                ),
                // The decoy URI is stored only as a ProgramTypeDescriptor.
                DescriptorReference(_academicSubjectDescriptor, DecoyUri, "$.academicSubjectDescriptor"),
            ]
        );

        result
            .InvalidDocumentReferences.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(DocumentReferenceFailureReason.Missing);
        result
            .InvalidDescriptorReferences.Select(failure => failure.Path.Value)
            .Should()
            .BeEquivalentTo("$.unknownDescriptor", "$.academicSubjectDescriptor");
        result
            .InvalidDescriptorReferences.Should()
            .OnlyContain(failure => failure.Reason == DescriptorReferenceFailureReason.Missing);
    }

    [Test]
    public async Task It_classifies_an_identity_row_owned_by_another_resource_as_an_incompatible_target()
    {
        const long studentEducationOrganizationId = 255999;
        var studentDocumentId = await InsertDocumentAsync(_connection, _student, _transaction);
        await ExecuteAsync(
            _connection,
            _transaction,
            $"INSERT INTO {Table("edfi", "EducationOrganizationIdentity")} ({Q("DocumentId")}, {Q("EducationOrganizationId")}, {Q("ResourceKeyId")}, {Q("Discriminator")}) VALUES (@documentId, @educationOrganizationId, @key, 'Ed-Fi:Student');",
            ("documentId", studentDocumentId),
            ("educationOrganizationId", studentEducationOrganizationId),
            ("key", ResourceKeyId(_student))
        );

        var result = await ResolveAsync([EducationOrganizationReference(studentEducationOrganizationId)], []);

        result.SuccessfulDocumentReferencesByPath.Should().BeEmpty();
        result
            .InvalidDocumentReferences.Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be(DocumentReferenceFailureReason.IncompatibleTargetType);
    }

    [Test]
    public async Task It_resolves_a_large_deduped_lookup_set_without_losing_repeated_missing_path_diagnostics()
    {
        const int LargeLookupCount = 4096;
        const long FirstBulkSchoolId = 300000;
        var documentIdBySchoolId = await SeedBulkSchoolsAsync(FirstBulkSchoolId, LargeLookupCount);

        DocumentReference[] references =
        [
            .. Enumerable
                .Range(0, LargeLookupCount)
                .Select(index =>
                    SchoolReference(FirstBulkSchoolId + index, $"$.bulkSchools[{index}].schoolReference")
                ),
            .. Enumerable
                .Range(0, 3)
                .Select(index =>
                    SchoolReference(UnknownSchoolId, $"$.missingSchools[{index}].schoolReference")
                ),
        ];

        var result = await ResolveAsync(references, []);

        result
            .SuccessfulDocumentReferencesByPath.ToDictionary(
                resolved => resolved.Key.Value,
                resolved => resolved.Value.DocumentId
            )
            .Should()
            .Equal(
                Enumerable
                    .Range(0, LargeLookupCount)
                    .ToDictionary(
                        index => $"$.bulkSchools[{index}].schoolReference",
                        index => documentIdBySchoolId[FirstBulkSchoolId + index]
                    )
            );
        result
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.missingSchools[0].schoolReference", DocumentReferenceFailureReason.Missing),
                ("$.missingSchools[1].schoolReference", DocumentReferenceFailureReason.Missing),
                ("$.missingSchools[2].schoolReference", DocumentReferenceFailureReason.Missing)
            );

        // The repeated missing identity is probed once; its three paths are classified from that one miss.
        var entries = CreateResolver()
            .TryBuildLookupRequest(ResolverRequest(references, []))!
            .LookupRequest!.Groups.Should()
            .ContainSingle()
            .Subject.Entries;
        entries.Should().HaveCount(LargeLookupCount + 1);
        entries
            .Count(entry =>
                Convert.ToInt64(entry.KeyValues.Single(), CultureInfo.InvariantCulture) == UnknownSchoolId
            )
            .Should()
            .Be(1);
    }

    [Test]
    public async Task It_rejects_a_descriptor_uri_that_matches_more_than_one_row()
    {
        var descriptor = Table("dms", "Descriptor");
        await ExecuteAsync(
            _connection,
            _transaction,
            IsPgsql
                ? """
                DROP INDEX "dms"."UX_Descriptor_UriLowered_ResourceKeyId";
                DROP INDEX "dms"."UX_Descriptor_ResourceKeyId_Uri";
                """
                : $"""
                DROP INDEX [UX_Descriptor_UriLowered_ResourceKeyId] ON {descriptor};
                DROP INDEX [UX_Descriptor_ResourceKeyId_Uri] ON {descriptor};
                """
        );
        await InsertDescriptorAsync(
            _connection,
            DuplicateDescriptorDocumentId,
            DuplicateDescriptorId,
            _programTypeDescriptor,
            ProgramTypeNamespace.ToUpperInvariant(),
            CareerCodeValue,
            _transaction
        );

        var act = () => ResolveAsync([], [DescriptorReference(_programTypeDescriptor, CareerUri)]);

        await act.Should().ThrowAsync<ReferenceLookupCorruptionException>();
    }

    [Test]
    public async Task It_probes_a_descriptor_post_target_to_its_owning_document_with_the_stored_content_version()
    {
        // The seeded DescriptorId differs from the owning DocumentId, so returning DescriptorId would fail here.
        (await ProbeDescriptorTargetAsync(_programTypeDescriptor, CareerUri))
            .Should()
            .BeEquivalentTo(await CareerDescriptorTargetAsync());
    }

    [Test]
    public async Task It_probes_a_case_variant_descriptor_uri_to_the_same_document_on_both_engines()
    {
        (await ProbeDescriptorTargetAsync(_programTypeDescriptor, CareerUri.ToUpperInvariant()))
            .Should()
            .BeEquivalentTo(await CareerDescriptorTargetAsync());
    }

    [Test]
    public async Task It_creates_new_when_the_descriptor_uri_is_stored_only_under_another_descriptor_type()
    {
        var candidate = new DocumentUuid(Guid.NewGuid());

        (await ProbeDescriptorTargetAsync(_academicSubjectDescriptor, DecoyUri, candidate))
            .Should()
            .BeEquivalentTo(new RelationalWriteTargetLookupResult.CreateNew(candidate));
    }

    [Test]
    public async Task It_creates_new_with_the_candidate_uuid_when_no_descriptor_matches()
    {
        var candidate = new DocumentUuid(Guid.NewGuid());

        (
            await ProbeDescriptorTargetAsync(
                _programTypeDescriptor,
                $"{ProgramTypeNamespace}#Missing",
                candidate
            )
        )
            .Should()
            .BeEquivalentTo(new RelationalWriteTargetLookupResult.CreateNew(candidate));
    }

    [Test]
    public async Task It_rejects_a_descriptor_post_target_that_matches_more_than_one_row()
    {
        var descriptor = Table("dms", "Descriptor");
        await ExecuteAsync(
            _connection,
            _transaction,
            IsPgsql
                ? """
                DROP INDEX "dms"."UX_Descriptor_UriLowered_ResourceKeyId";
                DROP INDEX "dms"."UX_Descriptor_ResourceKeyId_Uri";
                """
                : $"""
                DROP INDEX [UX_Descriptor_UriLowered_ResourceKeyId] ON {descriptor};
                DROP INDEX [UX_Descriptor_ResourceKeyId_Uri] ON {descriptor};
                """
        );
        await InsertDescriptorAsync(
            _connection,
            DuplicateDescriptorDocumentId,
            DuplicateDescriptorId,
            _programTypeDescriptor,
            ProgramTypeNamespace.ToUpperInvariant(),
            CareerCodeValue,
            _transaction
        );

        var act = () => ProbeDescriptorTargetAsync(_programTypeDescriptor, CareerUri);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*multiple rows*");
    }

    [Test]
    public async Task It_captures_the_stored_document_for_a_scalar_own_natural_key()
    {
        (
            await CaptureAsync(
                _school,
                CaptureIdentity(("$.schoolId", SchoolId.ToString(CultureInfo.InvariantCulture)))
            )
        )
            .Should()
            .Equal(_schoolDocumentId);
    }

    [Test]
    public async Task It_captures_through_the_compact_descriptor_id_and_the_abstract_reference_target()
    {
        // The decoy Program stores a DescriptorId equal to the career descriptor's DocumentId, so a capture that
        // compared DocumentId would reach it instead.
        (await CaptureAsync(_program, CaptureProgramIdentity(ProgramName, CareerUri)))
            .Should()
            .Equal(_programDocumentId);
    }

    [Test]
    public async Task It_captures_nothing_when_no_stored_row_has_the_identity()
    {
        (await CaptureAsync(_program, CaptureProgramIdentity("Unknown Program", CareerUri)))
            .Should()
            .BeEmpty();
        (await CaptureAsync(_program, CaptureProgramIdentity(ProgramName, DecoyUri))).Should().BeEmpty();
    }

    [Test]
    public async Task It_folds_descriptor_uri_case_on_both_engines()
    {
        (await CaptureAsync(_program, CaptureProgramIdentity(ProgramName, CareerUri.ToUpperInvariant())))
            .Should()
            .Equal(_programDocumentId);
    }

    [Test]
    public async Task It_follows_the_engine_identity_collation_for_a_case_variant_scalar()
    {
        var captured = await CaptureAsync(
            _program,
            CaptureProgramIdentity(ProgramName.ToUpperInvariant(), CareerUri)
        );

        // SQL Server identity columns use the case-insensitive identity collation; PostgreSQL's are exact.
        if (IsPgsql)
        {
            captured.Should().BeEmpty();
        }
        else
        {
            captured.Should().Equal(_programDocumentId);
        }
    }

    [Test]
    public async Task It_fails_the_capture_when_the_natural_key_admits_more_than_one_row()
    {
        await ExecuteAsync(
            _connection,
            _transaction,
            $"""
            ALTER TABLE {Table("edfi", "Program")} DROP CONSTRAINT {Q("UX_Program_NK")};
            DELETE FROM {Table("dms", "ReferentialIdentity")} WHERE {Q("DocumentId")} = @documentId;
            """,
            // The duplicate's trigger-maintained referential id equals the original's.
            ("documentId", _programDocumentId)
        );
        await InsertProgramAsync(_connection, ProgramName, CareerDescriptorId, _transaction);

        var act = () => CaptureAsync(_program, CaptureProgramIdentity(ProgramName, CareerUri));

        var exception = (await act.Should().ThrowAsync<DbException>()).Which;

        // A multi-row scalar subquery: PostgreSQL SQLSTATE 21000, SQL Server error 512. Never a silent pick.
        if (IsPgsql)
        {
            exception.SqlState.Should().Be("21000");
        }
        else
        {
            exception.GetType().GetProperty("Number")!.GetValue(exception).Should().Be(512);
        }
    }

    [Test]
    public async Task It_resolves_from_rows_decoded_out_of_a_composite_command_on_a_real_session()
    {
        DocumentReference[] documents =
        [
            SchoolReference(SchoolId),
            ProgramReference(ProgramName, CareerUri, "$.programReference"),
            SchoolReference(UnknownSchoolId, "$.unknownSchoolReference"),
        ];
        DescriptorReference[] descriptors = [DescriptorReference(_programTypeDescriptor, CareerUri)];
        var request = ResolverRequest(documents, descriptors);
        var resolver = CreateResolver();
        var batch = resolver.TryBuildLookupRequest(request)!;
        var lookupRequest = batch.LookupRequest!;

        IRelationalCompositeCommandDialect compositeDialect = IsPgsql
            ? PgsqlCompositeCommandDialect.Instance
            : MssqlCompositeCommandDialect.Instance;
        var compositeBuilder = new RelationalCompositeCommandBuilder(compositeDialect);
        var rewritten = RelationalCompositeStatementRewriter.Rewrite(
            _builder.BuildEmbeddableCommand(lookupRequest),
            compositeBuilder.Allocator,
            compositeBuilder.NextOrdinal
        );
        var lookupOrdinal = compositeBuilder.Append(
            "natural-key-lookup",
            rewritten.Sql,
            rewritten.Parameters,
            RelationalCompositeResultShape.Rows,
            (reader, cancellationToken) =>
                NaturalKeyLookupResultReader.ReadCompositeResultSetAsync(
                    reader,
                    lookupRequest,
                    cancellationToken
                )
        );
        var markerOrdinal = compositeBuilder.Append(
            "marker",
            $"SELECT 7 AS {Q("Marker")}",
            [],
            RelationalCompositeResultShape.Rows,
            static async (reader, cancellationToken) =>
                await reader.ReadAsync(cancellationToken) ? reader.GetInt32(0) : null
        );

        var outcomes = await new RelationalCompositeCommandExecution().ExecuteAsync(
            new RelationalWriteSession(_connection, _transaction),
            compositeBuilder.Seal()
        );
        var replayed = resolver.ResolveFromRows(
            request,
            batch,
            (NaturalKeyLookupMatch?[][])outcomes.Single(outcome => outcome.Ordinal == lookupOrdinal).Value!
        );
        var standalone = await ResolveAsync(documents, descriptors);

        replayed
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.programReference")]
            .DocumentId.Should()
            .Be(_programDocumentId);
        replayed
            .SuccessfulDescriptorReferencesByPath[new JsonPath("$.programTypeDescriptor")]
            .DescriptorId.Should()
            .Be(CareerDescriptorId);
        replayed
            .InvalidDocumentReferences.Should()
            .ContainSingle()
            .Which.Path.Value.Should()
            .Be("$.unknownSchoolReference");
        replayed
            .SuccessfulDocumentReferencesByPath.Should()
            .BeEquivalentTo(standalone.SuccessfulDocumentReferencesByPath);
        replayed
            .SuccessfulDescriptorReferencesByPath.Should()
            .BeEquivalentTo(standalone.SuccessfulDescriptorReferencesByPath);
        outcomes.Single(outcome => outcome.Ordinal == markerOrdinal).Value.Should().Be(7);
    }

    private NaturalKeyReferenceResolver CreateResolver() =>
        new(_builder, new ResolvedDocumentReferenceMapFactory());

    private Task<ResolvedReferenceSet> ResolveAsync(
        IReadOnlyList<DocumentReference> documents,
        IReadOnlyList<DescriptorReference> descriptors
    )
    {
        IRelationalWriteSession session = new RelationalWriteSession(_connection, _transaction);

        return CreateResolver()
            .ResolveAsync(ResolverRequest(documents, descriptors), session.CreateCommandExecutor());
    }

    private ReferenceResolverRequest ResolverRequest(
        IReadOnlyList<DocumentReference> documents,
        IReadOnlyList<DescriptorReference> descriptors
    ) => new(_mappingSet, _studentProgramAssociation, documents, descriptors);

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static DocumentReference SchoolReference(long schoolId, string path = "$.schoolReference") =>
        DocumentReference(_school, path, ("$.schoolId", Text(schoolId)));

    private static DocumentReference EducationOrganizationReference(long educationOrganizationId) =>
        DocumentReference(
            _educationOrganization,
            "$.educationOrganizationReference",
            ("$.educationOrganizationId", Text(educationOrganizationId))
        );

    private static DocumentReference ProgramReference(
        string programName,
        string programTypeUri,
        string path
    ) =>
        DocumentReference(
            _program,
            path,
            ("$.educationOrganizationReference.educationOrganizationId", Text(SchoolId)),
            ("$.programName", programName),
            ("$.programTypeDescriptor", programTypeUri)
        );

    private static DocumentReference AssociationReference(string programTypeUri) =>
        DocumentReference(
            _generalStudentProgramAssociation,
            "$.generalStudentProgramAssociationReference",
            ("$.beginDate", _beginDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("$.educationOrganizationReference.educationOrganizationId", Text(SchoolId)),
            ("$.programReference.educationOrganizationId", Text(SchoolId)),
            ("$.programReference.programName", ProgramName),
            ("$.programReference.programTypeDescriptor", programTypeUri),
            ("$.studentReference.studentUniqueId", StudentUniqueId)
        );

    private static DocumentReference DocumentReference(
        QualifiedResourceName target,
        string path,
        params (string Path, string Value)[] elements
    ) => new(ResourceInfo(target, isDescriptor: false), Identity(elements), new JsonPath(path));

    private static DescriptorReference DescriptorReference(
        QualifiedResourceName descriptorResource,
        string uri,
        string path = "$.programTypeDescriptor"
    ) =>
        new(
            ResourceInfo(descriptorResource, isDescriptor: true),
            Identity((DocumentIdentity.DescriptorIdentityJsonPath.Value, uri)),
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
    /// Runs the POST capture predicate the way the capture statement embeds it, over <c>dms.Document d</c>,
    /// on the real session executor.
    /// </summary>
    private Task<List<long>> CaptureAsync(QualifiedResourceName resource, DocumentIdentity identity)
    {
        var predicate = RelationalWriteTargetLookupSupport.BuildNaturalKeyCaptureTargetPredicate(
            _mappingSet,
            _mappingSet.WritePlansByResource[resource],
            identity
        );

        return ExecuteAsync(
            new RelationalCommand(
                $"SELECT d.{Q("DocumentId")} FROM {Table("dms", "Document")} d WHERE {predicate.CommandText};",
                predicate.Parameters
            ),
            static async (reader, cancellationToken) =>
            {
                List<long> documentIds = [];

                while (await reader.ReadAsync(cancellationToken))
                {
                    documentIds.Add(reader.GetFieldValue<long>(0));
                }

                return documentIds;
            }
        );
    }

    private Task<RelationalWriteTargetLookupResult> ProbeDescriptorTargetAsync(
        QualifiedResourceName descriptorResource,
        string descriptorUri,
        DocumentUuid? candidate = null
    )
    {
        IRelationalWriteSession session = new RelationalWriteSession(_connection, _transaction);

        return RelationalWriteTargetLookupSupport.ResolveDescriptorForPostAsync(
            session.CreateCommandExecutor(),
            _mappingSet,
            descriptorResource,
            descriptorUri,
            candidate ?? new DocumentUuid(Guid.NewGuid())
        );
    }

    /// <summary>
    /// The career descriptor's owning document as the probe must report it, read by its seeded DocumentId.
    /// </summary>
    private async Task<RelationalWriteTargetLookupResult.ExistingDocument> CareerDescriptorTargetAsync()
    {
        var row = (
            await RowsAsync(
                _connection,
                _transaction,
                $"SELECT {Q("DocumentUuid")}, {Q("ContentVersion")} FROM {Table("dms", "Document")} WHERE {Q("DocumentId")} = @documentId;",
                ("documentId", CareerDescriptorDocumentId)
            )
        ).Single();

        return new(CareerDescriptorDocumentId, new DocumentUuid((Guid)row[0]), Convert.ToInt64(row[1]));
    }

    private static DocumentIdentity CaptureProgramIdentity(string programName, string programTypeUri) =>
        CaptureIdentity(
            (
                "$.educationOrganizationReference.educationOrganizationId",
                SchoolId.ToString(CultureInfo.InvariantCulture)
            ),
            ("$.programName", programName),
            ("$.programTypeDescriptor", programTypeUri)
        );

    private static DocumentIdentity CaptureIdentity(params (string Path, string Value)[] elements) =>
        new([
            .. elements.Select(element => new DocumentIdentityElement(
                new JsonPath(element.Path),
                element.Value
            )),
        ]);

    private Task<NaturalKeyLookupMatch?[][]> LookupAsync(params NaturalKeyLookupGroup[] groups)
    {
        var request = Request(groups);

        return ExecuteAsync(
            _builder.BuildCommand(request),
            (reader, cancellationToken) =>
                NaturalKeyLookupResultReader.ReadAllResultSetsAsync(reader, request, cancellationToken)
        );
    }

    private Task<TResult> ExecuteAsync<TResult>(
        RelationalCommand command,
        Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync
    )
    {
        IRelationalWriteSession session = new RelationalWriteSession(_connection, _transaction);

        return session.CreateCommandExecutor().ExecuteReaderAsync(command, readAsync);
    }

    private NaturalKeyLookupRequest Request(params NaturalKeyLookupGroup[] groups) =>
        new(_mappingSet, groups);

    private static NaturalKeyLookupGroup Group(
        int groupOrdinal,
        NaturalKeyLookupTarget target,
        params IReadOnlyList<object>[] entries
    ) => new(groupOrdinal, target, [.. entries.Select(keyValues => new NaturalKeyLookupEntry(keyValues))]);

    private static IReadOnlyList<object> Key(object value) => [value];

    private NaturalKeyLookupTarget ProbeTarget(QualifiedResourceName resource) =>
        NaturalKeyLookupTarget.ForProbe(
            _mappingSet.NaturalKeyProbeTargets[resource],
            ResourceKeyId(resource)
        );

    private NaturalKeyLookupTarget DescriptorTarget(QualifiedResourceName resource) =>
        NaturalKeyLookupTarget.ForDescriptor(resource, ResourceKeyId(resource));

    private short ResourceKeyId(QualifiedResourceName resource) =>
        _mappingSet.ResourceKeyIdByResource[resource];

    private IReadOnlyList<object> ProgramEntry(string programName, string programTypeUri) =>
        Entry(
            _program,
            new()
            {
                ["$.educationOrganizationReference.educationOrganizationId"] = SchoolId,
                ["$.programName"] = programName,
                ["$.programTypeDescriptor"] = programTypeUri,
            }
        );

    private IReadOnlyList<object> AssociationEntry(string programTypeUri) =>
        Entry(
            _generalStudentProgramAssociation,
            new()
            {
                ["$.beginDate"] = _beginDate,
                ["$.educationOrganizationReference.educationOrganizationId"] = SchoolId,
                ["$.programReference.educationOrganizationId"] = SchoolId,
                ["$.programReference.programName"] = ProgramName,
                ["$.programReference.programTypeDescriptor"] = programTypeUri,
                ["$.studentReference.studentUniqueId"] = StudentUniqueId,
            }
        );

    /// <summary>
    /// Orders values by the compiled probe's key paths, so the test never depends on key positions.
    /// </summary>
    private IReadOnlyList<object> Entry(
        QualifiedResourceName resource,
        Dictionary<string, object> valuesByPath
    )
    {
        var keyColumns = _mappingSet.NaturalKeyProbeTargets[resource].KeyColumns;

        keyColumns
            .Select(keyColumn => keyColumn.IdentityJsonPath.Canonical)
            .Should()
            .BeEquivalentTo(valuesByPath.Keys, resource.ResourceName);

        return [.. keyColumns.Select(keyColumn => valuesByPath[keyColumn.IdentityJsonPath.Canonical])];
    }

    private async Task SeedAsync(DbConnection connection)
    {
        await InsertDescriptorAsync(
            connection,
            CareerDescriptorDocumentId,
            CareerDescriptorId,
            _programTypeDescriptor,
            ProgramTypeNamespace,
            CareerCodeValue
        );
        await InsertDescriptorAsync(
            connection,
            DecoyDescriptorDocumentId,
            DecoyDescriptorId,
            _programTypeDescriptor,
            ProgramTypeNamespace,
            DecoyCodeValue
        );
        await InsertDescriptorAsync(
            connection,
            OtherTypeDescriptorDocumentId,
            OtherTypeDescriptorId,
            _academicSubjectDescriptor,
            ProgramTypeNamespace,
            CareerCodeValue
        );

        _schoolDocumentId = await InsertDocumentAsync(connection, _school);
        await ExecuteAsync(
            connection,
            $"INSERT INTO {Table("edfi", "School")} ({Q("DocumentId")}, {Q("NameOfInstitution")}, {Q("SchoolId")}) VALUES (@documentId, 'Test School', @schoolId);",
            ("documentId", _schoolDocumentId),
            ("schoolId", SchoolId)
        );

        _programDocumentId = await InsertProgramAsync(connection, ProgramName, CareerDescriptorId);
        _decoyProgramDocumentId = await InsertProgramAsync(connection, DecoyProgramName, DecoyDescriptorId);

        var studentDocumentId = await InsertDocumentAsync(connection, _student);
        await ExecuteAsync(
            connection,
            $"INSERT INTO {Table("edfi", "Student")} ({Q("DocumentId")}, {Q("BirthDate")}, {Q("FirstName")}, {Q("LastSurname")}, {Q("StudentUniqueId")}) VALUES (@documentId, @birthDate, 'Ada', 'Lovelace', @studentUniqueId);",
            ("documentId", studentDocumentId),
            ("birthDate", new DateOnly(2010, 1, 1)),
            ("studentUniqueId", StudentUniqueId)
        );

        // The association's triggers copy Program_ProgramTypeDescriptor_DescriptorId into
        // GeneralStudentProgramAssociationIdentity.
        _associationDocumentId = await InsertDocumentAsync(connection, _studentProgramAssociation);
        await ExecuteAsync(
            connection,
            $"""
            INSERT INTO {Table("edfi", "StudentProgramAssociation")} (
                {Q("DocumentId")},
                {Q("EducationOrganization_DocumentId")},
                {Q("EducationOrganization_EducationOrganizationId")},
                {Q("ProgramProgram_DocumentId")},
                {Q("ProgramProgram_EducationOrganizationId")},
                {Q("ProgramProgram_ProgramName")},
                {Q("ProgramProgram_ProgramTypeDescriptor_DescriptorId")},
                {Q("Student_DocumentId")},
                {Q("Student_StudentUniqueId")},
                {Q("BeginDate")}
            )
            VALUES (
                @documentId, @schoolDocumentId, @schoolId, @programDocumentId, @schoolId, @programName,
                @descriptorId, @studentDocumentId, @studentUniqueId, @beginDate
            );
            """,
            ("documentId", _associationDocumentId),
            ("schoolDocumentId", _schoolDocumentId),
            ("schoolId", SchoolId),
            ("programDocumentId", _programDocumentId),
            ("programName", ProgramName),
            ("descriptorId", CareerDescriptorId),
            ("studentDocumentId", studentDocumentId),
            ("studentUniqueId", StudentUniqueId),
            ("beginDate", _beginDate)
        );
    }

    /// <summary>
    /// Seeds <paramref name="count"/> schools with consecutive ids inside the test transaction, set-based so the
    /// seed stays two statements however large the batch is, and returns each school's document id.
    /// </summary>
    private async Task<Dictionary<long, long>> SeedBulkSchoolsAsync(long firstSchoolId, int count)
    {
        var document = Table("dms", "Document");
        var school = Table("edfi", "School");
        var documents = IsPgsql
            ? "SELECT gen_random_uuid(), @key FROM generate_series(1, @count)"
            : "SELECT TOP (@count) NEWID(), @key FROM sys.all_columns a CROSS JOIN sys.all_columns b";

        await ExecuteAsync(
            _connection,
            _transaction,
            $"""
            INSERT INTO {document} ({Q("DocumentUuid")}, {Q("ResourceKeyId")}) {documents};
            INSERT INTO {school} ({Q("DocumentId")}, {Q("NameOfInstitution")}, {Q("SchoolId")})
            SELECT d.{Q("DocumentId")}, 'Bulk School', @firstSchoolId - 1 + ROW_NUMBER() OVER (ORDER BY d.{Q(
                "DocumentId"
            )})
            FROM {document} d
            WHERE d.{Q("ResourceKeyId")} = @key
                AND NOT EXISTS (SELECT 1 FROM {school} s WHERE s.{Q("DocumentId")} = d.{Q("DocumentId")});
            """,
            ("key", ResourceKeyId(_school)),
            ("count", count),
            ("firstSchoolId", firstSchoolId)
        );

        var rows = await RowsAsync(
            _connection,
            _transaction,
            $"SELECT {Q("SchoolId")}, {Q("DocumentId")} FROM {school} WHERE {Q("SchoolId")} >= @firstSchoolId;",
            ("firstSchoolId", firstSchoolId)
        );
        rows.Should().HaveCount(count);

        return rows.ToDictionary(row => Convert.ToInt64(row[0]), row => Convert.ToInt64(row[1]));
    }

    private async Task<long> InsertProgramAsync(
        DbConnection connection,
        string programName,
        int descriptorId,
        DbTransaction? transaction = null
    )
    {
        var documentId = await InsertDocumentAsync(connection, _program, transaction);
        await ExecuteAsync(
            connection,
            transaction,
            $"INSERT INTO {Table("edfi", "Program")} ({Q("DocumentId")}, {Q("EducationOrganization_DocumentId")}, {Q("EducationOrganization_EducationOrganizationId")}, {Q("ProgramTypeDescriptor_DescriptorId")}, {Q("ProgramName")}) VALUES (@documentId, @schoolDocumentId, @schoolId, @descriptorId, @programName);",
            ("documentId", documentId),
            ("schoolDocumentId", _schoolDocumentId),
            ("schoolId", SchoolId),
            ("descriptorId", descriptorId),
            ("programName", programName)
        );
        return documentId;
    }

    private async Task<long> InsertDocumentAsync(
        DbConnection connection,
        QualifiedResourceName resource,
        DbTransaction? transaction = null
    )
    {
        var insert = $"INSERT INTO {Table("dms", "Document")} ({Q("DocumentUuid")}, {Q("ResourceKeyId")})";
        var sql = IsPgsql
            ? $"{insert} VALUES (@uuid, @key) RETURNING {Q("DocumentId")};"
            : $"DECLARE @ids table (Id bigint); {insert} OUTPUT inserted.DocumentId INTO @ids VALUES (@uuid, @key); SELECT Id FROM @ids;";

        var rows = await RowsAsync(
            connection,
            transaction,
            sql,
            ("uuid", Guid.NewGuid()),
            ("key", ResourceKeyId(resource))
        );
        return Convert.ToInt64(rows.Single()[0]);
    }

    private async Task InsertDescriptorAsync(
        DbConnection connection,
        long documentId,
        int descriptorId,
        QualifiedResourceName descriptorResource,
        string ns,
        string codeValue,
        DbTransaction? transaction = null
    )
    {
        var document = Table("dms", "Document");
        var descriptor = Table("dms", "Descriptor");
        var insertDocument =
            $"INSERT INTO {document} ({Q("DocumentId")}, {Q("DocumentUuid")}, {Q("ResourceKeyId")})";
        var insertDescriptor =
            $"INSERT INTO {descriptor} ({Q("DescriptorId")}, {Q("DocumentId")}, {Q("ResourceKeyId")}, {Q("Namespace")}, {Q("CodeValue")}, {Q("ShortDescription")})";
        var sql = IsPgsql
            ? $"""
                {insertDocument} OVERRIDING SYSTEM VALUE VALUES (@documentId, @uuid, @key);
                {insertDescriptor} OVERRIDING SYSTEM VALUE VALUES (@descriptorId, @documentId, @key, @ns, @codeValue, 'Description');
                """
            : $"""
                SET IDENTITY_INSERT {document} ON;
                {insertDocument} VALUES (@documentId, @uuid, @key);
                SET IDENTITY_INSERT {document} OFF;
                SET IDENTITY_INSERT {descriptor} ON;
                {insertDescriptor} VALUES (@descriptorId, @documentId, @key, @ns, @codeValue, N'Description');
                SET IDENTITY_INSERT {descriptor} OFF;
                """;

        await ExecuteAsync(
            connection,
            transaction,
            sql,
            ("documentId", documentId),
            ("uuid", Guid.NewGuid()),
            ("key", ResourceKeyId(descriptorResource)),
            ("descriptorId", descriptorId),
            ("ns", ns),
            ("codeValue", codeValue)
        );
    }

    private static Task ExecuteAsync(
        DbConnection connection,
        string sql,
        params (string Name, object Value)[] values
    ) => ExecuteAsync(connection, null, sql, values);

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        params (string Name, object Value)[] values
    )
    {
        await using var command = CreateCommand(connection, transaction, sql, values);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<object[]>> RowsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        params (string Name, object Value)[] values
    )
    {
        await using var command = CreateCommand(connection, transaction, sql, values);
        await using var reader = await command.ExecuteReaderAsync();
        List<object[]> rows = [];

        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }

        return rows;
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        (string Name, object Value)[] values
    )
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 60;

        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
