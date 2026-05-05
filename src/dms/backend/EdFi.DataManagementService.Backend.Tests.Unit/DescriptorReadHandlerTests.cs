// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_DescriptorReadHandler
{
    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "SchoolTypeDescriptor");
    private static readonly QualifiedResourceName _requestResource = new("Ed-Fi", "Student");

    [TestCase(SqlDialect.Pgsql, "dms.\"Document\"", "dms.\"Descriptor\"")]
    [TestCase(SqlDialect.Mssql, "[dms].[Document]", "[dms].[Descriptor]")]
    public async Task It_reads_descriptor_gets_directly_from_document_and_descriptor(
        SqlDialect dialect,
        string expectedDocumentTableFragment,
        string expectedDescriptorTableFragment
    )
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(CreateDescriptorRow(documentUuid.Value)),
            ]),
        ]);
        var sut = new DescriptorReadHandler(commandExecutor, NullLogger<DescriptorReadHandler>.Instance);

        var result = await sut.HandleGetByIdAsync(CreateRequest(dialect, documentUuid));

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.DocumentUuid.Should().Be(documentUuid);
        success.LastModifiedDate.Should().Be(new DateTime(2026, 5, 5, 14, 30, 45, DateTimeKind.Utc));
        success.EdfiDoc["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        success.EdfiDoc["codeValue"]!.GetValue<string>().Should().Be("Alternative");
        success.EdfiDoc["shortDescription"]!.GetValue<string>().Should().Be("Alternative");
        success.EdfiDoc["id"]!.GetValue<string>().Should().Be(documentUuid.Value.ToString());
        success.EdfiDoc["_etag"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        success.EdfiDoc["_lastModifiedDate"]!.GetValue<string>().Should().Be("2026-05-05T14:30:45Z");
        success.EdfiDoc["Discriminator"].Should().BeNull();
        commandExecutor.Commands.Should().ContainSingle();
        commandExecutor.Commands[0].CommandText.Should().Contain(expectedDocumentTableFragment);
        commandExecutor.Commands[0].CommandText.Should().Contain(expectedDescriptorTableFragment);
        commandExecutor.Commands[0].CommandText.Should().Contain("LEFT JOIN");
        commandExecutor
            .Commands[0]
            .Parameters.Select(parameter => parameter.Value)
            .Should()
            .Equal(documentUuid.Value, (short)13);
    }

    [Test]
    public async Task It_returns_not_exists_when_document_uuid_is_missing_or_is_for_the_wrong_resource()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([InMemoryRelationalResultSet.Create()]),
        ]);
        var sut = new DescriptorReadHandler(commandExecutor, NullLogger<DescriptorReadHandler>.Instance);

        var result = await sut.HandleGetByIdAsync(
            CreateRequest(
                SqlDialect.Pgsql,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-cccccccccccc"))
            )
        );

        result.Should().BeOfType<GetResult.GetFailureNotExists>();
        commandExecutor.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task It_fails_closed_for_descriptor_get_authorization_without_executing_sql()
    {
        var commandExecutor = new InMemoryRelationalCommandExecutor([]);
        var sut = new DescriptorReadHandler(commandExecutor, NullLogger<DescriptorReadHandler>.Instance);

        var result = await sut.HandleGetByIdAsync(
            CreateRequest(
                SqlDialect.Pgsql,
                new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-dddddddddddd")),
                authorizationStrategyEvaluators: [new("RelationshipsWithEdOrgsOnly", [], FilterOperator.And)]
            )
        );

        result
            .Should()
            .BeEquivalentTo(
                new GetResult.GetFailureNotImplemented(
                    "Relational descriptor GET authorization is not implemented for resource 'Ed-Fi.SchoolTypeDescriptor' when effective GET authorization requires filtering. Effective strategies: ['RelationshipsWithEdOrgsOnly']. Only requests with no authorization strategies or only 'NoFurtherAuthorizationRequired' are currently supported."
                )
            );
        commandExecutor.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task It_returns_an_unknown_failure_when_the_selected_document_has_no_descriptor_row()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-eeeeeeeeeeee"));
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(
                        documentUuid.Value,
                        ns: null,
                        codeValue: null,
                        shortDescription: null,
                        description: null,
                        effectiveBeginDate: null,
                        effectiveEndDate: null,
                        discriminator: null
                    )
                ),
            ]),
        ]);
        var sut = new DescriptorReadHandler(commandExecutor, NullLogger<DescriptorReadHandler>.Instance);

        var result = await sut.HandleGetByIdAsync(CreateRequest(SqlDialect.Pgsql, documentUuid));

        var failure = result.Should().BeOfType<GetResult.UnknownFailure>().Subject;
        failure.FailureMessage.Should().Contain("dms.Descriptor.Namespace must not be null.");
        failure.FailureMessage.Should().Contain("DocumentId 101");
        failure.FailureMessage.Should().Contain("ResourceKeyId=13");
    }

    [Test]
    public async Task It_treats_discriminator_as_diagnostic_only_when_the_document_resource_key_matches()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-ffffffffffff"));
        var commandExecutor = new InMemoryRelationalCommandExecutor([
            new InMemoryRelationalCommandExecution([
                InMemoryRelationalResultSet.Create(
                    CreateDescriptorRow(documentUuid.Value, discriminator: "OtherDescriptor")
                ),
            ]),
        ]);
        var sut = new DescriptorReadHandler(commandExecutor, NullLogger<DescriptorReadHandler>.Instance);

        var result = await sut.HandleGetByIdAsync(CreateRequest(SqlDialect.Pgsql, documentUuid));

        var success = result.Should().BeOfType<GetResult.GetSuccess>().Subject;
        success.DocumentUuid.Should().Be(documentUuid);
        success.EdfiDoc["namespace"]!.GetValue<string>().Should().Be("uri://ed-fi.org/SchoolTypeDescriptor");
        success.EdfiDoc["codeValue"]!.GetValue<string>().Should().Be("Alternative");
    }

    private static DescriptorGetByIdRequest CreateRequest(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        AuthorizationStrategyEvaluator[]? authorizationStrategyEvaluators = null
    )
    {
        var mappingSet = CreateMappingSet(dialect);
        mappingSet
            .TryGetDescriptorResourceModel(_descriptorResource, out var descriptorResourceModel)
            .Should()
            .BeTrue();

        return new DescriptorGetByIdRequest(
            mappingSet,
            descriptorResourceModel!,
            _descriptorResource,
            documentUuid,
            RelationalGetRequestReadMode.ExternalResponse,
            authorizationStrategyEvaluators ?? [],
            readableProfileProjectionContext: null,
            new TraceId("descriptor-get-trace")
        );
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

    private static IReadOnlyDictionary<string, object?> CreateDescriptorRow(
        Guid documentUuid,
        string? ns = "uri://ed-fi.org/SchoolTypeDescriptor",
        string? codeValue = "Alternative",
        string? shortDescription = "Alternative",
        string? description = "Alternative school type",
        DateOnly? effectiveBeginDate = null,
        DateOnly? effectiveEndDate = null,
        string? discriminator = "SchoolTypeDescriptor"
    )
    {
        return RelationalAccessTestData.CreateRow(
            ("DocumentId", 101L),
            ("DocumentUuid", documentUuid),
            ("ContentLastModifiedAt", new DateTimeOffset(2026, 5, 5, 14, 30, 45, TimeSpan.Zero)),
            ("ResourceKeyId", (short)13),
            ("Namespace", ns),
            ("CodeValue", codeValue),
            ("ShortDescription", shortDescription),
            ("Description", description),
            ("EffectiveBeginDate", effectiveBeginDate),
            ("EffectiveEndDate", effectiveEndDate),
            ("Discriminator", discriminator)
        );
    }
}
