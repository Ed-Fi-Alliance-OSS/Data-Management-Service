// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.Tests.Common;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Old-vs-new resolver equivalence on SQL Server: two service providers over ONE seeded database, the same
/// request through both, and the whole <see cref="ResolvedReferenceSet"/> compared — the mirror of
/// <c>Given_PostgresqlReferenceResolverDifferential</c>, plus the two SQL-Server-specific cases: a batch
/// that crosses the per-command parameter ceiling, and the deliberate collation delta.
/// </summary>
[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category(MssqlCiShards.Shard1)]
public class Given_MssqlReferenceResolverDifferential
{
    private MssqlReferenceResolverTestDatabase _database = null!;
    private ServiceProvider _hashResolverProvider = null!;
    private ServiceProvider _naturalKeyResolverProvider = null!;

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
        _hashResolverProvider = CreateServiceProvider(useNaturalKeyResolver: false);
        _naturalKeyResolverProvider = CreateServiceProvider(useNaturalKeyResolver: true);
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
        if (_hashResolverProvider is not null)
        {
            await _hashResolverProvider.DisposeAsync();
        }

        if (_naturalKeyResolverProvider is not null)
        {
            await _naturalKeyResolverProvider.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    [Test]
    public async Task It_resolves_found_missing_and_repeated_document_references_identically()
    {
        var fixture = _database.Fixture;

        await AssertResolversAgreeAsync(
            "found/missing/repeated document references",
            documentReferences:
            [
                fixture.CreateSchoolReference("$.schoolReference"),
                fixture.CreateSchoolReference("$.sections[0].schoolReference"),
                fixture.CreateLocalEducationAgencyReference("$.localEducationAgencyReference"),
                fixture.CreateSchoolReference(
                    "$.sections[1].schoolReference",
                    fixture.MissingSchoolReferentialId,
                    schoolId: 999_999
                ),
                fixture.CreateSchoolReference(
                    "$.sections[2].schoolReference",
                    fixture.MissingSchoolReferentialId,
                    schoolId: 999_999
                ),
            ]
        );
    }

    [Test]
    public async Task It_resolves_abstract_references_identically()
    {
        var fixture = _database.Fixture;

        var (hashResult, naturalKeyResult) = await AssertResolversAgreeAsync(
            "abstract references",
            documentReferences:
            [
                fixture.CreateEducationOrganizationReference("$.educationOrganizationReference"),
                fixture.CreateEducationOrganizationReference(
                    "$.programs[0].educationOrganizationReference",
                    fixture.MissingEducationOrganizationReferentialId,
                    educationOrganizationId: ReferenceResolverIntegrationFixture.MissingEducationOrganizationIdentityValue
                ),
            ]
        );

        foreach (var result in new[] { hashResult, naturalKeyResult })
        {
            var resolved = result.SuccessfulDocumentReferencesByPath[
                new JsonPath("$.educationOrganizationReference")
            ];
            resolved.DocumentId.Should().Be(101L);
            resolved
                .ResourceKeyId.Should()
                .Be(
                    _database.MappingSet.ResourceKeyIdByResource[fixture.SchoolResource],
                    "an abstract hit reports the matched concrete subtype"
                );
        }
    }

    [Test]
    public async Task It_resolves_descriptor_found_missing_and_type_mismatch_identically()
    {
        var fixture = _database.Fixture;

        // A SchoolTypeDescriptor-typed reference carrying the AcademicSubjectDescriptor URI. The hash
        // resolver only reports DescriptorTypeMismatch (rather than Missing) when the referential id
        // resolves, so the SchoolTypeDescriptor-scoped hash of that URI is seeded against the
        // AcademicSubjectDescriptor document — the same wrong-type row the URI probe finds.
        var wrongTypeReferentialId = new ReferentialId(Guid.Parse("ab000000-0000-0000-0000-000000000404"));
        await _database.SeedAsync(
            new ReferenceResolverSeedData(
                ResourceKeys: [],
                Documents: [],
                ReferentialIdentities:
                [
                    new ReferenceResolverReferentialIdentitySeed(
                        wrongTypeReferentialId,
                        404,
                        _database.MappingSet.ResourceKeyIdByResource[fixture.SchoolTypeDescriptorResource]
                    ),
                ],
                Schools: [],
                LocalEducationAgencies: [],
                Descriptors: []
            )
        );

        var (_, naturalKeyResult) = await AssertResolversAgreeAsync(
            "descriptor found/missing/type-mismatch",
            descriptorReferences:
            [
                fixture.CreateSchoolTypeDescriptorReference("$.schoolTypeDescriptor"),
                fixture.CreateAcademicSubjectDescriptorReference("$.academicSubjectDescriptor"),
                fixture.CreateSchoolTypeDescriptorReference(
                    "$.programs[0].schoolTypeDescriptor",
                    fixture.MissingSchoolTypeDescriptorReferentialId,
                    fixture.MissingSchoolTypeDescriptorUri
                ),
                fixture.CreateSchoolTypeDescriptorReference(
                    "$.alternateSchoolTypeDescriptor",
                    wrongTypeReferentialId,
                    fixture.AcademicSubjectDescriptorUri
                ),
            ]
        );

        naturalKeyResult
            .InvalidDescriptorReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.programs[0].schoolTypeDescriptor", DescriptorReferenceFailureReason.Missing),
                ("$.alternateSchoolTypeDescriptor", DescriptorReferenceFailureReason.DescriptorTypeMismatch)
            );
    }

