// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_RelationalWriteTargetContextResolver
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");

    [Test]
    public async Task It_returns_create_new_for_post_when_request_referential_id_does_not_match_an_existing_document()
    {
        var referentialId = new ReferentialId(Guid.NewGuid());
        var candidateDocumentUuid = new DocumentUuid(Guid.NewGuid());
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = new RelationalWriteTargetContextResolver(executor);

        var result = await sut.ResolveForPostAsync(
            CreateMappingSet(SqlDialect.Pgsql),
            _requestResource,
            referentialId,
            candidateDocumentUuid
        );

        result.Should().BeEquivalentTo(new RelationalWriteTargetContext.CreateNew(candidateDocumentUuid));
        executor.Commands.Should().ContainSingle();
        executor.Commands[0].CommandText.Should().Contain("dms.\"ReferentialIdentity\"");
        executor
            .Commands[0]
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(referentialId.Value, (short)1);
    }

    [Test]
    public async Task It_returns_existing_document_for_post_when_request_referential_id_matches_a_persisted_document()
    {
        var referentialId = new ReferentialId(Guid.NewGuid());
        var candidateDocumentUuid = new DocumentUuid(Guid.NewGuid());
        var existingDocumentUuid = new DocumentUuid(Guid.NewGuid());
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("DocumentId", 101L),
                        ("DocumentUuid", existingDocumentUuid.Value)
                    )
                ),
            ]),
        ]);
        var sut = new RelationalWriteTargetContextResolver(executor);

        var result = await sut.ResolveForPostAsync(
            CreateMappingSet(SqlDialect.Pgsql),
            _requestResource,
            referentialId,
            candidateDocumentUuid
        );

        result
            .Should()
            .BeEquivalentTo(new RelationalWriteTargetContext.ExistingDocument(101L, existingDocumentUuid));
    }

    [TestCase(SqlDialect.Pgsql, "dms.\"Document\"")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document]")]
    public async Task It_returns_existing_document_for_put_when_requested_document_uuid_matches_a_persisted_document(
        SqlDialect dialect,
        string expectedTableFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var executor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("DocumentId", 404L),
                        ("DocumentUuid", documentUuid.Value)
                    )
                ),
            ]),
        ]);
        var sut = new RelationalWriteTargetContextResolver(executor);

        var result = await sut.ResolveForPutAsync(CreateMappingSet(dialect), _requestResource, documentUuid);

        result.Should().BeEquivalentTo(new RelationalWriteTargetContext.ExistingDocument(404L, documentUuid));
        executor.Commands.Should().ContainSingle();
        executor.Commands[0].CommandText.Should().Contain(expectedTableFragment);
        executor
            .Commands[0]
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentUuid.Value, (short)1);
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
}
