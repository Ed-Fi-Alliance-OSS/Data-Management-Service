// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using EdFi.DmsConfigurationService.Backend.AuthorizationMetadata;
using EdFi.DmsConfigurationService.Backend.Introspection;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Token;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.TokenInfo;

[TestFixture]
public class TokenInfoProviderTests
{
    private IEducationOrganizationRepository _educationOrganizationRepository = null!;
    private IClaimsHierarchyRepository _claimsHierarchyRepository = null!;
    private IAuthorizationMetadataResponseFactory _authorizationMetadataResponseFactory = null!;
    private IApiClientRepository _apiClientRepository = null!;
    private ILogger<TokenInfoProvider> _logger = null!;
    private TokenInfoProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _educationOrganizationRepository = A.Fake<IEducationOrganizationRepository>();
        _claimsHierarchyRepository = A.Fake<IClaimsHierarchyRepository>();
        _authorizationMetadataResponseFactory = A.Fake<IAuthorizationMetadataResponseFactory>();
        _apiClientRepository = A.Fake<IApiClientRepository>();
        _logger = A.Fake<ILogger<TokenInfoProvider>>();

        _provider = new TokenInfoProvider(
            _educationOrganizationRepository,
            _claimsHierarchyRepository,
            _authorizationMetadataResponseFactory,
            _apiClientRepository,
            _logger
        );
    }

    [Test]
    public async Task GetTokenInfoAsync_WithMissingClaimSetName_ReturnsEmptyResourcesAndServices()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );
        SetupApiClientAndOrgs();
        SetupClaimsHierarchy();
        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        "",
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(new AuthorizationMetadataResponse(new List<ClaimSetMetadata>()));
        // Act
        var result = await _provider.GetTokenInfoAsync(token);
        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().BeEmpty();
        result.Services.Should().BeEmpty();
    }

    [Test]
    public async Task GetTokenInfoAsync_WithMissingClientId_ReturnsNull()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );
        // Act
        var result = await _provider.GetTokenInfoAsync(token);
        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task GetTokenInfoAsync_WithMissingEducationOrganizationIds_ReturnsEmptyOrgs()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "",
            namespacePrefixes: "uri://ed-fi.org"
        );
        SetupApiClientAndOrgs();
        SetupClaimsHierarchy();
        SetupAuthorizationMetadata();
        // Act
        var result = await _provider.GetTokenInfoAsync(token);
        // Assert
        result.Should().NotBeNull();
        result!.EducationOrganizations.Should().BeEmpty();
    }

    [Test]
    public async Task GetTokenInfoAsync_WithMissingNamespacePrefixes_ReturnsEmptyPrefixes()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: ""
        );
        SetupApiClientAndOrgs();
        SetupClaimsHierarchy();
        SetupAuthorizationMetadata();
        // Act
        var result = await _provider.GetTokenInfoAsync(token);
        // Assert
        result.Should().NotBeNull();
        result!.NamespacePrefixes.Should().BeEmpty();
    }

    [Test]
    public async Task GetTokenInfoAsync_WithUnknownClaimSetName_ReturnsEmptyResourcesAndServices()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "UnknownClaimSet",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );
        SetupApiClientAndOrgs();
        SetupClaimsHierarchy();
        A.CallTo(() => _authorizationMetadataResponseFactory.Create("UnknownClaimSet", A<List<Backend.Models.ClaimsHierarchy.Claim>>._))
            .Returns(new AuthorizationMetadataResponse(new List<ClaimSetMetadata>()));
        // Act
        var result = await _provider.GetTokenInfoAsync(token);
        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().BeEmpty();
        result.Services.Should().BeEmpty();
    }

    [Test]
    public async Task GetTokenInfoAsync_WithUnknownClaimName_FallbacksToClaimName()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );
        SetupApiClientAndOrgs();
        SetupClaimsHierarchy(new List<Backend.Models.ClaimsHierarchy.Claim>());
        var claimSet = new ClaimSetMetadata(
            "SIS Vendor",
            new List<ClaimSetMetadata.Claim> { new("custom://some-other-format/resource", 1) },
            new List<ClaimSetMetadata.Authorization>
            {
                new(
                    1,
                    new[]
                    {
                        new ClaimSetMetadata.Action(
                            "Read",
                            new[] { new ClaimSetMetadata.AuthorizationStrategy("NoFurtherAuthorizationRequired") }
                        ),
                    }
                ),
            }
        );
        A.CallTo(() => _authorizationMetadataResponseFactory.Create("SIS Vendor", A<List<Backend.Models.ClaimsHierarchy.Claim>>._))
            .Returns(new AuthorizationMetadataResponse(new List<ClaimSetMetadata> { claimSet }));
        // Act
        var result = await _provider.GetTokenInfoAsync(token);
        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().ContainSingle(r => r.Resource == "custom://some-other-format/resource");
    }

    [Test]
    public async Task GetTokenInfoAsync_WithAlternateIdentityClaimsPrefix_ConvertsToResourcePath()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );
        SetupApiClientAndOrgs();
        SetupClaimsHierarchy(new List<Backend.Models.ClaimsHierarchy.Claim>());
        var claimSet = new ClaimSetMetadata(
            "SIS Vendor",
            new List<ClaimSetMetadata.Claim> { new("http://ed-fi.org/identity/claims/ed-fi/academicWeek", 1) },
            new List<ClaimSetMetadata.Authorization>
            {
                new(
                    1,
                    new[]
                    {
                        new ClaimSetMetadata.Action(
                            "Read",
                            new[] { new ClaimSetMetadata.AuthorizationStrategy("NoFurtherAuthorizationRequired") }
                        ),
                    }
                ),
            }
        );
        A.CallTo(() => _authorizationMetadataResponseFactory.Create("SIS Vendor", A<List<Backend.Models.ClaimsHierarchy.Claim>>._))
            .Returns(new AuthorizationMetadataResponse(new List<ClaimSetMetadata> { claimSet }));
        // Act
        var result = await _provider.GetTokenInfoAsync(token);
        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().ContainSingle(r => r.Resource == "/ed-fi/academicWeeks");
    }

    [Test]
    public async Task GetTokenInfoAsync_WithDomainsPrefixAndEdFiDescriptors_ConvertsToDescriptorsPath()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );
        SetupApiClientAndOrgs();
        SetupClaimsHierarchy(new List<Backend.Models.ClaimsHierarchy.Claim>());
        var claimSet = new ClaimSetMetadata(
            "SIS Vendor",
            new List<ClaimSetMetadata.Claim> { new("http://ed-fi.org/identity/claims/domains/edFiDescriptors", 1) },
            new List<ClaimSetMetadata.Authorization>
            {
                new(
                    1,
                    new[]
                    {
                        new ClaimSetMetadata.Action(
                            "Read",
                            new[] { new ClaimSetMetadata.AuthorizationStrategy("NoFurtherAuthorizationRequired") }
                        ),
                    }
                ),
            }
        );
        A.CallTo(() => _authorizationMetadataResponseFactory.Create("SIS Vendor", A<List<Backend.Models.ClaimsHierarchy.Claim>>._))
            .Returns(new AuthorizationMetadataResponse(new List<ClaimSetMetadata> { claimSet }));
        // Act
        var result = await _provider.GetTokenInfoAsync(token);
        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().ContainSingle(r => r.Resource == "/ed-fi/descriptors");
    }

    [Test]
    public async Task GetTokenInfoAsync_WhenClaimsHierarchyThrowsException_ReturnsEmptyResourcesAndServices()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );
        SetupApiClientAndOrgs();
        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Throws(new Exception("Claims hierarchy error"));
        // Act
        var result = await _provider.GetTokenInfoAsync(token);
        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().BeEmpty();
        result.Services.Should().BeEmpty();
    }

    [Test]
    public async Task GetTokenInfoAsync_WhenEducationOrganizationRepositoryThrowsException_ReturnsNull()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );
        SetupApiClientAndOrgs(throwOnOrgs: true);
        SetupClaimsHierarchy();
        SetupAuthorizationMetadata();
        // Act
        var result = await _provider.GetTokenInfoAsync(token);
        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task GetTokenInfoAsync_WhenApiClientNotFound_ReturnsNull()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "notfound-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );
        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("notfound-client"))
            .Returns(new ApiClientGetResult.FailureNotFound());
        // Act
        var result = await _provider.GetTokenInfoAsync(token);
        // Assert
        result.Should().BeNull();
    }

    // --- Helper methods for setup ---
    private void SetupApiClientAndOrgs(bool throwOnOrgs = false)
    {
        A.CallTo(() => _apiClientRepository.GetApiClientByClientId(A<string>._))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );
        if (throwOnOrgs)
        {
            A.CallTo(() => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._))
                .Throws(new Exception("DB error"));
        }
        else
        {
            A.CallTo(() => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._))
                .Returns(new List<TokenInfoEducationOrganization>());
        }
    }

    private void SetupClaimsHierarchy(List<Backend.Models.ClaimsHierarchy.Claim>? claims = null)
    {
        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(
                new ClaimsHierarchyGetResult.Success(
                    claims ?? new List<Backend.Models.ClaimsHierarchy.Claim>(),
                    DateTime.UtcNow,
                    1
                )
            );
    }

    private void SetupAuthorizationMetadata()
    {
        var claimSet = new ClaimSetMetadata(
            "SIS Vendor",
            new List<ClaimSetMetadata.Claim> { new("http://ed-fi.org/identity/claims/ed-fi/students", 1) },
            new List<ClaimSetMetadata.Authorization>
            {
                new(
                    1,
                    new[]
                    {
                        new ClaimSetMetadata.Action(
                            "Read",
                            new[] { new ClaimSetMetadata.AuthorizationStrategy("NoFurtherAuthorizationRequired") }
                        ),
                    }
                ),
            }
        );
        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        "SIS Vendor",
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(new AuthorizationMetadataResponse(new List<ClaimSetMetadata> { claimSet }));
    }

    [Test]
    public async Task GetTokenInfoAsync_WithInvalidToken_ReturnsNull()
    {
        // Arrange
        var invalidToken = "invalid-token";

        // Act
        var result = await _provider.GetTokenInfoAsync(invalidToken);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task GetTokenInfoAsync_WithValidToken_ReturnsTokenInfo()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901,255902",
            namespacePrefixes: "uri://ed-fi.org"
        );

        var educationOrganizations = new List<TokenInfoEducationOrganization>
        {
            new()
            {
                EducationOrganizationId = 255901,
                NameOfInstitution = "Test School",
                Type = "edfi.School",
                LocalEducationAgencyId = 255950,
            },
        };

        var claimsHierarchy = new List<Backend.Models.ClaimsHierarchy.Claim>();
        var authorizationMetadata = new AuthorizationMetadataResponse(
            new List<ClaimSetMetadata>
            {
                new(
                    "SIS Vendor",
                    new List<ClaimSetMetadata.Claim>
                    {
                        new("http://ed-fi.org/identity/claims/ed-fi/students", 1),
                    },
                    new List<ClaimSetMetadata.Authorization>
                    {
                        new(
                            1,
                            new[]
                            {
                                new ClaimSetMetadata.Action(
                                    "Create",
                                    new[]
                                    {
                                        new ClaimSetMetadata.AuthorizationStrategy(
                                            "NoFurtherAuthorizationRequired"
                                        ),
                                    }
                                ),
                                new ClaimSetMetadata.Action(
                                    "Read",
                                    new[]
                                    {
                                        new ClaimSetMetadata.AuthorizationStrategy(
                                            "NoFurtherAuthorizationRequired"
                                        ),
                                    }
                                ),
                            }
                        ),
                    }
                ),
            }
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(educationOrganizations);

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(new ClaimsHierarchyGetResult.Success(claimsHierarchy, DateTime.UtcNow, 1));

        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        "SIS Vendor",
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(authorizationMetadata);

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.Active.Should().BeTrue();
        result.ClientId.Should().Be("test-client");
        result.ClaimSet.Name.Should().Be("SIS Vendor");
        result.NamespacePrefixes.Should().Contain("uri://ed-fi.org");
        result.EducationOrganizations.Should().HaveCount(1);
        result.EducationOrganizations[0].EducationOrganizationId.Should().Be(255901);
        result.Resources.Should().NotBeEmpty();
    }

    [Test]
    public async Task GetTokenInfoAsync_WithExpiredToken_ReturnsInactiveTokenInfo()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org",
            expiresInMinutes: -60 // Expired 1 hour ago
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(new List<TokenInfoEducationOrganization>());

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(
                new ClaimsHierarchyGetResult.Success(
                    new List<Backend.Models.ClaimsHierarchy.Claim>(),
                    DateTime.UtcNow,
                    1
                )
            );

        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        A<string>._,
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(new AuthorizationMetadataResponse(new List<ClaimSetMetadata>()));

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.Active.Should().BeFalse();
    }

    [Test]
    public async Task GetTokenInfoAsync_WithNonExistentClient_ReturnsNull()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "non-existent-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("non-existent-client"))
            .Returns(new ApiClientGetResult.FailureNotFound());

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task GetTokenInfoAsync_WithServiceClaims_ReturnsServices()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "Service Provider",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );

        var claimsHierarchy = new List<Backend.Models.ClaimsHierarchy.Claim>();
        var authorizationMetadata = new AuthorizationMetadataResponse(
            new List<ClaimSetMetadata>
            {
                new(
                    "Service Provider",
                    new List<ClaimSetMetadata.Claim>
                    {
                        new("http://ed-fi.org/identity/claims/services/identity", 1),
                        new("http://ed-fi.org/identity/claims/services/rostering", 2),
                    },
                    new List<ClaimSetMetadata.Authorization>
                    {
                        new(
                            1,
                            new[]
                            {
                                new ClaimSetMetadata.Action(
                                    "Create",
                                    new[]
                                    {
                                        new ClaimSetMetadata.AuthorizationStrategy(
                                            "NoFurtherAuthorizationRequired"
                                        ),
                                    }
                                ),
                            }
                        ),
                        new(
                            2,
                            new[]
                            {
                                new ClaimSetMetadata.Action(
                                    "Read",
                                    new[]
                                    {
                                        new ClaimSetMetadata.AuthorizationStrategy(
                                            "NoFurtherAuthorizationRequired"
                                        ),
                                    }
                                ),
                            }
                        ),
                    }
                ),
            }
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(new List<TokenInfoEducationOrganization>());

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(new ClaimsHierarchyGetResult.Success(claimsHierarchy, DateTime.UtcNow, 1));

        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        "Service Provider",
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(authorizationMetadata);

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.Services.Should().HaveCount(2);

        var identityService = (result.Services ?? Enumerable.Empty<TokenInfoService>()).FirstOrDefault(s => s.Service == "identity");
        identityService.Should().NotBeNull();
        identityService!.Operations.Should().Contain("Create");

        var rosteringService = (result.Services ?? Enumerable.Empty<TokenInfoService>()).FirstOrDefault(s => s.Service == "rostering");
        rosteringService.Should().NotBeNull();
        rosteringService!.Operations.Should().Contain("Read");
    }

    [Test]
    public async Task GetTokenInfoAsync_WithEmptyNamespacePrefixes_ReturnsEmptyList()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "" // Empty namespace prefixes
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(new List<TokenInfoEducationOrganization>());

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(
                new ClaimsHierarchyGetResult.Success(
                    new List<Backend.Models.ClaimsHierarchy.Claim>(),
                    DateTime.UtcNow,
                    1
                )
            );

        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        A<string>._,
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(new AuthorizationMetadataResponse(new List<ClaimSetMetadata>()));

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.NamespacePrefixes.Should().BeEmpty();
    }

    [Test]
    public async Task GetTokenInfoAsync_WithEmptyEducationOrganizationIds_ReturnsEmptyList()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "", // Empty education organization IDs
            namespacePrefixes: "uri://ed-fi.org"
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(new List<TokenInfoEducationOrganization>());

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(
                new ClaimsHierarchyGetResult.Success(
                    new List<Backend.Models.ClaimsHierarchy.Claim>(),
                    DateTime.UtcNow,
                    1
                )
            );

        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        A<string>._,
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(new AuthorizationMetadataResponse(new List<ClaimSetMetadata>()));

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.EducationOrganizations.Should().BeEmpty();
    }

    [Test]
    public async Task GetTokenInfoAsync_WithInvalidEducationOrganizationIds_FiltersOutInvalidValues()
    {
        // Arrange - includes valid ID, invalid string, zero, and negative-like scenarios
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901,invalid,0,255902",
            namespacePrefixes: "uri://ed-fi.org"
        );

        var educationOrganizations = new List<TokenInfoEducationOrganization>
        {
            new()
            {
                EducationOrganizationId = 255901,
                NameOfInstitution = "Test School 1",
                Type = "edfi.School",
            },
            new()
            {
                EducationOrganizationId = 255902,
                NameOfInstitution = "Test School 2",
                Type = "edfi.School",
            },
        };

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(educationOrganizations);

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(
                new ClaimsHierarchyGetResult.Success(
                    new List<Backend.Models.ClaimsHierarchy.Claim>(),
                    DateTime.UtcNow,
                    1
                )
            );

        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        A<string>._,
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(new AuthorizationMetadataResponse(new List<ClaimSetMetadata>()));

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        // The repository was called with only valid IDs (255901 and 255902)
        A.CallTo(
                () =>
                    _educationOrganizationRepository.GetEducationOrganizationsAsync(
                        A<IEnumerable<long>>.That.Matches(ids =>
                            ids.Count() == 2 && ids.Contains(255901) && ids.Contains(255902)
                        )
                    )
            )
            .MustHaveHappenedOnceExactly();
    }

    [Test]
    public async Task GetTokenInfoAsync_WhenClaimsHierarchyFails_ReturnsEmptyResourcesAndServices()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(new List<TokenInfoEducationOrganization>());

        // Claims hierarchy fails
        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(new ClaimsHierarchyGetResult.FailureUnknown("Database error"));

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().BeEmpty();
        result.Services.Should().BeEmpty();
    }

    [Test]
    public async Task GetTokenInfoAsync_WhenClaimHasNoMatchingAuthorization_SkipsClaim()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );

        var claimsHierarchy = new List<Backend.Models.ClaimsHierarchy.Claim>();
        var authorizationMetadata = new AuthorizationMetadataResponse(
            new List<ClaimSetMetadata>
            {
                new(
                    "SIS Vendor",
                    new List<ClaimSetMetadata.Claim>
                    {
                        // Claim with authorization ID that doesn't match any authorization
                        new("http://ed-fi.org/identity/claims/ed-fi/students", 999),
                    },
                    new List<ClaimSetMetadata.Authorization>
                    {
                        // Authorization with different ID
                        new(
                            1,
                            new[]
                            {
                                new ClaimSetMetadata.Action(
                                    "Read",
                                    new[]
                                    {
                                        new ClaimSetMetadata.AuthorizationStrategy(
                                            "NoFurtherAuthorizationRequired"
                                        ),
                                    }
                                ),
                            }
                        ),
                    }
                ),
            }
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(new List<TokenInfoEducationOrganization>());

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(new ClaimsHierarchyGetResult.Success(claimsHierarchy, DateTime.UtcNow, 1));

        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        "SIS Vendor",
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(authorizationMetadata);

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().BeEmpty(); // Claim was skipped due to missing authorization
    }

    [Test]
    public async Task GetTokenInfoAsync_WhenAuthorizationHasNoActions_SkipsClaim()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );

        var claimsHierarchy = new List<Backend.Models.ClaimsHierarchy.Claim>();
        var authorizationMetadata = new AuthorizationMetadataResponse(
            new List<ClaimSetMetadata>
            {
                new(
                    "SIS Vendor",
                    new List<ClaimSetMetadata.Claim>
                    {
                        new("http://ed-fi.org/identity/claims/ed-fi/students", 1),
                    },
                    new List<ClaimSetMetadata.Authorization>
                    {
                        // Authorization with empty actions
                        new(1, Array.Empty<ClaimSetMetadata.Action>()),
                    }
                ),
            }
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(new List<TokenInfoEducationOrganization>());

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(new ClaimsHierarchyGetResult.Success(claimsHierarchy, DateTime.UtcNow, 1));

        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        "SIS Vendor",
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(authorizationMetadata);

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().BeEmpty(); // Claim was skipped due to no actions
    }

    [Test]
    public async Task GetTokenInfoAsync_WithDomainsClaim_ConvertsToDescriptorsPath()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );

        var claimsHierarchy = new List<Backend.Models.ClaimsHierarchy.Claim>();
        var authorizationMetadata = new AuthorizationMetadataResponse(
            new List<ClaimSetMetadata>
            {
                new(
                    "SIS Vendor",
                    new List<ClaimSetMetadata.Claim>
                    {
                        // Domains claim for descriptors
                        new("http://ed-fi.org/identity/claims/domains/edFiDescriptors", 1),
                    },
                    new List<ClaimSetMetadata.Authorization>
                    {
                        new(
                            1,
                            new[]
                            {
                                new ClaimSetMetadata.Action(
                                    "Read",
                                    new[]
                                    {
                                        new ClaimSetMetadata.AuthorizationStrategy(
                                            "NoFurtherAuthorizationRequired"
                                        ),
                                    }
                                ),
                            }
                        ),
                    }
                ),
            }
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(new List<TokenInfoEducationOrganization>());

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(new ClaimsHierarchyGetResult.Success(claimsHierarchy, DateTime.UtcNow, 1));

        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        "SIS Vendor",
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(authorizationMetadata);

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().HaveCount(1);
        result.Resources[0].Resource.Should().Be("/ed-fi/descriptors");
    }

    [Test]
    public async Task GetTokenInfoAsync_WithAlternateIdentityClaimsPrefix_ConvertsCorrectly()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );

        var claimsHierarchy = new List<Backend.Models.ClaimsHierarchy.Claim>();
        var authorizationMetadata = new AuthorizationMetadataResponse(
            new List<ClaimSetMetadata>
            {
                new(
                    "SIS Vendor",
                    new List<ClaimSetMetadata.Claim>
                    {
                        // Alternate identity claims prefix (without "ods")
                        new("http://ed-fi.org/identity/claims/ed-fi/academicWeek", 1),
                    },
                    new List<ClaimSetMetadata.Authorization>
                    {
                        new(
                            1,
                            new[]
                            {
                                new ClaimSetMetadata.Action(
                                    "Read",
                                    new[]
                                    {
                                        new ClaimSetMetadata.AuthorizationStrategy(
                                            "NoFurtherAuthorizationRequired"
                                        ),
                                    }
                                ),
                            }
                        ),
                    }
                ),
            }
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(new List<TokenInfoEducationOrganization>());

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(new ClaimsHierarchyGetResult.Success(claimsHierarchy, DateTime.UtcNow, 1));

        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        "SIS Vendor",
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(authorizationMetadata);

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().HaveCount(1);
        result.Resources[0].Resource.Should().Be("/ed-fi/academicWeeks");
    }

    [Test]
    public async Task GetTokenInfoAsync_WithUnrecognizedClaimFormat_ReturnsClaimAsIs()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );

        var claimsHierarchy = new List<Backend.Models.ClaimsHierarchy.Claim>();
        var authorizationMetadata = new AuthorizationMetadataResponse(
            new List<ClaimSetMetadata>
            {
                new(
                    "SIS Vendor",
                    new List<ClaimSetMetadata.Claim>
                    {
                        // Unrecognized claim format
                        new("custom://some-other-format/resource", 1),
                    },
                    new List<ClaimSetMetadata.Authorization>
                    {
                        new(
                            1,
                            new[]
                            {
                                new ClaimSetMetadata.Action(
                                    "Read",
                                    new[]
                                    {
                                        new ClaimSetMetadata.AuthorizationStrategy(
                                            "NoFurtherAuthorizationRequired"
                                        ),
                                    }
                                ),
                            }
                        ),
                    }
                ),
            }
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(new List<TokenInfoEducationOrganization>());

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(new ClaimsHierarchyGetResult.Success(claimsHierarchy, DateTime.UtcNow, 1));

        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        "SIS Vendor",
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(authorizationMetadata);

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().HaveCount(1);
        result.Resources[0].Resource.Should().Be("custom://some-other-format/resource");
    }

    [Test]
    public async Task GetTokenInfoAsync_WithMultipleNamespacePrefixes_ReturnsAllPrefixes()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org,uri://custom.org,uri://another.org"
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(new List<TokenInfoEducationOrganization>());

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(
                new ClaimsHierarchyGetResult.Success(
                    new List<Backend.Models.ClaimsHierarchy.Claim>(),
                    DateTime.UtcNow,
                    1
                )
            );

        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        A<string>._,
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Returns(new AuthorizationMetadataResponse(new List<ClaimSetMetadata>()));

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.NamespacePrefixes.Should().HaveCount(3);
        result.NamespacePrefixes.Should().Contain("uri://ed-fi.org");
        result.NamespacePrefixes.Should().Contain("uri://custom.org");
        result.NamespacePrefixes.Should().Contain("uri://another.org");
    }

    [Test]
    public async Task GetTokenInfoAsync_WhenAuthorizationMetadataThrowsException_ReturnsEmptyResourcesAndServices()
    {
        // Arrange
        var token = CreateTestJwtToken(
            clientId: "test-client",
            claimSetName: "SIS Vendor",
            educationOrganizationIds: "255901",
            namespacePrefixes: "uri://ed-fi.org"
        );

        A.CallTo(() => _apiClientRepository.GetApiClientByClientId("test-client"))
            .Returns(
                new ApiClientGetResult.Success(
                    new ApiClientResponse
                    {
                        Id = 1,
                        ApplicationId = 1,
                        ClientId = "test-client",
                        ClientUuid = Guid.NewGuid(),
                        Name = "Test Client",
                        IsApproved = true,
                        DmsInstanceIds = new List<long>(),
                    }
                )
            );

        A.CallTo(
                () => _educationOrganizationRepository.GetEducationOrganizationsAsync(A<IEnumerable<long>>._)
            )
            .Returns(new List<TokenInfoEducationOrganization>());

        A.CallTo(() => _claimsHierarchyRepository.GetClaimsHierarchy(A<DbTransaction?>._))
            .Returns(
                new ClaimsHierarchyGetResult.Success(
                    new List<Backend.Models.ClaimsHierarchy.Claim>(),
                    DateTime.UtcNow,
                    1
                )
            );

        // Authorization metadata factory throws exception
        A.CallTo(
                () =>
                    _authorizationMetadataResponseFactory.Create(
                        A<string>._,
                        A<List<Backend.Models.ClaimsHierarchy.Claim>>._
                    )
            )
            .Throws(new Exception("Database connection failed"));

        // Act
        var result = await _provider.GetTokenInfoAsync(token);

        // Assert
        result.Should().NotBeNull();
        result!.Resources.Should().BeEmpty();
        result.Services.Should().BeEmpty();
    }

    private static readonly string _testJwtSecretKey =
        Environment.GetEnvironmentVariable("EDFI_TEST_JWT_SECRET_KEY") ??
        TestConstants.TestJwtSigningKey;

    private static string CreateTestJwtToken(
        string clientId,
        string claimSetName,
        string educationOrganizationIds,
        string namespacePrefixes,
        int expiresInMinutes = 60
    )
    {
        var claims = new[]
        {
            new Claim("client_id", clientId),
            new Claim("scope", claimSetName),
            new Claim("educationOrganizationIds", educationOrganizationIds),
            new Claim("namespacePrefixes", namespacePrefixes),
            new Claim("jti", Guid.NewGuid().ToString()),
        };

        // Use a secret key from environment variable or fallback for tests.
        var key = new SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(_testJwtSecretKey)
        );
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "test-issuer",
            audience: "test-audience",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(expiresInMinutes),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