    [Test]
    public async Task It_resolves_a_case_insensitive_descriptor_uri_identically()
    {
        var fixture = _database.Fixture;

        await AssertResolversAgreeAsync(
            "lower-cased descriptor uri",
            descriptorReferences:
            [
                fixture.CreateSchoolTypeDescriptorReference(
                    "$.schoolTypeDescriptor",
                    uri: fixture.SchoolTypeDescriptorUri.ToLowerInvariant()
                ),
            ]
        );
    }

    [Test]
    public async Task It_resolves_every_scalar_kind_of_identity_value_identically()
    {
        var fixture = _database.Fixture;

        var (_, naturalKeyResult) = await AssertResolversAgreeAsync(
            "per-scalar-kind identity values",
            documentReferences:
            [
                fixture.CreateWideIdentityReference("$.wideIdentityReference"),
                fixture.CreateWideIdentityReference(
                    "$.misses[0].wideIdentityReference",
                    CreateMissReferentialId(1),
                    int64Key: "9007199254740994"
                ),
                fixture.CreateWideIdentityReference(
                    "$.misses[1].wideIdentityReference",
                    CreateMissReferentialId(2),
                    decimalKey: "12.75"
                ),
                fixture.CreateWideIdentityReference(
                    "$.misses[2].wideIdentityReference",
                    CreateMissReferentialId(3),
                    dateKey: "2024-03-06"
                ),
                fixture.CreateWideIdentityReference(
                    "$.misses[3].wideIdentityReference",
                    CreateMissReferentialId(4),
                    dateTimeKey: "2024-03-05T13:45:31Z"
                ),
                fixture.CreateWideIdentityReference(
                    "$.misses[4].wideIdentityReference",
                    CreateMissReferentialId(5),
                    booleanKey: "false"
                ),
                fixture.CreateWideIdentityReference(
                    "$.misses[5].wideIdentityReference",
                    CreateMissReferentialId(6),
                    stringKey: "Gamma-Delta"
                ),
            ]
        );

        naturalKeyResult
            .SuccessfulDocumentReferencesByPath.Keys.Select(path => path.Value)
            .Should()
            .Equal(
                ["$.wideIdentityReference"],
                because: "the seeded row must resolve through every scalar kind at once"
            );
        naturalKeyResult
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.wideIdentityReference")]
            .DocumentId.Should()
            .Be(550L);
        naturalKeyResult.InvalidDocumentReferences.Should().HaveCount(6);
    }

    [Test]
    public async Task It_deliberately_diverges_from_the_hash_resolver_for_a_case_variant_string_identity()
    {
        var fixture = _database.Fixture;

        // Plan decision 14, asserted rather than diffed. String identity comparison moved from
        // StringComparison.Ordinal in C# into the database, where SQL Server's default case-insensitive
        // collation matches a case-differing identity that the UUIDv5 hash never could. PostgreSQL keeps
        // the old behavior (see Given_PostgresqlReferenceResolverDifferential).
        DocumentReference[] documentReferences =
        [
            fixture.CreateWideIdentityReference(
                "$.caseVariantReference",
                fixture.CaseVariantWideIdentityReferentialId,
                stringKey: ReferenceResolverIntegrationFixture.WideIdentityCaseVariantStringValue
            ),
        ];

        var hashResult = await ResolveAsync(_hashResolverProvider, documentReferences, []);
        var naturalKeyResult = await ResolveAsync(_naturalKeyResolverProvider, documentReferences, []);

        hashResult
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                [("$.caseVariantReference", DocumentReferenceFailureReason.Missing)],
                because: "the UUIDv5 hash is computed over the exact identity text"
            );

