// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Claim = EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy.Claim;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

[TestFixture]
public class ClaimsDataLoaderTests
{
    private IClaimsProvider _claimsProvider = null!;
    private IClaimSetRepository _claimSetRepository = null!;
    private IClaimsHierarchyRepository _claimsHierarchyRepository = null!;
    private IClaimsTableValidator _claimsTableValidator = null!;
    private IClaimsDocumentRepository _claimsDocumentRepository = null!;
    private ILogger<Backend.ClaimsDataLoader.ClaimsDataLoader> _logger = null!;
    private Backend.ClaimsDataLoader.ClaimsDataLoader _claimsDataLoader = null!;

    [SetUp]
    public void Setup()
    {
        _claimsProvider = A.Fake<IClaimsProvider>();
        _claimSetRepository = A.Fake<IClaimSetRepository>();
        _claimsHierarchyRepository = A.Fake<IClaimsHierarchyRepository>();
        _claimsTableValidator = A.Fake<IClaimsTableValidator>();
        _claimsDocumentRepository = A.Fake<IClaimsDocumentRepository>();
        _logger = A.Fake<ILogger<Backend.ClaimsDataLoader.ClaimsDataLoader>>();

        // Default setup - tables are empty
        A.CallTo(() => _claimsTableValidator.AreClaimsTablesEmptyAsync()).Returns(true);

        _claimsDataLoader = new Backend.ClaimsDataLoader.ClaimsDataLoader(
            _claimsProvider,
            _claimSetRepository,
            _claimsHierarchyRepository,
            _claimsTableValidator,
            _claimsDocumentRepository,
            _logger
        );
    }

