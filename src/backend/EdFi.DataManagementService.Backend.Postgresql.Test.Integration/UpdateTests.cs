// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

[TestFixture]
public class UpdateTests : DatabaseTest
{
    private static readonly string _defaultResourceName = "DefaultResourceName";

    [TestFixture]
    public class Given_an_update_of_a_nonexistent_document : UpdateTests
    {
        private UpdateResult? _updateResult;
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":1}""";

        [SetUp]
        public async Task Setup()
        {
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _defaultResourceName,
                Guid.NewGuid(),
                Guid.NewGuid(),
                _edFiDocString
            );
            _updateResult = await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_a_failed_update()
        {
            _updateResult!.Should().BeOfType<UpdateResult.UpdateFailureNotExists>();
        }

        [Test]
        public async Task It_should_not_be_found_updated_by_get()
        {
            var getResult = await CreateGetById()
                .GetById(
                    CreateGetRequest(_defaultResourceName, _documentUuidGuid),
                    Connection!,
                    Transaction!
                );
            getResult.Should().BeOfType<GetResult.GetFailureNotExists>();
        }
    }

    [TestFixture]
    public class Given_an_update_of_an_existing_document : UpdateTests
    {
        private UpdateResult? _updateResult;
        private GetResult? _getResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            // Create
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString1
            );
            await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);

            // Update
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString2
            );
            _updateResult = await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);

            // Confirm change was made
            IGetRequest getRequest = CreateGetRequest(_defaultResourceName, _documentUuidGuid);
            _getResult = await CreateGetById().GetById(getRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_a_successful_update()
        {
            _updateResult!.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public void It_should_be_found_updated_by_get()
        {
            _getResult!.Should().BeOfType<GetResult.GetSuccess>();
            (_getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_documentUuidGuid);
            (_getResult! as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Be(_edFiDocString2);
        }
    }

    [TestFixture]
    public class Given_an_update_of_an_existing_document_with_different_referentialId : UpdateTests
    {
        private UpdateResult? _updateResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid1 = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid2 = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            // Create
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid1,
                _edFiDocString1
            );
            await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);

            // Update
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _defaultResourceName,
                _documentUuidGuid,
                _referentialIdGuid2,
                _edFiDocString2
            );
            _updateResult = await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_not_be_a_successful_update()
        {
            _updateResult!.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
        }

        [Test]
        public async Task It_should_not_have_changed_the_document()
        {
            var getResult = await CreateGetById()
                .GetById(
                    CreateGetRequest(_defaultResourceName, _documentUuidGuid),
                    Connection!,
                    Transaction!
                );
            (getResult as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Contain("\"abc\":1");
        }
    }

    [TestFixture]
    public class Given_an_update_of_the_same_document_with_two_overlapping_request : UpdateTests
    {
        private UpdateResult? _updateResult1;
        private UpdateResult? _updateResult2;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";
        private static readonly string _edFiDocString3 = """{"abc":3}""";

        [SetUp]
        public async Task Setup()
        {
            (_updateResult1, _updateResult2) = await OrchestrateOperations(
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    // Insert the original document
                    await CreateUpsert()
                        .Upsert(
                            CreateUpsertRequest(
                                _defaultResourceName,
                                _documentUuidGuid,
                                _referentialIdGuid,
                                _edFiDocString1
                            ),
                            connection,
                            transaction
                        );
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpdateRequest updateRequest = CreateUpdateRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString2
                    );
                    return await CreateUpdate().UpdateById(updateRequest, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpdateRequest updateRequest = CreateUpdateRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString3
                    );
                    return await CreateUpdate().UpdateById(updateRequest, connection, transaction);
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_update_for_1st_transaction()
        {
            _updateResult1!.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public void It_should_be_a_conflict_failure_for_2nd_transaction()
        {
            _updateResult2!.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
        }

        [Test]
        public async Task It_should_be_the_1st_update_found_by_get()
        {
            IGetRequest getRequest = CreateGetRequest(_defaultResourceName, _documentUuidGuid);
            GetResult? _getResult = await CreateGetById().GetById(getRequest, Connection!, Transaction!);

            (_getResult! as GetResult.GetSuccess)!.DocumentUuid.Value.Should().Be(_documentUuidGuid);
            (_getResult! as GetResult.GetSuccess)!.EdfiDoc.ToJsonString().Should().Be(_edFiDocString2);
        }
    }

    [TestFixture]
    public class Given_an_update_of_a_document_that_references_a_non_existent_document : UpdateTests
    {
        private UpdateResult? _updateResult;
        private List<UpsertResult> _upsertResults = new();

        private static readonly string _referencedResourceName = "ReferencedResource";
        private static readonly Guid _resourcedDocUuidGuid = Guid.NewGuid();
        private static readonly Guid _referencedRefIdGuid = Guid.NewGuid();
        private static readonly string _referencedDocString = """{"abc":1}""";

        private static readonly string _referencingResourceName = "ReferencingResource";
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            _upsertResults = new List<UpsertResult>();
            IUpsertRequest refUpsertRequest = CreateUpsertRequest(
                _referencedResourceName,
                _resourcedDocUuidGuid,
                _referencedRefIdGuid,
                _referencedDocString
            );
            _upsertResults.Add(await CreateUpsert().Upsert(refUpsertRequest, Connection!, Transaction!));

            // Add references
            Reference[] references = [new(_referencingResourceName, _referencedRefIdGuid)];

            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _referencingResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString,
                CreateDocumentReferences(references)
            );

            _upsertResults.Add(await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!));

            // Update
            string updatedReferencedDocString = """{"abc":3}""";
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _referencedResourceName,
                _resourcedDocUuidGuid,
                _referentialIdGuid,
                updatedReferencedDocString
            );
            _updateResult = await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_a_successful_inserts()
        {
            _upsertResults.Should().HaveCount(2);
            _upsertResults.ForEach(x => x.Should().BeOfType<UpsertResult.InsertSuccess>());
        }

        [Test]
        public void It_should_be_a_update_failure_reference()
        {
            _updateResult.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
        }
    }

    [TestFixture]
    public class Given_an_update_of_a_document_that_references_an_existing_document : UpdateTests
    {
        private UpdateResult? _updateResult;
        private List<UpsertResult> _upsertResults = new();

        private static readonly string _referencedResourceName = "ReferencedResource";
        private static readonly Guid _resourcedDocUuidGuid = Guid.NewGuid();
        private static readonly Guid _referencedRefIdGuid = Guid.NewGuid();
        private static readonly string _referencedDocString = """{"abc":1}""";

        private static readonly string _referencingResourceName = "ReferencingResource";
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            // First, insert the referenced document
            IUpsertRequest refUpsertRequest = CreateUpsertRequest(
                _referencedResourceName,
                _resourcedDocUuidGuid,
                _referencedRefIdGuid,
                _referencedDocString
            );
            _upsertResults.Add(await CreateUpsert().Upsert(refUpsertRequest, Connection!, Transaction!));

            // Ensure the referenced document is successfully inserted
            _upsertResults.Should().HaveCount(1);
            _upsertResults[0].Should().BeOfType<UpsertResult.InsertSuccess>();

            // Add references
            Reference[] references = { new (_referencingResourceName, _referencedRefIdGuid) };

            // Then, insert the referencing document that refers to the existing document
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _referencingResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString,
                CreateDocumentReferences(references)
            );

            _upsertResults.Add(await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!));

            // Update the referencing document
            string updatedReferencingDocString = """{"abc":3}""";
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _referencingResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                updatedReferencingDocString
            );
            _updateResult = await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_a_successful_update()
        {
            _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }
    }

    [TestFixture]
    public class Given_an_update_of_a_document_with_one_existing_and_one_non_existent_reference : UpdateTests
    {
        private UpdateResult? _updateResult;
        private List<UpsertResult> _upsertResults = new();

        private static readonly string _existingReferencedResourceName = "ExistingReferencedResource";
        private static readonly Guid _existingResourcedDocUuidGuid = Guid.NewGuid();
        private static readonly Guid _existingReferencedRefIdGuid = Guid.NewGuid();
        private static readonly string _existingReferencedDocString = """{"abc":1}""";
        
        private static readonly Guid _nonExistentReferencedRefIdGuid = Guid.NewGuid();

        private static readonly string _referencingResourceName = "ReferencingResource";
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            _upsertResults = new List<UpsertResult>();

            // First, insert the existing referenced document
            IUpsertRequest existingRefUpsertRequest = CreateUpsertRequest(
                _existingReferencedResourceName,
                _existingResourcedDocUuidGuid,
                _existingReferencedRefIdGuid,
                _existingReferencedDocString
            );
            _upsertResults.Add(await CreateUpsert().Upsert(existingRefUpsertRequest, Connection!, Transaction!));

            // Ensure the existing referenced document is successfully inserted
            _upsertResults.Should().HaveCount(1);
            _upsertResults[0].Should().BeOfType<UpsertResult.InsertSuccess>();

            // Add references: one existing and one non-existent
            Reference[] references = { new (_existingReferencedResourceName, _existingReferencedRefIdGuid) };

            // Then, insert the referencing document that refers to both existing and non-existent documents
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                _referencingResourceName,
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString,
                CreateDocumentReferences(references)
            );

            _upsertResults.Add(await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!));

            // Update the referencing document
            string updatedReferencingDocString = """{"abc":3}""";
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _referencingResourceName,
                _documentUuidGuid,
                _nonExistentReferencedRefIdGuid,
                updatedReferencingDocString
            );
            _updateResult = await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_a_successful_inserts()
        {
            _upsertResults.Should().HaveCount(2);
            _upsertResults.ForEach(x => x.Should().BeOfType<UpsertResult.InsertSuccess>());
        }

        [Test]
        public void It_should_be_a_update_failure_reference()
        {
            _updateResult.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
        }
    }

    [TestFixture]
    public class Given_an_update_of_a_subclass_document_referenced_by_an_existing_document_as_a_superclass : UpdateTests
    {
        private UpdateResult? _updateResult;
        private List<UpsertResult> _upsertResults = new();

        private static readonly string _superclassResourceName = "SuperclassResource";
        private static readonly Guid _superclassDocUuidGuid = Guid.NewGuid();
        private static readonly Guid _superclassRefIdGuid = Guid.NewGuid();
        private static readonly string _superclassDocString = """{"abc":1, "subClass": {"xyz":2}}""";

        private static readonly string _subclassResourceName = "SubclassResource";
        private static readonly Guid _subclassDocUuidGuid = Guid.NewGuid();
        private static readonly Guid _subclassRefIdGuid = Guid.NewGuid();
        private static readonly string _subclassDocString = """{"xyz":3}""";

        [SetUp]
        public async Task Setup()
        {
            _upsertResults = new List<UpsertResult>();

            IUpsertRequest superclassUpsertRequest = CreateUpsertRequest(
                _superclassResourceName,
                _superclassDocUuidGuid,
                _superclassRefIdGuid,
                _superclassDocString
            );
            _upsertResults.Add(await CreateUpsert().Upsert(superclassUpsertRequest, Connection!, Transaction!));

            _upsertResults.Should().HaveCount(1);
            _upsertResults[0].Should().BeOfType<UpsertResult.InsertSuccess>();

            Reference[] references = { new (_superclassResourceName, _superclassRefIdGuid) };

            IUpsertRequest subclassUpsertRequest = CreateUpsertRequest(
                _subclassResourceName,
                _subclassDocUuidGuid,
                _subclassRefIdGuid,
                _subclassDocString,
                CreateDocumentReferences(references)
            );

            _upsertResults.Add(await CreateUpsert().Upsert(subclassUpsertRequest, Connection!, Transaction!));

            string updatedSubclassDocString = """{"xyz":10}""";
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _subclassResourceName,
                _subclassDocUuidGuid,
                _subclassRefIdGuid,
                updatedSubclassDocString
            );

            _updateResult = await CreateUpdate().UpdateById(updateRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_a_successful_inserts()
        {
            _upsertResults.Should().HaveCount(2);
            _upsertResults.ForEach(x => x.Should().BeOfType<UpsertResult.InsertSuccess>());
        }

        [Test]
        public void It_should_be_a_successful_update()
        {
            _updateResult.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }
    }

    [TestFixture]
    public class Given_an_update_of_the_same_document_with_two_overlapping_request_but_also_with_different_references : UpdateTests
    {
        private UpdateResult? _updateResult1;
        private UpdateResult? _updateResult2;

        private static readonly string _existingReferencedResourceName = "ExistingReferencedResource";
        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly Guid _referentialIdGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";
        private static readonly string _edFiDocString3 = """{"abc":3}""";

        private static readonly string _referencingResourceName = "ReferencingResource";
        private static readonly Guid _refDocumentUuidGuid = Guid.NewGuid();
        private static readonly Guid _refReferentialIdGuid = Guid.NewGuid();
        private static readonly string _refEdFiDocString = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            (_updateResult1, _updateResult2) = await OrchestrateOperations(
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    // Insert the original document
                    await CreateUpsert()
                        .Upsert(
                            CreateUpsertRequest(
                                _defaultResourceName,
                                _documentUuidGuid,
                                _referentialIdGuid,
                                _edFiDocString1
                            ),
                            connection,
                            transaction
                        );

                    // Add references: one existing and one non-existent
                    Reference[] references = { new(_existingReferencedResourceName, _documentUuidGuid) };

                    IUpsertRequest upsertRequest = CreateUpsertRequest(
                        _referencingResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _refEdFiDocString,
                        CreateDocumentReferences(references)
                    );

                    await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpdateRequest updateRequest = CreateUpdateRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString2
                    );
                    return await CreateUpdate().UpdateById(updateRequest, connection, transaction);
                },
                async (NpgsqlConnection connection, NpgsqlTransaction transaction) =>
                {
                    IUpdateRequest updateRequest = CreateUpdateRequest(
                        _defaultResourceName,
                        _documentUuidGuid,
                        _referentialIdGuid,
                        _edFiDocString3
                    );
                    return await CreateUpdate().UpdateById(updateRequest, connection, transaction);
                }
            );
        }

        [Test]
        public void It_should_be_a_successful_update_for_1st_transaction()
        {
            _updateResult1!.Should().BeOfType<UpdateResult.UpdateSuccess>();
        }

        [Test]
        public void It_should_be_a_conflict_failure_for_2nd_transaction()
        {
            _updateResult2!.Should().BeOfType<UpdateResult.UpdateFailureWriteConflict>();
        }
    }

    // Future tests - from Meadowlark
    
    // given an update of a document that references an existing descriptor

    // given an update of a document that references a nonexisting descriptor

    // Future tests - new concurrency-based

    // given an update of a document that references an existing document that is concurrently deleted
}
