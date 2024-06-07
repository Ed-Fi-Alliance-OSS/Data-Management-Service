// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

[TestFixture]
public class UpdateTests : DatabaseTest
{
    [TestFixture]
    public class GivenAnUpdateOfAnExistingDocument : UpdateTests
    {
        private UpdateResult? _updateResult;
        private GetResult? _getResult;

        private static readonly Guid _documentUuidGuid = Guid.NewGuid();
        private static readonly string _edFiDocString1 = """{"abc":1}""";
        private static readonly string _edFiDocString2 = """{"abc":2}""";

        [SetUp]
        public async Task Setup()
        {
            // Create
            IUpsertRequest upsertRequest = CreateUpsertRequest(_documentUuidGuid, _edFiDocString1);
            await CreateUpsert(DataSource!).Upsert(upsertRequest);

            // Update
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _documentUuidGuid,
                upsertRequest.DocumentInfo.ReferentialId.Value,
                _edFiDocString2
            );
            _updateResult = await CreateUpdate(DataSource!).UpdateById(updateRequest);

            // Confirm change was made
            IGetRequest getRequest = CreateGetRequest(_documentUuidGuid);
            _getResult = await CreateGetById(DataSource!).GetById(getRequest);
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
    public class GivenAnUpdateOfAnExistingDocumentWithDifferentReferentialId : UpdateTests
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
            IUpsertRequest upsertRequest = CreateUpsertRequest(_documentUuidGuid, _edFiDocString1);
            await CreateUpsert(DataSource!).Upsert(upsertRequest);

            // Update
            IUpdateRequest updateRequest = CreateUpdateRequest(
                _documentUuidGuid,
                _referentialIdGuid,
                _edFiDocString2
            );
            _updateResult = await CreateUpdate(DataSource!).UpdateById(updateRequest);

            // Confirm change was made
            IGetRequest getRequest = CreateGetRequest(_documentUuidGuid);
            _getResult = await CreateGetById(DataSource!).GetById(getRequest);
        }

        [Test]
        public void It_should_not_be_a_successful_update()
        {
            _updateResult!.Should().BeOfType<UpdateResult.UpdateFailureImmutableIdentity>();
        }
    }
}
