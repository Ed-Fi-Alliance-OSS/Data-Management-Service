// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

public partial class Given_Descriptor_Write_Response_Etags
{
    // Deliberately wider than DescriptorId: every lock, update, RI write, and cache operation
    // must retain the owning document key. The update SQL must not allocate or assign either key.
    private const long ExistingDocumentId = 4_000_000_345L;

    private static IEnumerable<TestCaseData> PostRepresentationCases() =>
        RepresentationCases(["namespace-case", "code-case", "equal-uri-components"]);

    private static IEnumerable<TestCaseData> PutRepresentationCases() =>
        RepresentationCases([
            "namespace-case",
            "code-case",
            "equal-uri-components",
            "different-code",
            "pre-delimiter-space",
        ]);

    private static IEnumerable<TestCaseData> RepresentationCases(string[] changes)
    {
        foreach (var dialect in new[] { SqlDialect.Pgsql, SqlDialect.Mssql })
        {
            foreach (var useIfMatch in new[] { false, true })
            {
                foreach (var change in changes)
                {
                    yield return new TestCaseData(dialect, useIfMatch, change);
                }
            }
        }
    }

    [TestCaseSource(nameof(PostRepresentationCases))]
    public async Task It_applies_incoming_components_and_returns_the_updated_etag_for_an_RI_matching_post(
        SqlDialect dialect,
        bool useIfMatch,
        string change
    )
    {
        var (persistedNamespace, persistedCode, incomingNamespace, incomingCode) = ComponentsFor(change);
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                ExistingDocumentId,
                documentUuid,
                44L
            ),
        };
        var sessionFactory = PrepareExistingDescriptor(
            dialect,
            documentUuid,
            persistedNamespace,
            persistedCode,
            useIfMatch
        );
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);
        var request = CreatePostRequest(CreateMappingSet(dialect), documentUuid);
        request.RequestBody["namespace"] = incomingNamespace;
        request.RequestBody["codeValue"] = incomingCode;
        if (useIfMatch)
        {
            request = request with
            {
                WritePrecondition = new WritePrecondition.IfMatch(ExpectedComposedDescriptorEtag(44L)),
            };
        }

        var result = await CreateSut(targetLookupService, sessionFactory)
            .HandlePostWithSamePolicyForCreateAndUpdateAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new UpsertResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(45L))
            );
        targetLookupService.ResolveForPostCallCount.Should().Be(useIfMatch ? 0 : 1);
        sessionFactory.Session.CommitCallCount.Should().Be(1);
        sessionFactory.Session.RollbackCallCount.Should().Be(0);
        AssertExistingDescriptorCommands(sessionFactory, dialect, useIfMatch, writes: true);
        var update = sessionFactory.Session.Executor.Commands[^1];
        AssertRepresentationUpdate(update, dialect, incomingNamespace, incomingCode);
        update
            .CommandText.Should()
            .Contain(
                dialect is SqlDialect.Pgsql
                    ? "VALUES (@referentialId, @documentId, @resourceKeyId)"
                    : "USING (VALUES (@referentialId, @documentId, @resourceKeyId))"
            );
        update
            .Parameters.Single(parameter => parameter.Name == "@referentialId")
            .Value.Should()
            .Be(request.ReferentialId!.Value.Value);
        update.Parameters.Single(parameter => parameter.Name == "@resourceKeyId").Value.Should().Be((short)1);
    }

    [TestCaseSource(nameof(PutRepresentationCases))]
    public async Task It_compares_the_reconstructed_whole_URI_ordinally_before_applying_a_put(
        SqlDialect dialect,
        bool useIfMatch,
        string change
    )
    {
        var (persistedNamespace, persistedCode, incomingNamespace, incomingCode) = ComponentsFor(change);
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                ExistingDocumentId,
                documentUuid,
                44L
            ),
        };
        var sessionFactory = PrepareExistingDescriptor(
            dialect,
            documentUuid,
            persistedNamespace,
            persistedCode,
            useIfMatch
        );
        sessionFactory.Session.Executor.ResultSets.Enqueue([CreateContentVersionResultSet(45L)]);
        var request = CreatePutRequest(CreateMappingSet(dialect), documentUuid);
        request.RequestBody["namespace"] = incomingNamespace;
        request.RequestBody["codeValue"] = incomingCode;
        if (useIfMatch)
        {
            request = request with
            {
                WritePrecondition = new WritePrecondition.IfMatch(ExpectedComposedDescriptorEtag(44L)),
            };
        }

        var result = await CreateSut(targetLookupService, sessionFactory).HandlePutAsync(request);

        var sameUri = change == "equal-uri-components";
        if (sameUri)
        {
            result
                .Should()
                .BeEquivalentTo(
                    new UpdateResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(45L))
                );
            AssertRepresentationUpdate(
                sessionFactory.Session.Executor.Commands[^1],
                dialect,
                incomingNamespace,
                incomingCode
            );
        }
        else
        {
            result.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
        }
        targetLookupService.ResolveForPutCallCount.Should().Be(useIfMatch ? 0 : 1);
        sessionFactory.Session.CommitCallCount.Should().Be(sameUri ? 1 : 0);
        sessionFactory.Session.RollbackCallCount.Should().Be(sameUri ? 0 : 1);
        AssertExistingDescriptorCommands(sessionFactory, dialect, useIfMatch, writes: sameUri);
    }

    [TestCase(SqlDialect.Pgsql, false, false)]
    [TestCase(SqlDialect.Pgsql, false, true)]
    [TestCase(SqlDialect.Pgsql, true, false)]
    [TestCase(SqlDialect.Pgsql, true, true)]
    [TestCase(SqlDialect.Mssql, false, false)]
    [TestCase(SqlDialect.Mssql, false, true)]
    [TestCase(SqlDialect.Mssql, true, false)]
    [TestCase(SqlDialect.Mssql, true, true)]
    public async Task It_returns_the_unchanged_etag_without_any_persist_or_enqueue_command_for_an_unchanged_body(
        SqlDialect dialect,
        bool useIfMatch,
        bool post
    )
    {
        const string Namespace = "uri://ed-fi.org/SchoolTypeDescriptor #Part";
        const string Code = "Charter#More";
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetLookupService = new StubRelationalWriteTargetLookupService
        {
            PostResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                ExistingDocumentId,
                documentUuid,
                44L
            ),
            PutResult = new RelationalWriteTargetLookupResult.ExistingDocument(
                ExistingDocumentId,
                documentUuid,
                44L
            ),
        };
        var sessionFactory = PrepareExistingDescriptor(dialect, documentUuid, Namespace, Code, useIfMatch);
        var request = post
            ? CreatePostRequest(CreateMappingSet(dialect), documentUuid)
            : CreatePutRequest(CreateMappingSet(dialect), documentUuid);
        request.RequestBody["namespace"] = Namespace;
        request.RequestBody["codeValue"] = Code;
        if (useIfMatch)
        {
            request = request with
            {
                WritePrecondition = new WritePrecondition.IfMatch(ExpectedComposedDescriptorEtag(44L)),
            };
        }
        var sut = CreateSut(targetLookupService, sessionFactory);

        if (post)
        {
            var result = await sut.HandlePostWithSamePolicyForCreateAndUpdateAsync(request);
            result
                .Should()
                .BeEquivalentTo(
                    new UpsertResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(44L))
                );
        }
        else
        {
            var result = await sut.HandlePutAsync(request);
            result
                .Should()
                .BeEquivalentTo(
                    new UpdateResult.UpdateSuccess(documentUuid, ExpectedComposedDescriptorEtag(44L))
                );
        }
        sessionFactory.Session.CommitCallCount.Should().Be(0);
        sessionFactory.Session.RollbackCallCount.Should().Be(1);
        AssertExistingDescriptorCommands(sessionFactory, dialect, useIfMatch, writes: false);
    }

    private static (string, string, string, string) ComponentsFor(string change) =>
        change switch
        {
            "namespace-case" => (
                "uri://ed-fi.org/SchoolTypeDescriptor",
                "Charter",
                "uri://ED-FI.org/SchoolTypeDescriptor",
                "Charter"
            ),
            "code-case" => (
                "uri://ed-fi.org/SchoolTypeDescriptor",
                "Charter",
                "uri://ed-fi.org/SchoolTypeDescriptor",
                "CHARTER"
            ),
            "equal-uri-components" => (
                "uri://ed-fi.org/SchoolTypeDescriptor",
                "Part#Charter",
                "uri://ed-fi.org/SchoolTypeDescriptor#Part",
                "Charter"
            ),
            "different-code" => (
                "uri://ed-fi.org/SchoolTypeDescriptor",
                "Charter",
                "uri://ed-fi.org/SchoolTypeDescriptor",
                "Alternative"
            ),
            "pre-delimiter-space" => (
                "uri://ed-fi.org/SchoolTypeDescriptor ",
                "Charter",
                "uri://ed-fi.org/SchoolTypeDescriptor",
                "Charter"
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

    [TestCase(SqlDialect.Pgsql, false)]
    [TestCase(SqlDialect.Pgsql, true)]
    [TestCase(SqlDialect.Mssql, false)]
    [TestCase(SqlDialect.Mssql, true)]
    public async Task It_deletes_a_compact_descriptor_through_its_owning_document_association(
        SqlDialect dialect,
        bool useIfMatch
    )
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var factory = useIfMatch
            ? PrepareExistingDescriptor(
                dialect,
                documentUuid,
                "uri://ed-fi.org/SchoolTypeDescriptor",
                "Charter",
                useIfMatch
            )
            : new RecordingRelationalWriteSessionFactory(dialect);
        factory.Session.Executor.ResultSets.Enqueue([
            InMemoryRelationalResultSet.Create(),
            InMemoryRelationalResultSet.Create(
                new Dictionary<string, object?> { ["DocumentId"] = ExistingDocumentId }
            ),
        ]);
        var request = new DescriptorDeleteRequest(
            CreateMappingSet(dialect),
            _descriptorResource,
            documentUuid,
            new TraceId("compact-descriptor-delete")
        );
        if (useIfMatch)
        {
            request = request with
            {
                WritePrecondition = new WritePrecondition.IfMatch(ExpectedComposedDescriptorEtag(44L)),
            };
        }

        var result = await CreateSut(new StubRelationalWriteTargetLookupService(), factory)
            .HandleDeleteAsync(request);

        result.Should().BeOfType<DeleteResult.DeleteSuccess>();
        factory.Session.CommitCallCount.Should().Be(1);
        factory.Session.RollbackCallCount.Should().Be(0);
        if (useIfMatch)
        {
            AssertExistingDescriptorCommands(factory, dialect, useIfMatch, writes: true);
        }
        else
        {
            factory.Session.Executor.Commands.Should().ContainSingle();
            factory.Session.ScalarCommands.Should().BeEmpty();
        }
        var delete = factory.Session.Executor.Commands[^1];
        delete
            .CommandText.Should()
            .Contain(
                dialect is SqlDialect.Pgsql
                    ? "WHERE \"DocumentId\" IN (\n    SELECT \"DocumentId\""
                    : "WHERE [DocumentId] IN (\n    SELECT [DocumentId]"
            );
        delete.CommandText.Should().NotContain("DescriptorId");
        delete
            .Parameters.Single(parameter => parameter.Name == "@documentUuid")
            .Value.Should()
            .Be(documentUuid.Value);
        delete.Parameters.Single(parameter => parameter.Name == "@resourceKeyId").Value.Should().Be((short)1);
    }

    private static RecordingRelationalWriteSessionFactory PrepareExistingDescriptor(
        SqlDialect dialect,
        DocumentUuid documentUuid,
        string @namespace,
        string codeValue,
        bool useIfMatch
    )
    {
        var factory = new RecordingRelationalWriteSessionFactory(dialect);
        if (useIfMatch)
        {
            factory.Session.Executor.ResultSets.Enqueue([
                InMemoryRelationalResultSet.Create(
                    new Dictionary<string, object?>
                    {
                        ["DocumentId"] = ExistingDocumentId,
                        ["DocumentUuid"] = documentUuid.Value,
                        ["ResourceKeyId"] = (short)1,
                        ["ContentVersion"] = 44L,
                        ["ContentLastModifiedAt"] = new DateTimeOffset(
                            2026,
                            10,
                            7,
                            12,
                            30,
                            45,
                            TimeSpan.Zero
                        ),
                    }
                ),
            ]);
        }
        factory.Session.ScalarResults.Enqueue(44L);
        factory.Session.Executor.ResultSets.Enqueue([
            CreatePersistedDescriptorResultSet(@namespace: @namespace, codeValue: codeValue),
        ]);
        return factory;
    }

    private static void AssertExistingDescriptorCommands(
        RecordingRelationalWriteSessionFactory factory,
        SqlDialect dialect,
        bool useIfMatch,
        bool writes
    )
    {
        factory.Session.ScalarCommands.Should().ContainSingle();
        factory
            .Session.ScalarCommands[0]
            .Parameters.Single(parameter => parameter.Name == "@documentId")
            .Value.Should()
            .BeOfType<long>()
            .Which.Should()
            .Be(ExistingDocumentId);
        factory.Session.Executor.Commands.Should().HaveCount((useIfMatch ? 2 : 1) + (writes ? 1 : 0));
        var read = factory.Session.Executor.Commands[useIfMatch ? 1 : 0];
        read.CommandText.Should()
            .Be(
                dialect is SqlDialect.Pgsql
                    ? "SELECT \"Namespace\", \"CodeValue\", \"ShortDescription\", \"Description\", \"EffectiveBeginDate\", \"EffectiveEndDate\"\nFROM dms.\"Descriptor\"\nWHERE \"DocumentId\" = @documentId;"
                    : "SELECT [Namespace], [CodeValue], [ShortDescription], [Description], [EffectiveBeginDate], [EffectiveEndDate]\nFROM [dms].[Descriptor]\nWHERE [DocumentId] = @documentId;"
            );
        read.Parameters.Single().Value.Should().BeOfType<long>().Which.Should().Be(ExistingDocumentId);
    }

    private static void AssertRepresentationUpdate(
        RelationalCommand update,
        SqlDialect dialect,
        string @namespace,
        string codeValue
    )
    {
        AssertOnlyStoredDescriptorParameters(update);
        var firstStatement = update.CommandText.Split(';')[0];
        var assignments = firstStatement[
            (firstStatement.IndexOf("SET ", StringComparison.Ordinal) + 4)..firstStatement.IndexOf(
                "WHERE ",
                StringComparison.Ordinal
            )
        ];
        assignments
            .Should()
            .NotContain("DescriptorId")
            .And.NotContain("DocumentId")
            .And.NotContain("ResourceKeyId");
        update
            .CommandText.Should()
            .Contain(
                dialect is SqlDialect.Pgsql
                    ? "WHERE \"DocumentId\" = @documentId"
                    : "WHERE [DocumentId] = @documentId"
            );
        update
            .CommandText.Should()
            .Contain(
                dialect is SqlDialect.Pgsql
                    ? "work.\"DocumentId\" = document.\"DocumentId\""
                    : "work.[DocumentId] = document.[DocumentId]"
            );
        update
            .Parameters.Single(parameter => parameter.Name == "@documentId")
            .Value.Should()
            .BeOfType<long>()
            .Which.Should()
            .Be(ExistingDocumentId);
        update.Parameters.Single(parameter => parameter.Name == "@namespace").Value.Should().Be(@namespace);
        update.Parameters.Single(parameter => parameter.Name == "@codeValue").Value.Should().Be(codeValue);
        update.Parameters.Single(parameter => parameter.Name == "@description").Value.Should().Be("Charter");
        update
            .Parameters.Single(parameter => parameter.Name == "@effectiveBeginDate")
            .Value.Should()
            .Be("2024-01-01");
    }

    private static void AssertOnlyStoredDescriptorParameters(RelationalCommand command)
    {
        command
            .CommandText.Should()
            .NotContain("\"Uri\"")
            .And.NotContain("[Uri]")
            .And.NotContain("Discriminator");
        command
            .Parameters.Select(parameter => parameter.Name)
            .Should()
            .NotContain(["@uri", "@discriminator", "@descriptorId"]);
    }
}
