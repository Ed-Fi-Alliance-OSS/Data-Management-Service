// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.Composite;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Runs the natural-key lookup builders against a database provisioned from the authoritative DS 5.2 DDL, through
/// the builder, a real command executor and <see cref="NaturalKeyLookupResultReader"/>, with no resolver.
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

    private async Task<long> InsertProgramAsync(DbConnection connection, string programName, int descriptorId)
    {
        var documentId = await InsertDocumentAsync(connection, _program);
        await ExecuteAsync(
            connection,
            $"INSERT INTO {Table("edfi", "Program")} ({Q("DocumentId")}, {Q("EducationOrganization_DocumentId")}, {Q("EducationOrganization_EducationOrganizationId")}, {Q("ProgramTypeDescriptor_DescriptorId")}, {Q("ProgramName")}) VALUES (@documentId, @schoolDocumentId, @schoolId, @descriptorId, @programName);",
            ("documentId", documentId),
            ("schoolDocumentId", _schoolDocumentId),
            ("schoolId", SchoolId),
            ("descriptorId", descriptorId),
            ("programName", programName)
        );
        return documentId;
    }

    private async Task<long> InsertDocumentAsync(DbConnection connection, QualifiedResourceName resource)
    {
        var insert = $"INSERT INTO {Table("dms", "Document")} ({Q("DocumentUuid")}, {Q("ResourceKeyId")})";
        var sql = IsPgsql
            ? $"{insert} VALUES (@uuid, @key) RETURNING {Q("DocumentId")};"
            : $"DECLARE @ids table (Id bigint); {insert} OUTPUT inserted.DocumentId INTO @ids VALUES (@uuid, @key); SELECT Id FROM @ids;";

        var rows = await RowsAsync(
            connection,
            null,
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
        string codeValue
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
            sql,
            ("documentId", documentId),
            ("uuid", Guid.NewGuid()),
            ("key", ResourceKeyId(descriptorResource)),
            ("descriptorId", descriptorId),
            ("ns", ns),
            ("codeValue", codeValue)
        );
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        string sql,
        params (string Name, object Value)[] values
    )
    {
        await using var command = CreateCommand(connection, null, sql, values);
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
