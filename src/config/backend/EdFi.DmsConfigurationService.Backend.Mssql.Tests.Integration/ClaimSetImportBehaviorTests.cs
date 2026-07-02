// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Mssql.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class ClaimSetImportBehaviorTests : DatabaseTest
{
    private readonly IClaimSetRepository _repository = new ClaimSetRepository(
        MssqlTestConfiguration.DatabaseOptions,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaimSetRepository>.Instance,
        new ClaimsHierarchyRepository(
            MssqlTestConfiguration.DatabaseOptions,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaimsHierarchyRepository>.Instance,
            new TestAuditContext()
        ),
        new EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy.ClaimsHierarchyManager(),
        new TestAuditContext(),
        new TenantContextProvider()
    );

    protected async Task EnsureClaimsDataLoadedInternal()
    {
        var claimsDataLoader = new EdFi.DmsConfigurationService.Backend.ClaimsDataLoader.ClaimsDataLoader(
            new EdFi.DmsConfigurationService.Backend.Claims.ClaimsProvider(
                Microsoft
                    .Extensions
                    .Logging
                    .Abstractions
                    .NullLogger<EdFi.DmsConfigurationService.Backend.Claims.ClaimsProvider>
                    .Instance,
                Microsoft.Extensions.Options.Options.Create(
                    new EdFi.DmsConfigurationService.Backend.Claims.ClaimsOptions
                    {
                        ClaimsSource = EdFi.DmsConfigurationService.Backend.Claims.ClaimsSource.Embedded,
                        ClaimsDirectory = "",
                    }
                ),
                new EdFi.DmsConfigurationService.Backend.Claims.ClaimsValidator(
                    Microsoft
                        .Extensions
                        .Logging
                        .Abstractions
                        .NullLogger<EdFi.DmsConfigurationService.Backend.Claims.ClaimsValidator>
                        .Instance
                ),
                new EdFi.DmsConfigurationService.Backend.Claims.ClaimsFragmentComposer(
                    Microsoft
                        .Extensions
                        .Logging
                        .Abstractions
                        .NullLogger<EdFi.DmsConfigurationService.Backend.Claims.ClaimsFragmentComposer>
                        .Instance
                )
            ),
            _repository,
            new ClaimsHierarchyRepository(
                MssqlTestConfiguration.DatabaseOptions,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaimsHierarchyRepository>.Instance,
                new TestAuditContext()
            ),
            new ClaimsTableValidator(
                MssqlTestConfiguration.DatabaseOptions,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaimsTableValidator>.Instance
            ),
            new ClaimsDocumentRepository(
                MssqlTestConfiguration.DatabaseOptions,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaimsDocumentRepository>.Instance
            ),
            Microsoft
                .Extensions
                .Logging
                .Abstractions
                .NullLogger<EdFi.DmsConfigurationService.Backend.ClaimsDataLoader.ClaimsDataLoader>
                .Instance
        );

        var result = await claimsDataLoader.LoadInitialClaimsAsync();
        if (result is not ClaimsDataLoadResult.Success and not ClaimsDataLoadResult.AlreadyLoaded)
        {
            throw new System.InvalidOperationException($"Failed to load claims data: {result}");
        }
    }

    [Test]
    public async Task Import_should_upsert_existing_claimset()
    {
        await EnsureClaimsDataLoadedInternal();

        var command = new ClaimSetImportCommand
        {
            Name = "Test-Import-Upsert",
            ResourceClaims = new List<ResourceClaim>(),
        };

        var result1 = await _repository.Import(command);
        result1.Should().BeOfType<ClaimSetImportResult.Success>();
        var id1 = (result1 as ClaimSetImportResult.Success)!.Id;

        var command2 = new ClaimSetImportCommand
        {
            Name = "Test-Import-Upsert",
            ResourceClaims = new List<ResourceClaim>(),
        };
        var result2 = await _repository.Import(command2);
        result2.Should().BeOfType<ClaimSetImportResult.Success>();
        var id2 = (result2 as ClaimSetImportResult.Success)!.Id;

        id1.Should().BeGreaterThan(0);
        id2.Should().Be(id1);
    }

    [Test]
    public async Task Import_should_return_warnings_for_missing_claims()
    {
        await EnsureClaimsDataLoadedInternal();

        var rcMissing = new ResourceClaim
        {
            ClaimName = "http://example.org/nonexistent/Claim",
            Actions = new List<ResourceClaimAction>
            {
                new ResourceClaimAction { Name = "Read", Enabled = true },
            },
        };
        var command = new ClaimSetImportCommand
        {
            Name = "Test-Import-Warn",
            ResourceClaims = new List<ResourceClaim> { rcMissing },
        };

        var result = await _repository.Import(command);
        result.Should().BeOfType<ClaimSetImportResult.Success>();
        var success = result as ClaimSetImportResult.Success;
        var warnings = success!.Warnings ?? Enumerable.Empty<string>();
        warnings.Should().Contain("http://example.org/nonexistent/Claim");
    }

    [Test]
    public async Task Import_should_replace_existing_hierarchy_assignments_for_claim_set()
    {
        await EnsureClaimsDataLoadedInternal();

        var first = await _repository.Import(
            new ClaimSetImportCommand
            {
                Name = "Test-Import-Replacement",
                ResourceClaims =
                [
                    new ResourceClaim
                    {
                        ClaimName = "http://ed-fi.org/identity/claims/ed-fi/school",
                        Actions = [new ResourceClaimAction { Name = "Read", Enabled = true }],
                    },
                ],
            }
        );
        var id = ((ClaimSetImportResult.Success)first).Id;

        var second = await _repository.Import(
            new ClaimSetImportCommand
            {
                Name = "Test-Import-Replacement",
                ResourceClaims =
                [
                    new ResourceClaim
                    {
                        ClaimName = "http://ed-fi.org/identity/claims/ed-fi/student",
                        Actions = [new ResourceClaimAction { Name = "Read", Enabled = true }],
                    },
                ],
            }
        );
        ((ClaimSetImportResult.Success)second).Id.Should().Be(id);

        var result = (ClaimSetGetResult.Success)await _repository.GetClaimSet(id);
        result
            .ClaimSetResponse.ResourceClaims.Should()
            .ContainSingle(rc => rc.ClaimName == "http://ed-fi.org/identity/claims/ed-fi/student");
        result
            .ClaimSetResponse.ResourceClaims.Should()
            .NotContain(rc => rc.ClaimName == "http://ed-fi.org/identity/claims/ed-fi/school");
    }
}