    [Test]
    public async Task Given_provider_returns_null_It_should_return_validation_failure()
    {
        // Arrange
        A.CallTo(() => _claimsProvider.GetClaimsDocumentNodes()).Returns(null!);

        // Act
        var result = await _claimsDataLoader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.ValidationFailure>());
        var failure = (ClaimsDataLoadResult.ValidationFailure)result;
        Assert.That(failure.Errors, Contains.Item("Failed to load claims document from provider"));
    }

    [Test]
    public async Task Given_provider_has_invalid_claims_It_should_return_validation_failure()
    {
        // Arrange
        A.CallTo(() => _claimsProvider.IsClaimsValid).Returns(false);
        A.CallTo(() => _claimsProvider.ClaimsFailures)
            .Returns(
                new List<ClaimsFailure>
                {
                    new("Syntax", "Invalid JSON syntax"),
                    new("Schema", "Missing required field"),
                }
            );

        // When GetClaimsDocumentNodes is called and IsClaimsValid is false, it should throw
        A.CallTo(() => _claimsProvider.GetClaimsDocumentNodes())
            .Throws(new InvalidOperationException("Claims validation failed"));

        // Act
        var result = await _claimsDataLoader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.ValidationFailure>());
        var failure = (ClaimsDataLoadResult.ValidationFailure)result;
        Assert.That(failure.Errors.Count, Is.EqualTo(2));
        Assert.That(failure.Errors[0], Is.EqualTo("Syntax: Invalid JSON syntax"));
        Assert.That(failure.Errors[1], Is.EqualTo("Schema: Missing required field"));
    }

    [Test]
    public async Task Given_successful_load_It_should_log_information()
    {
        // Arrange
        var claimSetsJson = JsonNode.Parse(
            """
            [
                { "claimSetName": "Test1", "isSystemReserved": true },
                { "claimSetName": "Test2", "isSystemReserved": false }
            ]
            """
        );

        var hierarchyJson = JsonNode.Parse(
            """
            [
                { "name": "domain1", "claims": [] }
            ]
            """
        );

        var claimsNodes = new ClaimsDocument(claimSetsJson!, hierarchyJson!);
        A.CallTo(() => _claimsProvider.GetClaimsDocumentNodes()).Returns(claimsNodes);
        A.CallTo(() => _claimsProvider.IsClaimsValid).Returns(true);

        A.CallTo(() => _claimSetRepository.InsertClaimSet(A<ClaimSetInsertCommand>._))
            .Returns(new ClaimSetInsertResult.Success(1));

        A.CallTo(() =>
                _claimsHierarchyRepository.SaveClaimsHierarchy(
                    A<List<Claim>>._,
                    A<DateTime>._,
                    A<System.Data.Common.DbTransaction?>._
                )
            )
            .Returns(new ClaimsHierarchySaveResult.Success());

        // Act
        var result = await _claimsDataLoader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.Success>());
        var success = (ClaimsDataLoadResult.Success)result;
        Assert.That(success.ClaimSetsLoaded, Is.EqualTo(2));
        Assert.That(success.HierarchyLoaded, Is.True);
    }

    [Test]
    public async Task Given_claimset_insert_fails_It_should_return_database_failure()
    {
        // Arrange
        var claimSetsJson = JsonNode.Parse("""[{ "claimSetName": "Test1", "isSystemReserved": true }]""");
        var hierarchyJson = JsonNode.Parse("""[]""");

        var claimsNodes = new ClaimsDocument(claimSetsJson!, hierarchyJson!);
        A.CallTo(() => _claimsProvider.GetClaimsDocumentNodes()).Returns(claimsNodes);
        A.CallTo(() => _claimsProvider.IsClaimsValid).Returns(true);

        A.CallTo(() => _claimSetRepository.InsertClaimSet(A<ClaimSetInsertCommand>._))
            .Returns(new ClaimSetInsertResult.FailureUnknown("Database error"));

        // Act
        var result = await _claimsDataLoader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.DatabaseFailure>());
        var failure = (ClaimsDataLoadResult.DatabaseFailure)result;
        Assert.That(failure.ErrorMessage, Contains.Substring("Failed to load claim set Test1"));
    }

    [Test]
    public async Task Given_exception_during_load_It_should_return_unexpected_failure()
    {
        // Arrange
        A.CallTo(() => _claimsProvider.GetClaimsDocumentNodes())
            .Throws(new InvalidOperationException("Unexpected error"));

        // Act
        var result = await _claimsDataLoader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.UnexpectedFailure>());
        var failure = (ClaimsDataLoadResult.UnexpectedFailure)result;
        Assert.That(failure.ErrorMessage, Is.EqualTo("Invalid operation during claims data loading"));
        Assert.That(failure.Exception, Is.Not.Null);
        Assert.That(failure.Exception, Is.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task Given_duplicate_claimset_It_should_continue_loading()
    {
        // Arrange
        var claimSetsJson = JsonNode.Parse(
            """
            [
                { "claimSetName": "Test1", "isSystemReserved": true },
                { "claimSetName": "Test2", "isSystemReserved": false }
            ]
            """
        );

        var hierarchyJson = JsonNode.Parse("""[]""");

        var claimsNodes = new ClaimsDocument(claimSetsJson!, hierarchyJson!);
        A.CallTo(() => _claimsProvider.GetClaimsDocumentNodes()).Returns(claimsNodes);
        A.CallTo(() => _claimsProvider.IsClaimsValid).Returns(true);

        // First claim set returns duplicate, second succeeds
        A.CallTo(() =>
                _claimSetRepository.InsertClaimSet(
                    A<ClaimSetInsertCommand>.That.Matches(x => x.Name == "Test1")
                )
            )
            .Returns(new ClaimSetInsertResult.FailureDuplicateClaimSetName());
        A.CallTo(() =>
                _claimSetRepository.InsertClaimSet(
                    A<ClaimSetInsertCommand>.That.Matches(x => x.Name == "Test2")
                )
            )
            .Returns(new ClaimSetInsertResult.Success(2));

        A.CallTo(() =>
                _claimsHierarchyRepository.SaveClaimsHierarchy(
                    A<List<Claim>>._,
                    A<DateTime>._,
                    A<System.Data.Common.DbTransaction?>._
                )
            )
            .Returns(new ClaimsHierarchySaveResult.Success());

        // Act
        var result = await _claimsDataLoader.LoadInitialClaimsAsync();

        // Assert
        Assert.That(result, Is.TypeOf<ClaimsDataLoadResult.Success>());
        var success = (ClaimsDataLoadResult.Success)result;
        Assert.That(success.ClaimSetsLoaded, Is.EqualTo(1)); // Only one succeeded
        Assert.That(success.HierarchyLoaded, Is.True);
    }
}
