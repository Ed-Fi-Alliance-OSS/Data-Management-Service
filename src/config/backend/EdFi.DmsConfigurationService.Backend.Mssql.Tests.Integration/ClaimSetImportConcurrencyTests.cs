// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class ClaimSetImportConcurrencyTests : DatabaseTest
{
    private class FakeClaimsHierarchyRepository : IClaimsHierarchyRepository
    {
        private int _saveCalls = 0;
        private readonly TaskCompletionSource _firstGetStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _releaseFirstGet = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public int SaveCalls => _saveCalls;

        public Task FirstGetStarted => _firstGetStarted.Task;

        public void ReleaseFirstGet() => _releaseFirstGet.TrySetResult();

        public Task<ClaimsHierarchyGetResult> GetClaimsHierarchy(DbTransaction? transaction = null)
        {
            if (!_firstGetStarted.Task.IsCompleted)
            {
                _firstGetStarted.TrySetResult();
                return WaitForReleaseThenReturnSuccess();
            }

            return Task.FromResult<ClaimsHierarchyGetResult>(CreateSuccessResult());
        }

        private async Task<ClaimsHierarchyGetResult> WaitForReleaseThenReturnSuccess()
        {
            await _releaseFirstGet.Task;
            return CreateSuccessResult();
        }

        private static ClaimsHierarchyGetResult.Success CreateSuccessResult()
        {
            var claims = new List<Claim>
            {
                new Claim
                {
                    Name = "Root",
                    ClaimSets = new List<ClaimSet>(),
                    Claims = new List<Claim>(),
                },
            };

            return new ClaimsHierarchyGetResult.Success(claims, DateTime.UtcNow, 1);
        }

        public Task<ClaimsHierarchySaveResult> SaveClaimsHierarchy(
            List<Claim> claimsHierarchy,
            DateTime existingLastModifiedDate,
            DbTransaction? transaction = null
        )
        {
            _saveCalls++;
            if (_saveCalls == 1)
            {
                // Simulate multi-user conflict on first call
                return Task.FromResult<ClaimsHierarchySaveResult>(
                    new ClaimsHierarchySaveResult.FailureMultiUserConflict()
                );
            }

            return Task.FromResult<ClaimsHierarchySaveResult>(new ClaimsHierarchySaveResult.Success());
        }
    }

    private class RetryingWarningsClaimsHierarchyRepository : IClaimsHierarchyRepository
    {
        private int _getCalls;
        private int _saveCalls;

        public Task<ClaimsHierarchyGetResult> GetClaimsHierarchy(DbTransaction? transaction = null)
        {
            _getCalls++;

            return Task.FromResult<ClaimsHierarchyGetResult>(
                _getCalls == 1 ? CreateInitialHierarchy() : CreateRetriedHierarchy()
            );
        }

        public Task<ClaimsHierarchySaveResult> SaveClaimsHierarchy(
            List<Claim> claimsHierarchy,
            DateTime existingLastModifiedDate,
            DbTransaction? transaction = null
        )
        {
            _saveCalls++;

            return Task.FromResult<ClaimsHierarchySaveResult>(
                _saveCalls == 1
                    ? new ClaimsHierarchySaveResult.FailureMultiUserConflict()
                    : new ClaimsHierarchySaveResult.Success()
            );
        }

        private static ClaimsHierarchyGetResult.Success CreateInitialHierarchy()
        {
            return new ClaimsHierarchyGetResult.Success(
                [
                    new Claim
                    {
                        Name = "Root",
                        ClaimSets = [],
                        Claims = [],
                    },
                ],
                DateTime.UtcNow,
                1
            );
        }

        private static ClaimsHierarchyGetResult.Success CreateRetriedHierarchy()
        {
            return new ClaimsHierarchyGetResult.Success(
                [
                    new Claim
                    {
                        Name = "Root",
                        ClaimSets = [],
                        Claims =
                        [
                            new Claim
                            {
                                Name = "TargetClaim",
                                ClaimSets = [],
                                Claims = [],
                            },
                        ],
                    },
                ],
                DateTime.UtcNow,
                2
            );
        }
    }

    [Test]
    public async Task Import_should_retry_on_concurrency_and_succeed()
    {
        // Arrange
        var fakeRepo = new FakeClaimsHierarchyRepository();

        var repository = new ClaimSetRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<ClaimSetRepository>.Instance,
            fakeRepo, // IClaimsHierarchyRepository
            new EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy.ClaimsHierarchyManager(),
            new TestAuditContext(),
            new TenantContextProvider()
        );

        var command = new ClaimSetImportCommand
        {
            Name = "ConcurrentImportTest",
            ResourceClaims = new List<ResourceClaim>(),
        };

        // Act
        var firstImport = repository.Import(command);
        await fakeRepo.FirstGetStarted;
        var secondImport = repository.Import(command);
        fakeRepo.ReleaseFirstGet();

        var results = await Task.WhenAll(firstImport, secondImport);

        // Assert
        results.Should().OnlyContain(result => result is ClaimSetImportResult.Success);
        fakeRepo.SaveCalls.Should().BeGreaterThanOrEqualTo(2);
        var resultIds = results.Select(result => ((ClaimSetImportResult.Success)result).Id).ToArray();
        resultIds.Should().OnlyContain(id => id == resultIds[0]);
    }

    [Test]
    public async Task Import_should_return_warnings_from_the_final_retry_only()
    {
        // Arrange
        var fakeRepo = new RetryingWarningsClaimsHierarchyRepository();

        var repository = new ClaimSetRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<ClaimSetRepository>.Instance,
            fakeRepo,
            new EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy.ClaimsHierarchyManager(),
            new TestAuditContext(),
            new TenantContextProvider()
        );

        var command = new ClaimSetImportCommand
        {
            Name = "RetryWarningImport",
            ResourceClaims =
            [
                new ResourceClaim
                {
                    Name = "TargetClaim",
                    Actions = [new ResourceClaimAction { Name = "Read", Enabled = true }],
                },
            ],
        };

        // Act
        var result = await repository.Import(command);

        // Assert
        result.Should().BeOfType<ClaimSetImportResult.Success>();
        ((ClaimSetImportResult.Success)result).Warnings.Should().BeNullOrEmpty();
    }
}
