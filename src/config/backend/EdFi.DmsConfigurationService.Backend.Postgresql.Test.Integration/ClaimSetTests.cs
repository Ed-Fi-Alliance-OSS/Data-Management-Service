// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Test.Integration;

public class ClaimSetTests : DatabaseTest
{
    private readonly IClaimSetRepository _repository = new ClaimSetRepository(
        Configuration.DatabaseOptions,
        NullLogger<ClaimSetRepository>.Instance
    );

    [TestFixture]
    public class InsertTest : ClaimSetTests
    {
        private long _id;

        [SetUp]
        public async Task Setup()
        {
            ClaimSetInsertCommand claimSet = new() { Name = "Test ClaimSet" };

            var result = await _repository.InsertClaimSet(claimSet);
            result.Should().BeOfType<ClaimSetInsertResult.Success>();
            _id = (result as ClaimSetInsertResult.Success)!.Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task Should_get_test_claimSet_from_get_all()
        {
            var getResul = await _repository.QueryClaimSet(
                new PagingQuery() { Limit = 25, Offset = 0 },
                false
            );
            getResul.Should().BeOfType<ClaimSetQueryResult.Success>();

            object claimSetFromDb = ((ClaimSetQueryResult.Success)getResul).ClaimSetResponses.First();
            claimSetFromDb.Should().NotBeNull();
            claimSetFromDb.Should().BeOfType<ClaimSetResponseReduced>();

            var reducedResponse = (ClaimSetResponseReduced)claimSetFromDb;
            reducedResponse.Name.Should().Be("Test ClaimSet");
        }

        [Test]
        public async Task Should_get_test_claimSet_from_get_by_id()
        {
            var getByIdResult = await _repository.GetClaimSet(_id, false);
            getByIdResult.Should().BeOfType<ClaimSetGetResult.Success>();

            object claimSetFromDb = ((ClaimSetGetResult.Success)getByIdResult).ClaimSetResponse;
            claimSetFromDb.Should().BeOfType<ClaimSetResponseReduced>();

            var reducedResponse = (ClaimSetResponseReduced)claimSetFromDb;
            reducedResponse.Name.Should().Be("Test ClaimSet");
        }
    }

    [TestFixture]
    public class UpdateTests : ClaimSetTests
    {
        private ClaimSetInsertCommand _insertClaimSet = null!;
        private ClaimSetUpdateCommand _updateClaimSet = null!;

        [SetUp]
        public async Task Setup()
        {
            _insertClaimSet = new ClaimSetInsertCommand() { Name = "Test Insert ClaimSet" };

            _updateClaimSet = new ClaimSetUpdateCommand() { Name = "Test Update ClaimSet" };

            var insertResult = await _repository.InsertClaimSet(_insertClaimSet);
            insertResult.Should().BeOfType<ClaimSetInsertResult.Success>();

            _updateClaimSet.Id = (insertResult as ClaimSetInsertResult.Success)!.Id;
            _updateClaimSet.Name = "Test Update ClaimSet";

            var updateResult = await _repository.UpdateClaimSet(_updateClaimSet);
            updateResult.Should().BeOfType<ClaimSetUpdateResult.Success>();
        }

        [Test]
        public async Task Should_get_update_claimSet_from_get_all()
        {
            var getResul = await _repository.QueryClaimSet(
                new PagingQuery() { Limit = 25, Offset = 0 },
                false
            );
            getResul.Should().BeOfType<ClaimSetQueryResult.Success>();

            object claimSetFromDb = ((ClaimSetQueryResult.Success)getResul).ClaimSetResponses.First();
            claimSetFromDb.Should().NotBeNull();
            claimSetFromDb.Should().BeOfType<ClaimSetResponseReduced>();

            var reducedResponse = (ClaimSetResponseReduced)claimSetFromDb;
            reducedResponse.Name.Should().Be("Test Update ClaimSet");
        }

        [Test]
        public async Task Should_get_test_claimSet_from_get_by_id()
        {
            var getByIdResult = await _repository.GetClaimSet(_updateClaimSet.Id, false);
            getByIdResult.Should().BeOfType<ClaimSetGetResult.Success>();

            object claimSetFromDb = ((ClaimSetGetResult.Success)getByIdResult).ClaimSetResponse;
            claimSetFromDb.Should().BeOfType<ClaimSetResponseReduced>();

            var reducedResponse = (ClaimSetResponseReduced)claimSetFromDb;
            reducedResponse.Name.Should().Be("Test Update ClaimSet");
        }
    }

    [TestFixture]
    public class DeleteTests : ClaimSetTests
    {
        private long _id1;
        private long _id2;

        [SetUp]
        public async Task Setup()
        {
            var insertResult1 = await _repository.InsertClaimSet(
                new ClaimSetInsertCommand() { Name = "Test One" }
            );
            _id1 = ((ClaimSetInsertResult.Success)insertResult1).Id;

            var insertResult2 = await _repository.InsertClaimSet(
                new ClaimSetInsertCommand() { Name = "Test Two" }
            );
            _id2 = ((ClaimSetInsertResult.Success)insertResult2).Id;

            var result = await _repository.DeleteClaimSet(_id1);
            result.Should().BeOfType<ClaimSetDeleteResult.Success>();
        }

