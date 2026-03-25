// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Old.Postgresql;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

file sealed class TestHostApplicationLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() { }
}

[TestFixture]
[Category("DatabaseIntegration")]
[Category("PostgresqlIntegration")]
[NonParallelizable]
public class Given_PostgresqlReferenceResolver
{
    private PostgresqlReferenceResolverTestDatabase _database = null!;
    private ServiceProvider _serviceProvider = null!;

    [SetUp]
    public async Task Setup()
    {
        _database = await PostgresqlReferenceResolverTestDatabase.CreateProvisionedAsync();
        await _database.SeedAsync();
        _serviceProvider = CreateServiceProvider();
    }

    [TearDown]
    public async Task TearDown()
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
    public async Task It_surfaces_concrete_target_mismatches_as_incompatible_target_type()
    {
        var result = await ResolveDocumentReferencesAsync(
            _database.Fixture.CreateSchoolReference(
                "$.localEducationAgencyReference",
                _database.Fixture.LocalEducationAgencyReferentialId
            )
        );

        result.SuccessfulDocumentReferencesByPath.Should().BeEmpty();
        result
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.localEducationAgencyReference", DocumentReferenceFailureReason.IncompatibleTargetType)
            );
        result
            .LookupsByReferentialId[_database.Fixture.LocalEducationAgencyReferentialId]
            .Result!.ResourceKeyId.Should()
            .Be(_database.MappingSet.ResourceKeyIdByResource[_database.Fixture.LocalEducationAgencyResource]);
    }

    [Test]
    public async Task It_resolves_abstract_alias_references_and_fails_closed_for_incompatible_alias_targets()
    {
        var result = await ResolveDocumentReferencesAsync(
            _database.Fixture.CreateEducationOrganizationReference("$.educationOrganizationReference"),
            _database.Fixture.CreateLocalEducationAgencyReference(
                "$.localEducationAgencyReference",
                _database.Fixture.EducationOrganizationAliasReferentialId
            )
        );

        var resolvedEducationOrganization = result.SuccessfulDocumentReferencesByPath[
            new JsonPath("$.educationOrganizationReference")
        ];

        resolvedEducationOrganization.DocumentId.Should().Be(101L);
        resolvedEducationOrganization
            .ResourceKeyId.Should()
            .Be(_database.MappingSet.ResourceKeyIdByResource[_database.Fixture.SchoolResource]);
        result
            .LookupsByReferentialId[_database.Fixture.EducationOrganizationAliasReferentialId]
            .Result!.ReferentialIdentityResourceKeyId.Should()
            .Be(
                _database.MappingSet.ResourceKeyIdByResource[_database.Fixture.EducationOrganizationResource]
            );
        result
            .InvalidDocumentReferences.Select(failure => (failure.Path.Value, failure.Reason))
            .Should()
            .Equal(
                ("$.localEducationAgencyReference", DocumentReferenceFailureReason.IncompatibleTargetType)
            );
    }

    private async Task<ResolvedReferenceSet> ResolveDocumentReferencesAsync(
        params DocumentReference[] documentReferences
    )
    {
        using var scope = _serviceProvider.CreateScope();
        var instanceSelection = scope.ServiceProvider.GetRequiredService<IDmsInstanceSelection>();
        instanceSelection.SetSelectedDmsInstance(
            new DmsInstance(
                Id: 1,
                InstanceType: "test",
                InstanceName: "PostgresqlReferenceResolverIntegration",
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
                DescriptorReferences: []
            )
        );
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<NpgsqlDataSourceCache>();
        services.AddScoped<IDmsInstanceSelection, DmsInstanceSelection>();
        services.AddScoped<NpgsqlDataSourceProvider>();
        services.AddPostgresqlReferenceResolver();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
    }
}