        naturalKeyResult.InvalidDocumentReferences.Should().BeEmpty();
        naturalKeyResult
            .SuccessfulDocumentReferencesByPath[new JsonPath("$.caseVariantReference")]
            .DocumentId.Should()
            .Be(550L, "SQL Server's default collation is case insensitive");
    }

    [Test]
    public async Task It_resolves_a_mixed_document_and_descriptor_batch_identically()
    {
        var fixture = _database.Fixture;

        await AssertResolversAgreeAsync(
            "mixed document and descriptor batch",
            documentReferences:
            [
                fixture.CreateSchoolReference("$.schoolReference"),
                fixture.CreateEducationOrganizationReference("$.educationOrganizationReference"),
                fixture.CreateWideIdentityReference("$.wideIdentityReference"),
                fixture.CreateLocalEducationAgencyReference(
                    "$.localEducationAgencyReference",
                    fixture.MissingLocalEducationAgencyReferentialId,
                    localEducationAgencyId: 999_998
                ),
            ],
            descriptorReferences:
            [
                fixture.CreateSchoolTypeDescriptorReference("$.schoolTypeDescriptor"),
                fixture.CreateSchoolTypeDescriptorReference(
                    "$.programs[0].schoolTypeDescriptor",
                    fixture.MissingSchoolTypeDescriptorReferentialId,
                    fixture.MissingSchoolTypeDescriptorUri
                ),
            ]
        );
    }

    [Test]
    public async Task It_resolves_a_parameter_budget_crossing_lookup_set_identically()
    {
        // 2500 one-column entries: past the 2098-parameter per-command ceiling (so the adapter slices the
        // batch) and past the 2000-parameter per-VALUES-clause budget (so the builder also chunks within
        // a slice). Both bounds are exercised end to end against a live server.
        const int LargeLookupCount = 2500;
        var fixture = _database.Fixture;
        var schoolResourceKeyId = _database.MappingSet.ResourceKeyIdByResource[fixture.SchoolResource];

        await _database.SeedAsync(CreateAdditionalSchoolSeedData(LargeLookupCount, schoolResourceKeyId));

        DocumentReference[] documentReferences =
        [
            .. Enumerable
                .Range(0, LargeLookupCount)
                .Select(index =>
                    fixture.CreateSchoolReference(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"$.bulkSchools[{index}].schoolReference"
                        ),
                        CreateBulkSchoolReferentialId(index + 1),
                        schoolId: 300_000 + index
                    )
                ),
            fixture.CreateSchoolReference(
                "$.missingSchools[0].schoolReference",
                fixture.MissingSchoolReferentialId,
                schoolId: 999_999
            ),
            fixture.CreateSchoolReference(
                "$.missingSchools[1].schoolReference",
                fixture.MissingSchoolReferentialId,
                schoolId: 999_999
            ),
        ];

        var (_, naturalKeyResult) = await AssertResolversAgreeAsync(
            "parameter-budget-crossing bulk batch",
            documentReferences
        );

        naturalKeyResult.SuccessfulDocumentReferencesByPath.Should().HaveCount(LargeLookupCount);
        naturalKeyResult.InvalidDocumentReferences.Should().HaveCount(2);
    }

    private async Task<(
        ResolvedReferenceSet HashResult,
        ResolvedReferenceSet NaturalKeyResult
    )> AssertResolversAgreeAsync(
        string scenario,
        IReadOnlyList<DocumentReference>? documentReferences = null,
        IReadOnlyList<DescriptorReference>? descriptorReferences = null
    )
    {
        var hashResult = await ResolveAsync(
            _hashResolverProvider,
            documentReferences ?? [],
            descriptorReferences ?? []
        );
        var naturalKeyResult = await ResolveAsync(
            _naturalKeyResolverProvider,
            documentReferences ?? [],
            descriptorReferences ?? []
        );

        naturalKeyResult.ShouldResolveIdenticallyTo(hashResult, scenario);

        return (hashResult, naturalKeyResult);
    }

    private async Task<ResolvedReferenceSet> ResolveAsync(
        ServiceProvider serviceProvider,
        IReadOnlyList<DocumentReference> documentReferences,
        IReadOnlyList<DescriptorReference> descriptorReferences
    )
    {
        using var scope = serviceProvider.CreateScope();
        var instanceSelection = scope.ServiceProvider.GetRequiredService<IDataStoreSelection>();
        instanceSelection.SetSelectedDataStore(
            new DataStore(
                Id: 1,
                DataStoreType: "test",
                Name: "MssqlReferenceResolverDifferential",
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

    private static ServiceProvider CreateServiceProvider(bool useNaturalKeyResolver)
    {
        var services = new ServiceCollection();

        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IDataStoreSelection, DataStoreSelection>();
        services.AddTestReadableProfileProjector();

        if (useNaturalKeyResolver)
        {
            services.AddMssqlNaturalKeyReferenceResolver();
        }
        else
        {
            services.AddMssqlReferenceResolver();
        }

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }

    private static ReferentialId CreateMissReferentialId(int ordinal) =>
        new(Guid.Parse($"aa000000-0000-0000-0000-{ordinal:000000000000}"));

    private static ReferentialId CreateBulkSchoolReferentialId(int ordinal) =>
        new(Guid.Parse($"90000000-0000-0000-0000-{ordinal:000000000000}"));

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
                    2000L + index,
                    Guid.Parse($"80000000-0000-0000-0000-{index + 1:000000000000}"),
                    schoolResourceKeyId
                )),
        ];

        return new(
            ResourceKeys: [],
            Documents: documents,
            ReferentialIdentities:
            [
                .. Enumerable
                    .Range(0, count)
                    .Select(index => new ReferenceResolverReferentialIdentitySeed(
                        CreateBulkSchoolReferentialId(index + 1),
                        documents[index].DocumentId,
                        schoolResourceKeyId
                    )),
            ],
            Schools:
            [
                .. Enumerable
                    .Range(0, count)
                    .Select(index => new ReferenceResolverSchoolSeed(
                        documents[index].DocumentId,
                        300_000 + index
                    )),
            ],
            LocalEducationAgencies: [],
            Descriptors: []
        );
    }
}