        [Test]
        public async Task Should_get_one_test_claimSet_from_get_all()
        {
            var result = await _repository.QueryClaimSet(new PagingQuery() { Limit = 25, Offset = 0 }, false);
            result.Should().BeOfType<ClaimSetQueryResult.Success>();

            ((ClaimSetQueryResult.Success)result).ClaimSetResponses.Count().Should().Be(1);
        }

        [Test]
        public async Task Should_get_test_claimSet_from_get_by_id()
        {
            var result1 = await _repository.GetClaimSet(_id1, false);
            result1.Should().BeOfType<ClaimSetGetResult.FailureNotFound>();

            var result2 = await _repository.GetClaimSet(_id2, false);
            result2.Should().BeOfType<ClaimSetGetResult.Success>();
        }
    }

    [TestFixture]
    public class ExportTest : ClaimSetTests
    {
        private long _id;

        [SetUp]
        public async Task Setup()
        {
            ClaimSetInsertCommand claimSet = new() { Name = "Test Export ClaimSet" };

            var result = await _repository.InsertClaimSet(claimSet);
            result.Should().BeOfType<ClaimSetInsertResult.Success>();
            _id = (result as ClaimSetInsertResult.Success)!.Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task Should_export_claimSet()
        {
            var result = await _repository.Export(_id);
            result.Should().BeOfType<ClaimSetExportResult.Success>();

            var valueFromDb = ((ClaimSetExportResult.Success)result).ClaimSetExportResponse;
            valueFromDb.Name.Should().Be("Test Export ClaimSet");
        }
    }

    [TestFixture]
    public class ImportTest : ClaimSetTests
    {
        private long _id;

        [SetUp]
        public async Task Setup()
        {
            string resourceClaimsJson = """
                {
                    "Resource": "Value"
                }
                """;
            JsonElement resourceClaims = JsonDocument.Parse(resourceClaimsJson).RootElement;

            ClaimSetImportCommand claimSet = new()
            {
                Name = "Test Import ClaimSet",
                ResourceClaims = resourceClaims,
            };

            var result = await _repository.Import(claimSet);
            result.Should().BeOfType<ClaimSetImportResult.Success>();
            _id = (result as ClaimSetImportResult.Success)!.Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task Should_get_one_test_claimSet_from_get_all()
        {
            var result = await _repository.QueryClaimSet(new PagingQuery() { Limit = 25, Offset = 0 }, false);
            result.Should().BeOfType<ClaimSetQueryResult.Success>();

            ((ClaimSetQueryResult.Success)result).ClaimSetResponses.Count().Should().Be(1);
        }

        [Test]
        public async Task Should_get_test_claimSet_from_get_by_id()
        {
            var getByIdResult = await _repository.GetClaimSet(_id, true);
            getByIdResult.Should().BeOfType<ClaimSetGetResult.Success>();

            object claimSetFromDb = ((ClaimSetGetResult.Success)getByIdResult).ClaimSetResponse;
            claimSetFromDb.Should().BeOfType<ClaimSetResponse>();

            var response = (ClaimSetResponse)claimSetFromDb;
            response.Name.Should().Be("Test Import ClaimSet");
            response.ResourceClaims.Should().NotBeNull();
        }
    }

    [TestFixture]
    public class CopyTest : ClaimSetTests
    {
        private long _id;
        private long _idCopy;

        [SetUp]
        public async Task Setup()
        {
            ClaimSetInsertCommand claimSet = new() { Name = "Original ClaimSet" };

            var result = await _repository.InsertClaimSet(claimSet);
            result.Should().BeOfType<ClaimSetInsertResult.Success>();
            _id = (result as ClaimSetInsertResult.Success)!.Id;
            _id.Should().BeGreaterThan(0);

            ClaimSetCopyCommand command = new() { OriginalId = _id, Name = "Copy Test ClaimSet" };

            var copy = await _repository.Copy(command);
            copy.Should().BeOfType<ClaimSetCopyResult.Success>();
            _idCopy = (copy as ClaimSetCopyResult.Success)!.Id;
            _idCopy.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task Should_get_two_claimSet_from_get_all()
        {
            var result = await _repository.QueryClaimSet(new PagingQuery() { Limit = 25, Offset = 0 }, false);
            result.Should().BeOfType<ClaimSetQueryResult.Success>();

            ((ClaimSetQueryResult.Success)result).ClaimSetResponses.Count().Should().Be(2);
        }

        [Test]
        public async Task Should_get_claimSet_from_get_by_id()
        {
            var getByIdResult1 = await _repository.GetClaimSet(_id, false);
            getByIdResult1.Should().BeOfType<ClaimSetGetResult.Success>();

            object claimSetFromDb1 = ((ClaimSetGetResult.Success)getByIdResult1).ClaimSetResponse;
            claimSetFromDb1.Should().BeOfType<ClaimSetResponseReduced>();

            var reducedResponse1 = (ClaimSetResponseReduced)claimSetFromDb1;
            reducedResponse1.Name.Should().Be("Original ClaimSet");

            var getByIdResult2 = await _repository.GetClaimSet(_idCopy, false);
            getByIdResult2.Should().BeOfType<ClaimSetGetResult.Success>();

            object claimSetFromDb2 = ((ClaimSetGetResult.Success)getByIdResult2).ClaimSetResponse;
            claimSetFromDb2.Should().BeOfType<ClaimSetResponseReduced>();

            var reducedResponse2 = (ClaimSetResponseReduced)claimSetFromDb2;
            reducedResponse2.Name.Should().Be("Copy Test ClaimSet");
        }
    }
}
