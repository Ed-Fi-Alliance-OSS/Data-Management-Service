// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_RelationalWrite_Target_Lookup_Surfaces
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");

    [TestCase(SqlDialect.Pgsql, "dms.\"Document\"")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document]")]
    public async Task It_returns_not_found_for_repository_put_lookup_when_requested_document_uuid_does_not_match_a_persisted_document(
        SqlDialect dialect,
        string expectedTableFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var commandExecutor = new RecordingRelationalCommandExecutor(CreateLookupReader());
        var sut = new RelationalWriteTargetLookupService(commandExecutor);

        var result = await sut.ResolveForPutAsync(CreateMappingSet(dialect), _requestResource, documentUuid);

        result.Should().BeOfType<RelationalWriteTargetLookupResult.NotFound>();
        commandExecutor.ExecuteReaderAsyncCallCount.Should().Be(1);
        commandExecutor.CapturedCommand.Should().NotBeNull();
        commandExecutor.CapturedCommand!.CommandText.Should().Contain(expectedTableFragment);
        commandExecutor
            .CapturedCommand.CommandText.Should()
            .NotContain("ContentLastModifiedAt")
            .And.NotContain("JOIN");
        commandExecutor
            .CapturedCommand.Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentUuid.Value, (short)1);
    }

    [TestCase(SqlDialect.Pgsql, "dms.\"Document\"")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document]")]
    public async Task It_returns_existing_document_for_repository_put_lookup_when_requested_document_uuid_matches_a_persisted_document(
        SqlDialect dialect,
        string expectedTableFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        const long observedContentVersion = 907L;
        var commandExecutor = new RecordingRelationalCommandExecutor(
            CreateLookupReader((404L, documentUuid.Value, observedContentVersion))
        );
        var sut = new RelationalWriteTargetLookupService(commandExecutor);

        var result = await sut.ResolveForPutAsync(CreateMappingSet(dialect), _requestResource, documentUuid);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteTargetLookupResult.ExistingDocument(
                    404L,
                    documentUuid,
                    observedContentVersion
                )
            );
        commandExecutor.ExecuteReaderAsyncCallCount.Should().Be(1);
        commandExecutor.CapturedCommand.Should().NotBeNull();
        commandExecutor.CapturedCommand!.CommandText.Should().Contain(expectedTableFragment);
        commandExecutor
            .CapturedCommand.CommandText.Should()
            .NotContain("ContentLastModifiedAt")
            .And.NotContain("JOIN");
        commandExecutor
            .CapturedCommand.Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentUuid.Value, (short)1);
    }

    [TestCase(SqlDialect.Pgsql, "dms.\"Document\"", "document.\"ResourceKeyId\" = @resourceKeyId")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document]", "document.[ResourceKeyId] = @resourceKeyId")]
    public async Task It_resolves_descriptor_delete_from_document_only_metadata(
        SqlDialect dialect,
        string expectedTable,
        string expectedResourcePredicate
    )
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor");
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("DocumentId", 4_000_000_345L),
                        ("DocumentUuid", documentUuid.Value),
                        ("ResourceKeyId", (short)13),
                        ("ContentVersion", null)
                    )
                ),
            ]),
        ]);

        var result = await RelationalDocumentUuidLookupSupport.TryResolveDeleteTargetAsync(
            commandExecutor,
            CreateMappingSet(dialect),
            descriptorResource,
            documentUuid
        );

        result
            .Should()
            .BeEquivalentTo(new RelationalDocumentUuidLookupSupport.ResolvedDeleteTarget(4_000_000_345L));
        commandExecutor.Commands.Should().ContainSingle();
        var command = commandExecutor.Commands[0];
        command.CommandText.Should().Contain(expectedTable).And.Contain(expectedResourcePredicate);
        command.CommandText.Should().NotContain("ContentLastModifiedAt").And.NotContain("JOIN");
        command.Parameters.Select(parameter => parameter.Value).Should().Equal(documentUuid.Value, (short)13);
    }

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public async Task It_returns_no_descriptor_delete_target_when_uuid_is_missing_or_belongs_to_another_resource(
        SqlDialect dialect
    )
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor");
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);

        var result = await RelationalDocumentUuidLookupSupport.TryResolveDeleteTargetAsync(
            commandExecutor,
            CreateMappingSet(dialect),
            descriptorResource,
            documentUuid
        );

        result.Should().BeNull();
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor
            .Commands[0]
            .CommandText.Should()
            .NotContain("ContentLastModifiedAt")
            .And.NotContain("JOIN");
        commandExecutor
            .Commands[0]
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentUuid.Value, (short)13);
    }

    private static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        var mappingSet = RelationalAccessTestData.CreateMappingSet(_requestResource);

        return mappingSet with
        {
            Key = new MappingSetKey(
                mappingSet.Key.EffectiveSchemaHash,
                dialect,
                mappingSet.Key.RelationalMappingVersion
            ),
            Model = mappingSet.Model with { Dialect = dialect },
        };
    }

    private static DataTableReader CreateLookupReader(
        params (long DocumentId, Guid DocumentUuid, long ContentVersion)[] rows
    )
    {
        var table = new DataTable();
        table.Columns.Add("DocumentId", typeof(long));
        table.Columns.Add("DocumentUuid", typeof(Guid));
        table.Columns.Add("ResourceKeyId", typeof(short));
        table.Columns.Add("ContentVersion", typeof(long));

        foreach (var row in rows)
        {
            table.Rows.Add(row.DocumentId, row.DocumentUuid, (short)1, row.ContentVersion);
        }

        return table.CreateDataReader();
    }

    private sealed class RecordingRelationalCommandExecutor(DataTableReader reader)
        : IRelationalCommandExecutor
    {
        private readonly DataTableReader _reader = reader;

        public SqlDialect Dialect => SqlDialect.Pgsql;

        public int ExecuteReaderAsyncCallCount { get; private set; }

        public RelationalCommand? CapturedCommand { get; private set; }

        public async Task<TResult> ExecuteReaderAsync<TResult>(
            RelationalCommand command,
            Func<IRelationalCommandReader, CancellationToken, Task<TResult>> readAsync,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedCommand = command;
            ExecuteReaderAsyncCallCount++;

            await using var relationalReader = new DbRelationalCommandReader(_reader);
            return await readAsync(relationalReader, cancellationToken);
        }
    }
}

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_The_Descriptor_Target_Probe_Command(SqlDialect dialect)
{
    private const string DescriptorUri = "URI://ed-fi.org/ProgramTypeDescriptor#Career";
    private const int DescriptorId = 7;
    private const long OwningDocumentId = 900_001L;
    private const long ContentVersion = 41L;

    private readonly DocumentUuid _documentUuid = new(Guid.NewGuid());
    private short _resourceKeyId;
    private RelationalCommand _command = null!;
    private RelationalWriteTargetLookupResult _result = null!;

    [SetUp]
    public async Task Setup()
    {
        var mappingSet = NaturalKeyLookupTestMappingSets.Ds52(dialect);
        _resourceKeyId = mappingSet.ResourceKeyIdByResource[
            NaturalKeyLookupTestMappingSets.ProgramTypeDescriptor
        ];
        var commandExecutor = new InMemoryRelationalCommandExecutor(
            [
                new InMemoryRelationalCommandExecution([
                    InMemoryRelationalResultSet.Create(
                        RelationalAccessTestData.CreateRow(
                            ("DescriptorId", DescriptorId),
                            ("DocumentId", OwningDocumentId),
                            ("DocumentUuid", _documentUuid.Value),
                            ("ContentVersion", ContentVersion)
                        )
                    ),
                ]),
            ],
            dialect
        );

        _result = await RelationalWriteTargetLookupSupport.ResolveDescriptorForPostAsync(
            commandExecutor,
            mappingSet,
            NaturalKeyLookupTestMappingSets.ProgramTypeDescriptor,
            DescriptorUri,
            new DocumentUuid(Guid.NewGuid())
        );
        _command = commandExecutor.Commands.Single();
    }

    [Test]
    public void It_probes_the_descriptor_index_fold_and_the_descriptor_type_literal()
    {
        // PostgreSQL folds both sides exactly as the descriptor URI index expression does, and SQL Server
        // collates the parameter inside LOWER before comparing the indexed lowered column.
        var expected =
            dialect == SqlDialect.Pgsql
                ? $"""
                    SELECT
                        descriptor."DescriptorId" AS "DescriptorId",
                        descriptor."DocumentId" AS "DocumentId",
                        document."DocumentUuid" AS "DocumentUuid",
                        document."ContentVersion" AS "ContentVersion"
                    FROM "dms"."Descriptor" descriptor
                    INNER JOIN "dms"."Document" document
                        ON document."DocumentId" = descriptor."DocumentId"
                    WHERE lower((descriptor."Namespace" || '#' || descriptor."CodeValue") COLLATE "pg_c_utf8") = lower(@descriptorUri COLLATE "pg_c_utf8")
                        AND descriptor."ResourceKeyId" = CAST({_resourceKeyId} AS smallint)
                    """
                : $"""
                    SELECT
                        descriptor.[DescriptorId] AS [DescriptorId],
                        descriptor.[DocumentId] AS [DocumentId],
                        document.[DocumentUuid] AS [DocumentUuid],
                        document.[ContentVersion] AS [ContentVersion]
                    FROM [dms].[Descriptor] descriptor
                    INNER JOIN [dms].[Document] document
                        ON document.[DocumentId] = descriptor.[DocumentId]
                    WHERE descriptor.[UriLowered] = LOWER(@descriptorUri COLLATE SQL_Latin1_General_CP1_CI_AS)
                        AND descriptor.[ResourceKeyId] = CAST({_resourceKeyId} AS smallint)
                    """;

        _command.CommandText.Should().Be(expected);
    }

    [Test]
    public void It_binds_only_the_unmodified_request_uri()
    {
        _command
            .Parameters.Select(parameter => (parameter.Name, parameter.Value))
            .Should()
            .Equal(("@descriptorUri", (object?)DescriptorUri));
    }

    [Test]
    public void It_returns_the_owning_document_rather_than_the_descriptor_key()
    {
        _result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteTargetLookupResult.ExistingDocument(
                    OwningDocumentId,
                    _documentUuid,
                    ContentVersion
                )
            );
    }
}
