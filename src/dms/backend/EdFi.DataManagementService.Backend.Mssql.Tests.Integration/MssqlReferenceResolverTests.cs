// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[NonParallelizable]
public class Given_MssqlReferenceResolver
{
    private MssqlReferenceResolverTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore(
                "SQL Server integration tests require a MssqlAdmin connection string in appsettings.Test.json"
            );
        }

        _database = await MssqlReferenceResolverTestDatabase.CreateProvisionedAsync();
        _serviceProvider = CreateServiceProvider();
    }

    [SetUp]
    public async Task Setup()
    {
        await _database.ResetAsync();
        await _database.SeedAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_preserves_per_path_results_while_deduping_repeated_document_reference_lookups()
    {
        var result = await ResolveDocumentReferencesAsync(
            _database.Fixture.CreateSchoolReference("$.schoolReference"),
            _database.Fixture.CreateSchoolReference("$.sections[0].schoolReference"),
            _database.Fixture.CreateSchoolReference(
                "$.sections[1].schoolReference",
                _database.Fixture.MissingSchoolReferentialId
            ),
            _database.Fixture.CreateSchoolReference(
                "$.sections[2].schoolReference",
                _database.Fixture.MissingSchoolReferentialId
            )
        );

        result.LookupsByReferentialId.Should().HaveCount(2);
        result
            .DocumentReferenceOccurrences.Select(occurrence => occurrence.Lookup)
            .Should()
            .SatisfyRespectively(
                firstLookup =>
                {
                    firstLookup.ReferentialId.Should().Be(_database.Fixture.SchoolReferentialId);
                    firstLookup.Result.Should().NotBeNull();
                },
                secondLookup => secondLookup.Should().BeSameAs(result.DocumentReferenceOccurrences[0].Lookup),
                thirdLookup =>
                {
                    thirdLookup.ReferentialId.Should().Be(_database.Fixture.MissingSchoolReferentialId);
                    thirdLookup.Result.Should().BeNull();
                },
                fourthLookup => fourthLookup.Should().BeSameAs(result.DocumentReferenceOccurrences[2].Lookup)
            );

        result
            .SuccessfulDocumentReferencesByPath.Keys.Should()
            .Equal(new JsonPath("$.schoolReference"), new JsonPath("$.sections[0].schoolReference"));
        result
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.sections[1].schoolReference", DocumentReferenceFailureReason.Missing),
                ("$.sections[2].schoolReference", DocumentReferenceFailureReason.Missing)
            );
    }

    [Test]
    public async Task It_fails_closed_when_a_concrete_target_mismatch_has_a_mismatched_verification_key()
    {
        var act = async () =>
            await ResolveDocumentReferencesAsync(
                _database.Fixture.CreateSchoolReference(
                    "$.localEducationAgencyReference",
                    _database.Fixture.LocalEducationAgencyReferentialId
                )
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Reference lookup corruption detected");
        exception
            .Which.Message.Should()
            .Contain(_database.Fixture.LocalEducationAgencyReferentialId.Value.ToString());
        exception.Which.Message.Should().Contain("$.localEducationAgencyReference");
        exception.Which.Message.Should().Contain("$.schoolId=255901");
        exception.Which.Message.Should().Contain("'<null>'");
    }

    [Test]
    public async Task It_resolves_abstract_alias_references_and_fails_closed_for_incompatible_alias_verification_mismatches()
    {
        var resolvedAliasResult = await ResolveDocumentReferencesAsync(
            _database.Fixture.CreateEducationOrganizationReference("$.educationOrganizationReference")
        );
        var incompatibleAliasAct = async () =>
            await ResolveDocumentReferencesAsync(
                _database.Fixture.CreateLocalEducationAgencyReference(
                    "$.localEducationAgencyReference",
                    _database.Fixture.EducationOrganizationAliasReferentialId
                )
            );

        var resolvedEducationOrganization = resolvedAliasResult.SuccessfulDocumentReferencesByPath[
            new JsonPath("$.educationOrganizationReference")
        ];

        resolvedEducationOrganization.DocumentId.Should().Be(101L);
        resolvedEducationOrganization
            .ResourceKeyId.Should()
            .Be(_database.MappingSet.ResourceKeyIdByResource[_database.Fixture.SchoolResource]);
        resolvedAliasResult
            .LookupsByReferentialId[_database.Fixture.EducationOrganizationAliasReferentialId]
            .Result!.ReferentialIdentityResourceKeyId.Should()
            .Be(
                _database.MappingSet.ResourceKeyIdByResource[_database.Fixture.EducationOrganizationResource]
            );
        var exception = await incompatibleAliasAct.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Reference lookup corruption detected");
        exception
            .Which.Message.Should()
            .Contain(_database.Fixture.EducationOrganizationAliasReferentialId.Value.ToString());
        exception.Which.Message.Should().Contain("$.localEducationAgencyReference");
        exception.Which.Message.Should().Contain("$.localEducationAgencyId=255901");
        exception.Which.Message.Should().Contain("'<null>'");
    }

    [Test]
    public async Task It_fails_closed_when_a_descriptor_lookup_has_a_non_descriptor_verification_mismatch()
    {
        var act = async () =>
            await ResolveDescriptorReferencesAsync(
                _database.Fixture.CreateSchoolTypeDescriptorReference(
                    "$.schoolTypeDescriptor",
                    _database.Fixture.LocalEducationAgencyReferentialId,
                    _database.Fixture.SchoolTypeDescriptorUri
                )
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Reference lookup corruption detected");
        exception
            .Which.Message.Should()
            .Contain(_database.Fixture.LocalEducationAgencyReferentialId.Value.ToString());
        exception.Which.Message.Should().Contain("$.schoolTypeDescriptor");
        exception
            .Which.Message.Should()
            .Contain("$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative");
        exception.Which.Message.Should().Contain("'<null>'");
    }

    [Test]
    public async Task It_surfaces_resolved_wrong_descriptor_type_failures_as_descriptor_type_mismatch()
    {
        var result = await ResolveDescriptorReferencesAsync(
            _database.Fixture.CreateSchoolTypeDescriptorReference(
                "$.schoolTypeDescriptor",
                _database.Fixture.AcademicSubjectDescriptorReferentialId,
                _database.Fixture.AcademicSubjectDescriptorUri
            )
        );

        result.SuccessfulDescriptorReferencesByPath.Should().BeEmpty();
        result
            .InvalidDescriptorReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(("$.schoolTypeDescriptor", DescriptorReferenceFailureReason.DescriptorTypeMismatch));
        result
            .LookupsByReferentialId[_database.Fixture.AcademicSubjectDescriptorReferentialId]
            .Result.Should()
            .NotBeNull();
        result
            .LookupsByReferentialId[_database.Fixture.AcademicSubjectDescriptorReferentialId]
            .Result!.IsDescriptor.Should()
            .BeTrue();
        result
            .LookupsByReferentialId[_database.Fixture.AcademicSubjectDescriptorReferentialId]
            .Result!.ResourceKeyId.Should()
            .Be(
                _database.MappingSet.ResourceKeyIdByResource[
                    _database.Fixture.AcademicSubjectDescriptorResource
                ]
            );
    }

    [Test]
    public async Task It_classifies_missing_descriptor_lookups_with_the_same_reasons_as_the_shared_classifier()
    {
        var wrongTypeMissingDescriptorReferentialId = new ReferentialId(Guid.NewGuid());
        var result = await ResolveDescriptorReferencesAsync(
            _database.Fixture.CreateSchoolTypeDescriptorReference("$.schoolTypeDescriptor"),
            _database.Fixture.CreateSchoolTypeDescriptorReference(
                "$.alternateSchoolTypeDescriptor",
                wrongTypeMissingDescriptorReferentialId,
                _database.Fixture.AcademicSubjectDescriptorUri
            ),
            _database.Fixture.CreateSchoolTypeDescriptorReference(
                "$.programs[0].schoolTypeDescriptor",
                _database.Fixture.MissingSchoolTypeDescriptorReferentialId,
                _database.Fixture.MissingSchoolTypeDescriptorUri
            )
        );

        result
            .SuccessfulDescriptorReferencesByPath.Keys.Should()
            .Equal(new JsonPath("$.schoolTypeDescriptor"));
        result
            .InvalidDescriptorReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.alternateSchoolTypeDescriptor", DescriptorReferenceFailureReason.Missing),
                ("$.programs[0].schoolTypeDescriptor", DescriptorReferenceFailureReason.Missing)
            );
        result
            .DescriptorReferenceOccurrences.Where(occurrence =>
                occurrence.Reference.ReferentialId
                == _database.Fixture.MissingSchoolTypeDescriptorReferentialId
            )
            .Select(occurrence => occurrence.Lookup.Result)
            .Should()
            .AllSatisfy(lookupResult => lookupResult.Should().BeNull());
    }

    [Test]
    public async Task It_fails_closed_when_a_same_type_document_referential_identity_row_is_cross_wired()
    {
        await _database.ResetAsync();
        await _database.SeedAsync(CreateCrossWiredSchoolSeedData());

        var act = async () =>
            await ResolveDocumentReferencesAsync(
                _database.Fixture.CreateSchoolReference("$.schoolReference")
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Reference lookup corruption detected");
        exception.Which.Message.Should().Contain(_database.Fixture.SchoolReferentialId.Value.ToString());
        exception.Which.Message.Should().Contain("$.schoolReference");
        exception.Which.Message.Should().Contain("$.schoolId=255901");
        exception.Which.Message.Should().Contain("$.schoolId=255902");
    }

    [Test]
    public async Task It_fails_closed_when_a_same_type_descriptor_referential_identity_row_is_cross_wired()
    {
        await _database.ResetAsync();
        await _database.SeedAsync(CreateCrossWiredSchoolTypeDescriptorSeedData());

        var act = async () =>
            await ResolveDescriptorReferencesAsync(
                _database.Fixture.CreateSchoolTypeDescriptorReference("$.schoolTypeDescriptor")
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Reference lookup corruption detected");
        exception
            .Which.Message.Should()
            .Contain(_database.Fixture.SchoolTypeDescriptorReferentialId.Value.ToString());
        exception.Which.Message.Should().Contain("$.schoolTypeDescriptor");
        exception
            .Which.Message.Should()
            .Contain("$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative");
        exception.Which.Message.Should().Contain("$.descriptor=uri://ed-fi.org/schooltypedescriptor#wrong");
    }

    [Test]
    public async Task It_fails_closed_when_a_wrong_type_document_referential_identity_row_is_cross_wired()
    {
        await _database.ResetAsync();
        await _database.SeedAsync(CreateWrongTypeSchoolSeedData());

        var act = async () =>
            await ResolveDocumentReferencesAsync(
                _database.Fixture.CreateSchoolReference("$.schoolReference")
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Reference lookup corruption detected");
        exception.Which.Message.Should().Contain(_database.Fixture.SchoolReferentialId.Value.ToString());
        exception.Which.Message.Should().Contain("$.schoolReference");
        exception.Which.Message.Should().Contain("$.schoolId=255901");
        exception.Which.Message.Should().Contain("'<null>'");
    }

    [Test]
    public async Task It_fails_closed_when_a_wrong_type_descriptor_referential_identity_row_is_cross_wired()
    {
        await _database.ResetAsync();
        await _database.SeedAsync(CreateWrongTypeSchoolTypeDescriptorSeedData());

        var act = async () =>
            await ResolveDescriptorReferencesAsync(
                _database.Fixture.CreateSchoolTypeDescriptorReference("$.schoolTypeDescriptor")
            );

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Reference lookup corruption detected");
        exception
            .Which.Message.Should()
            .Contain(_database.Fixture.SchoolTypeDescriptorReferentialId.Value.ToString());
        exception.Which.Message.Should().Contain("$.schoolTypeDescriptor");
        exception
            .Which.Message.Should()
            .Contain("$.descriptor=uri://ed-fi.org/schooltypedescriptor#alternative");
        exception
            .Which.Message.Should()
            .Contain("$.descriptor=uri://ed-fi.org/academicsubjectdescriptor#mathematics");
    }

    [Test]
    public async Task It_resolves_school_and_lea_references_with_ids_above_int32_max()
    {
        const long BigIntSchoolId = 3_000_000_001L;
        const long BigIntLeaId = 3_000_000_002L;

        var schoolResourceKeyId = _database.MappingSet.ResourceKeyIdByResource[
            _database.Fixture.SchoolResource
        ];
        var leaResourceKeyId = _database.MappingSet.ResourceKeyIdByResource[
            _database.Fixture.LocalEducationAgencyResource
        ];

        var bigIntSchoolReferentialId = new ReferentialId(Guid.Parse("A0000000-0000-0000-0000-000000000001"));
        var bigIntLeaReferentialId = new ReferentialId(Guid.Parse("A0000000-0000-0000-0000-000000000002"));

        await _database.ResetAsync();
        await _database.SeedAsync(
            new ReferenceResolverSeedData(
                ResourceKeys: _database.Fixture.SeedData.ResourceKeys,
                Documents:
                [
                    new ReferenceResolverDocumentSeed(
                        901,
                        Guid.Parse("A1000000-0000-0000-0000-000000000901"),
                        schoolResourceKeyId
                    ),
                    new ReferenceResolverDocumentSeed(
                        902,
                        Guid.Parse("A2000000-0000-0000-0000-000000000902"),
                        leaResourceKeyId
                    ),
                ],
                ReferentialIdentities:
                [
                    new ReferenceResolverReferentialIdentitySeed(
                        bigIntSchoolReferentialId,
                        901,
                        schoolResourceKeyId
                    ),
                    new ReferenceResolverReferentialIdentitySeed(
                        bigIntLeaReferentialId,
                        902,
                        leaResourceKeyId
                    ),
                ],
                Schools: [new ReferenceResolverSchoolSeed(901, BigIntSchoolId)],
                LocalEducationAgencies: [new ReferenceResolverLocalEducationAgencySeed(902, BigIntLeaId)],
                Descriptors: []
            )
        );

        var result = await ResolveDocumentReferencesAsync(
            CreateSchoolReference("$.schoolReference", bigIntSchoolReferentialId, BigIntSchoolId),
            CreateLocalEducationAgencyReference(
                "$.localEducationAgencyReference",
                bigIntLeaReferentialId,
                BigIntLeaId
            )
        );

        result.SuccessfulDocumentReferencesByPath.Should().HaveCount(2);
        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.schoolReference")]
            .DocumentId.Should()
            .Be(901L);
        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.localEducationAgencyReference")]
            .DocumentId.Should()
            .Be(902L);
        result.InvalidDocumentReferences.Should().BeEmpty();
    }

    [Test]
    public async Task It_resolves_a_threshold_crossing_deduped_lookup_set_without_losing_repeated_missing_path_diagnostics()
    {
        const int LargeLookupCount = 1999;

        await _database.SeedAsync(
            CreateAdditionalSchoolSeedData(
                LargeLookupCount,
                _database.MappingSet.ResourceKeyIdByResource[_database.Fixture.SchoolResource]
            )
        );

        DocumentReference[] documentReferences =
        [
            .. Enumerable
                .Range(0, LargeLookupCount)
                .Select(index =>
                    CreateSchoolReference(
                        $"$.bulkSchools[{index}].schoolReference",
                        CreateBulkSchoolReferentialId(index + 1),
                        300000 + index
                    )
                ),
            _database.Fixture.CreateSchoolReference(
                "$.missingSchools[0].schoolReference",
                _database.Fixture.MissingSchoolReferentialId
            ),
            _database.Fixture.CreateSchoolReference(
                "$.missingSchools[1].schoolReference",
                _database.Fixture.MissingSchoolReferentialId
            ),
            _database.Fixture.CreateSchoolReference(
                "$.missingSchools[2].schoolReference",
                _database.Fixture.MissingSchoolReferentialId
            ),
        ];

        var result = await ResolveDocumentReferencesAsync(documentReferences);

        result.LookupsByReferentialId.Should().HaveCount(LargeLookupCount + 1);
        result.DocumentReferenceOccurrences.Should().HaveCount(LargeLookupCount + 3);
        result.SuccessfulDocumentReferencesByPath.Should().HaveCount(LargeLookupCount);
        result
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.bulkSchools[0].schoolReference")]
            .DocumentId.Should()
            .Be(1000L);
        result
            .SuccessfulDocumentReferencesByPath[
                new JsonPath($"$.bulkSchools[{LargeLookupCount - 1}].schoolReference")
            ]
            .DocumentId.Should()
            .Be(1000L + LargeLookupCount - 1);
        result
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.missingSchools[0].schoolReference", DocumentReferenceFailureReason.Missing),
                ("$.missingSchools[1].schoolReference", DocumentReferenceFailureReason.Missing),
                ("$.missingSchools[2].schoolReference", DocumentReferenceFailureReason.Missing)
            );

        var missingOccurrences = result
            .DocumentReferenceOccurrences.Where(occurrence =>
                occurrence.Reference.ReferentialId == _database.Fixture.MissingSchoolReferentialId
            )
            .ToArray();

        missingOccurrences.Should().HaveCount(3);
        missingOccurrences[1].Lookup.Should().BeSameAs(missingOccurrences[0].Lookup);
        missingOccurrences[2].Lookup.Should().BeSameAs(missingOccurrences[0].Lookup);
        result.LookupsByReferentialId[_database.Fixture.MissingSchoolReferentialId].Result.Should().BeNull();
    }

    private async Task<ResolvedReferenceSet> ResolveDocumentReferencesAsync(
        params DocumentReference[] documentReferences
    )
    {
        return await ResolveReferencesAsync(documentReferences, []);
    }

    private async Task<ResolvedReferenceSet> ResolveDescriptorReferencesAsync(
        params DescriptorReference[] descriptorReferences
    )
    {
        return await ResolveReferencesAsync([], descriptorReferences);
    }

    private async Task<ResolvedReferenceSet> ResolveReferencesAsync(
        IReadOnlyList<DocumentReference> documentReferences,
        IReadOnlyList<DescriptorReference> descriptorReferences
    )
    {
        using var scope = _serviceProvider.CreateScope();
        var instanceSelection = scope.ServiceProvider.GetRequiredService<IDmsInstanceSelection>();
        instanceSelection.SetSelectedDmsInstance(
            new DmsInstance(
                Id: 1,
                InstanceType: "test",
                InstanceName: "MssqlReferenceResolverIntegration",
                ConnectionString: _database.ConnectionString,
                RouteContext: []
            )
        );

        var resolver = scope.ServiceProvider.GetRequiredService<IReferenceResolver>();

        return await resolver.ResolveAsync(
            new ReferenceResolverRequest(
                MappingSet: _database.MappingSet,
                RequestResource: _database.Fixture.RequestResource,
                DocumentReferences: documentReferences,
                DescriptorReferences: descriptorReferences
            )
        );
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddMssqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private DocumentReference CreateSchoolReference(string path, ReferentialId referentialId, long schoolId)
    {
        return new(
            ResourceInfo: new BaseResourceInfo(
                new ProjectName(_database.Fixture.SchoolResource.ProjectName),
                new ResourceName(_database.Fixture.SchoolResource.ResourceName),
                false
            ),
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(new JsonPath("$.schoolId"), schoolId.ToString()),
            ]),
            ReferentialId: referentialId,
            Path: new JsonPath(path)
        );
    }

    private DocumentReference CreateLocalEducationAgencyReference(
        string path,
        ReferentialId referentialId,
        long localEducationAgencyId
    )
    {
        return new(
            ResourceInfo: new BaseResourceInfo(
                new ProjectName(_database.Fixture.LocalEducationAgencyResource.ProjectName),
                new ResourceName(_database.Fixture.LocalEducationAgencyResource.ResourceName),
                false
            ),
            DocumentIdentity: new DocumentIdentity([
                new DocumentIdentityElement(
                    new JsonPath("$.localEducationAgencyId"),
                    localEducationAgencyId.ToString()
                ),
            ]),
            ReferentialId: referentialId,
            Path: new JsonPath(path)
        );
    }

    private ReferenceResolverSeedData CreateCrossWiredSchoolSeedData()
    {
        var seedData = _database.Fixture.SeedData;
        var schoolResourceKeyId = _database.MappingSet.ResourceKeyIdByResource[
            _database.Fixture.SchoolResource
        ];

        return seedData with
        {
            Documents =
            [
                .. seedData.Documents,
                new ReferenceResolverDocumentSeed(
                    505,
                    Guid.Parse("50000000-0000-0000-0000-000000000505"),
                    schoolResourceKeyId
                ),
            ],
            ReferentialIdentities =
            [
                .. seedData.ReferentialIdentities.Select(referentialIdentity =>
                    referentialIdentity.ReferentialId == _database.Fixture.SchoolReferentialId
                        ? referentialIdentity with
                        {
                            DocumentId = 505,
                        }
                        : referentialIdentity
                ),
            ],
            Schools = [.. seedData.Schools, new ReferenceResolverSchoolSeed(505, 255902)],
        };
    }

    private ReferenceResolverSeedData CreateCrossWiredSchoolTypeDescriptorSeedData()
    {
        var seedData = _database.Fixture.SeedData;
        var schoolTypeDescriptorResourceKeyId = _database.MappingSet.ResourceKeyIdByResource[
            _database.Fixture.SchoolTypeDescriptorResource
        ];

        return seedData with
        {
            Documents =
            [
                .. seedData.Documents,
                new ReferenceResolverDocumentSeed(
                    606,
                    Guid.Parse("60000000-0000-0000-0000-000000000606"),
                    schoolTypeDescriptorResourceKeyId
                ),
            ],
            ReferentialIdentities =
            [
                .. seedData.ReferentialIdentities.Select(referentialIdentity =>
                    referentialIdentity.ReferentialId == _database.Fixture.SchoolTypeDescriptorReferentialId
                        ? referentialIdentity with
                        {
                            DocumentId = 606,
                        }
                        : referentialIdentity
                ),
            ],
            Descriptors =
            [
                .. seedData.Descriptors,
                new ReferenceResolverDescriptorSeed(
                    606,
                    "uri://ed-fi.org",
                    "Wrong",
                    "Wrong",
                    "SchoolTypeDescriptor",
                    "uri://ed-fi.org/SchoolTypeDescriptor#Wrong"
                ),
            ],
        };
    }

    private ReferenceResolverSeedData CreateWrongTypeSchoolSeedData()
    {
        var seedData = _database.Fixture.SeedData;
        var localEducationAgencyResourceKeyId = _database.MappingSet.ResourceKeyIdByResource[
            _database.Fixture.LocalEducationAgencyResource
        ];

        return seedData with
        {
            Documents =
            [
                .. seedData.Documents,
                new ReferenceResolverDocumentSeed(
                    707,
                    Guid.Parse("70000000-0000-0000-0000-000000000707"),
                    localEducationAgencyResourceKeyId
                ),
            ],
            LocalEducationAgencies =
            [
                .. seedData.LocalEducationAgencies,
                new ReferenceResolverLocalEducationAgencySeed(707, 255902),
            ],
            ReferentialIdentities =
            [
                .. seedData.ReferentialIdentities.Select(referentialIdentity =>
                    referentialIdentity.ReferentialId == _database.Fixture.SchoolReferentialId
                        ? referentialIdentity with
                        {
                            DocumentId = 707,
                            ResourceKeyId = localEducationAgencyResourceKeyId,
                        }
                        : referentialIdentity
                ),
            ],
        };
    }

    private ReferenceResolverSeedData CreateWrongTypeSchoolTypeDescriptorSeedData()
    {
        var seedData = _database.Fixture.SeedData;
        var academicSubjectDescriptorResourceKeyId = _database.MappingSet.ResourceKeyIdByResource[
            _database.Fixture.AcademicSubjectDescriptorResource
        ];

        return seedData with
        {
            Documents =
            [
                .. seedData.Documents,
                new ReferenceResolverDocumentSeed(
                    808,
                    Guid.Parse("80000000-0000-0000-0000-000000000808"),
                    academicSubjectDescriptorResourceKeyId
                ),
            ],
            Descriptors =
            [
                .. seedData.Descriptors,
                new ReferenceResolverDescriptorSeed(
                    808,
                    "uri://ed-fi.org",
                    "Mathematics",
                    "Mathematics",
                    "AcademicSubjectDescriptor",
                    "uri://ed-fi.org/AcademicSubjectDescriptor#Mathematics"
                ),
            ],
            ReferentialIdentities =
            [
                .. seedData.ReferentialIdentities.Select(referentialIdentity =>
                    referentialIdentity.ReferentialId == _database.Fixture.SchoolTypeDescriptorReferentialId
                        ? referentialIdentity with
                        {
                            DocumentId = 808,
                            ResourceKeyId = academicSubjectDescriptorResourceKeyId,
                        }
                        : referentialIdentity
                ),
            ],
        };
    }

    private static ReferenceResolverSeedData CreateAdditionalSchoolSeedData(
        int count,
        short schoolResourceKeyId
    )
    {
        ReferenceResolverDocumentSeed[] documents =
        [
            .. Enumerable
                .Range(0, count)
                .Select(index => new ReferenceResolverDocumentSeed(
                    1000L + index,
                    CreateBulkSchoolDocumentUuid(index + 1),
                    schoolResourceKeyId
                )),
        ];

        ReferenceResolverReferentialIdentitySeed[] referentialIdentities =
        [
            .. Enumerable
                .Range(0, count)
                .Select(index => new ReferenceResolverReferentialIdentitySeed(
                    CreateBulkSchoolReferentialId(index + 1),
                    documents[index].DocumentId,
                    schoolResourceKeyId
                )),
        ];

        ReferenceResolverSchoolSeed[] schools =
        [
            .. Enumerable
                .Range(0, count)
                .Select(index => new ReferenceResolverSchoolSeed(
                    documents[index].DocumentId,
                    300000 + index
                )),
        ];

        return new(
            ResourceKeys: [],
            Documents: documents,
            ReferentialIdentities: referentialIdentities,
            Schools: schools,
            LocalEducationAgencies: [],
            Descriptors: []
        );
    }

    private static Guid CreateBulkSchoolDocumentUuid(int ordinal)
    {
        return Guid.Parse($"80000000-0000-0000-0000-{ordinal:000000000000}");
    }

    private static ReferentialId CreateBulkSchoolReferentialId(int ordinal)
    {
        return new ReferentialId(Guid.Parse($"90000000-0000-0000-0000-{ordinal:000000000000}"));
    }
}
