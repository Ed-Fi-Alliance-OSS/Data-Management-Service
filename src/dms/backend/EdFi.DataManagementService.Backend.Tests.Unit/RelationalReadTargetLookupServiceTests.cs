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
[Parallelizable]
public class Given_RelationalReadTargetLookupService
{
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");

    [TestCase(SqlDialect.Pgsql, "\"dms\".\"Document\"", "\"edfi\".\"Student\"")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document]", "[edfi].[Student]")]
    public async Task It_returns_not_found_when_document_uuid_does_not_match_a_persisted_document(
        SqlDialect dialect,
        string expectedTableFragment,
        string expectedRootTableFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = new RelationalReadTargetLookupService(commandExecutor);

        var result = await sut.ResolveForGetByIdAsync(
            CreateMappingSet(dialect),
            _requestResource,
            documentUuid
        );

        result.Should().BeOfType<RelationalReadTargetLookupResult.NotFound>();
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor.Commands[0].CommandText.Should().Contain(expectedTableFragment);
        commandExecutor
            .Commands[0]
            .CommandText.Should()
            .Contain($"LEFT JOIN {expectedRootTableFragment} owner");
        commandExecutor.Commands[0].CommandText.Should().NotContain("document.\"ContentLastModifiedAt\"");
        commandExecutor.Commands[0].CommandText.Should().NotContain("document.[ContentLastModifiedAt]");
        commandExecutor
            .Commands[0]
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentUuid.Value, (short)1);
    }

    [TestCase(SqlDialect.Pgsql, "\"edfi\".\"Student\"")]
    [TestCase(SqlDialect.Mssql, "[edfi].[Student]")]
    public async Task It_returns_existing_document_when_document_uuid_matches_the_requested_resource(
        SqlDialect dialect,
        string expectedTableFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var contentLastModifiedAt = new DateTimeOffset(2026, 4, 11, 12, 30, 45, TimeSpan.Zero);
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("DocumentId", 404L),
                        ("DocumentUuid", documentUuid.Value),
                        ("ResourceKeyId", (short)1),
                        ("ContentVersion", 907L),
                        ("ContentLastModifiedAt", contentLastModifiedAt)
                    )
                ),
            ]),
        ]);
        var sut = new RelationalReadTargetLookupService(commandExecutor);

        var result = await sut.ResolveForGetByIdAsync(
            CreateMappingSet(dialect),
            _requestResource,
            documentUuid
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    404L,
                    documentUuid,
                    907L,
                    contentLastModifiedAt
                )
            );
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor.Commands[0].CommandText.Should().Contain($"LEFT JOIN {expectedTableFragment} owner");
        commandExecutor.Commands[0].CommandText.Should().Contain("owner.");
        commandExecutor.Commands[0].CommandText.Should().Contain("ContentLastModifiedAt");
        commandExecutor.Commands[0].CommandText.Should().NotContain("document.\"ContentLastModifiedAt\"");
        commandExecutor.Commands[0].CommandText.Should().NotContain("document.[ContentLastModifiedAt]");
        commandExecutor
            .Commands[0]
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentUuid.Value, (short)1);
    }

    [Test]
    public void It_rejects_existing_document_without_a_real_content_last_modified_timestamp()
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var existingDocument = new RelationalReadTargetLookupResult.ExistingDocument(
            404L,
            documentUuid,
            907L,
            new DateTimeOffset(2026, 4, 11, 12, 30, 45, TimeSpan.Zero)
        );

        Action constructAction = () =>
            _ = new RelationalReadTargetLookupResult.ExistingDocument(404L, documentUuid, 907L, default);
        Action cloneAction = () => _ = existingDocument with { ContentLastModifiedAt = default };

        constructAction.Should().Throw<ArgumentException>().WithMessage("*ContentLastModifiedAt*");
        cloneAction.Should().Throw<ArgumentException>().WithMessage("*ContentLastModifiedAt*");
    }

    [TestCase(SqlDialect.Pgsql, "\"dms\".\"Document\"")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document]")]
    public async Task It_distinguishes_a_uuid_that_exists_for_the_wrong_resource(
        SqlDialect dialect,
        string expectedTableFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("DocumentId", 808L),
                        ("DocumentUuid", documentUuid.Value),
                        ("ResourceKeyId", (short)11),
                        ("ContentVersion", 1234L),
                        ("ContentLastModifiedAt", null)
                    )
                ),
            ]),
        ]);
        var sut = new RelationalReadTargetLookupService(commandExecutor);

        var result = await sut.ResolveForGetByIdAsync(
            CreateMappingSet(dialect),
            _requestResource,
            documentUuid
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalReadTargetLookupResult.WrongResource(
                    documentUuid,
                    new QualifiedResourceName("Ed-Fi", "School")
                )
            );
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor.Commands[0].CommandText.Should().Contain(expectedTableFragment);
        commandExecutor.Commands[0].CommandText.Should().Contain("LEFT JOIN");
        commandExecutor
            .Commands[0]
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentUuid.Value, (short)1);
    }

    [TestCase(SqlDialect.Pgsql, "\"dms\".\"Descriptor\"")]
    [TestCase(SqlDialect.Mssql, "[dms].[Descriptor]")]
    public async Task It_uses_the_descriptor_document_id_for_descriptor_reads(
        SqlDialect dialect,
        string expectedDescriptorTable
    )
    {
        var descriptorResource = new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor");
        var documentUuid = new DocumentUuid(Guid.NewGuid());
        var timestamp = new DateTimeOffset(2026, 4, 11, 12, 30, 45, TimeSpan.Zero);
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    RelationalAccessTestData.CreateRow(
                        ("DocumentId", 4_000_000_345L),
                        ("DocumentUuid", documentUuid.Value),
                        ("ResourceKeyId", (short)13),
                        ("ContentVersion", 907L),
                        ("ContentLastModifiedAt", timestamp)
                    )
                ),
            ]),
        ]);

        var result = await new RelationalReadTargetLookupService(commandExecutor).ResolveForGetByIdAsync(
            CreateMappingSet(dialect),
            descriptorResource,
            documentUuid
        );

        result
            .Should()
            .BeEquivalentTo(
                new RelationalReadTargetLookupResult.ExistingDocument(
                    4_000_000_345L,
                    documentUuid,
                    907L,
                    timestamp
                )
            );
        commandExecutor.Commands.Should().ContainSingle();
        var sql = commandExecutor.Commands[0].CommandText;
        sql.Should().Contain($"LEFT JOIN {expectedDescriptorTable} owner");
        sql.Should()
            .Contain(
                dialect == SqlDialect.Pgsql
                    ? "owner.\"DocumentId\" = document.\"DocumentId\""
                    : "owner.[DocumentId] = document.[DocumentId]"
            );
        sql.Should().NotContain("owner.\"DescriptorId\"").And.NotContain("owner.[DescriptorId]");
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
}
