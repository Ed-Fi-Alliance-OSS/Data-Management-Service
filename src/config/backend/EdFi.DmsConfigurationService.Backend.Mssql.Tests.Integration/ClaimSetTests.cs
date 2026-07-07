// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Mssql.ClaimsDataLoader;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class ClaimSetTests : DatabaseTest
{
    private readonly IClaimSetRepository _repository = new ClaimSetRepository(
        MssqlTestConfiguration.DatabaseOptions,
        NullLogger<ClaimSetRepository>.Instance,
        new ClaimsHierarchyRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<ClaimsHierarchyRepository>.Instance,
            new TestAuditContext()
        ),
        new ClaimsHierarchyManager(),
        new TestAuditContext(),
        new TenantContextProvider()
    );

    protected async Task EnsureClaimsDataLoaded()
    {
        // Set up ClaimsDataLoader to load initial claims data
        var claimsOptions = Options.Create(
            new ClaimsOptions
            {
                ClaimsSource = ClaimsSource.Embedded, // Use embedded resource
                ClaimsDirectory = "",
            }
        );

        var claimsValidator = new ClaimsValidator(NullLogger<ClaimsValidator>.Instance);
        var claimsFragmentComposer = new ClaimsFragmentComposer(NullLogger<ClaimsFragmentComposer>.Instance);

        var claimsHierarchyRepository = new ClaimsHierarchyRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<ClaimsHierarchyRepository>.Instance,
            new TestAuditContext()
        );

        var claimsTableValidator = new ClaimsTableValidator(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<ClaimsTableValidator>.Instance
        );

        var claimsDocumentRepository = new ClaimsDocumentRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<ClaimsDocumentRepository>.Instance
        );

        var claimsProvider = new ClaimsProvider(
            NullLogger<ClaimsProvider>.Instance,
            claimsOptions,
            claimsValidator,
            claimsFragmentComposer
        );

        var claimsDataLoader = new Backend.ClaimsDataLoader.ClaimsDataLoader(
            claimsProvider,
            _repository,
            claimsHierarchyRepository,
            claimsTableValidator,
            claimsDocumentRepository,
            NullLogger<Backend.ClaimsDataLoader.ClaimsDataLoader>.Instance
        );

        // Load claims data - it will return AlreadyLoaded if data exists
        var result = await claimsDataLoader.LoadInitialClaimsAsync();
        if (result is not ClaimsDataLoadResult.Success and not ClaimsDataLoadResult.AlreadyLoaded)
        {
            throw new InvalidOperationException($"Failed to load claims data: {result}");
        }
    }

    [TestFixture]
    public class InsertTest : ClaimSetTests
    {
        private long _id;

        [SetUp]
        public async Task Setup()
        {
            await EnsureClaimsDataLoaded();

            ClaimSetInsertCommand claimSet = new() { Name = "Test-ClaimSet" };

            var result = await _repository.InsertClaimSet(claimSet);
            result.Should().BeOfType<ClaimSetInsertResult.Success>();
            _id = (result as ClaimSetInsertResult.Success)!.Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task Should_get_test_claimSet_from_get_all()
        {
            var getResult = await _repository.QueryClaimSet(
                new ClaimSetQuery
                {
                    Limit = 25,
                    Offset = 0,
                    Name = "Test-ClaimSet",
                }
            );
            getResult.Should().BeOfType<ClaimSetQueryResult.Success>();

            object claimSetFromDb = ((ClaimSetQueryResult.Success)getResult).ClaimSetResponses.Single();
            claimSetFromDb.Should().NotBeNull();
            claimSetFromDb.Should().BeOfType<ClaimSetResponse>();

            var reducedResponse = (ClaimSetResponse)claimSetFromDb;
            reducedResponse.Name.Should().Be("Test-ClaimSet");
            reducedResponse.Applications.HasValue.Should().BeTrue();
            reducedResponse.Applications!.Value.ValueKind.Should().Be(JsonValueKind.Array);
        }

        [Test]
        public async Task Should_get_test_claimSet_from_get_by_id()
        {
            var getByIdResult = await _repository.GetClaimSet(_id);
            getByIdResult.Should().BeOfType<ClaimSetGetResult.Success>();

            object claimSetFromDb = ((ClaimSetGetResult.Success)getByIdResult).ClaimSetResponse;
            claimSetFromDb.Should().BeOfType<ClaimSetResponse>();

            var reducedResponse = (ClaimSetResponse)claimSetFromDb;
            reducedResponse.Name.Should().Be("Test-ClaimSet");
            reducedResponse.Applications.HasValue.Should().BeTrue();
            reducedResponse.Applications!.Value.ValueKind.Should().Be(JsonValueKind.Array);
        }

        [Test]
        public async Task Should_return_unknown_failure_when_get_by_id_has_no_claims_hierarchy()
        {
            await ClaimsHierarchyTestHelper.ReinitializeClaimsHierarchy(clearOnly: true);

            var getByIdResult = await _repository.GetClaimSet(_id);

            getByIdResult.Should().BeOfType<ClaimSetGetResult.FailureUnknown>();
            ((ClaimSetGetResult.FailureUnknown)getByIdResult)
                .FailureMessage.Should()
                .Be("Claims hierarchy not found.");
        }

        [Test]
        public async Task Should_get_duplicate_failure()
        {
            ClaimSetInsertCommand claimSetDup = new() { Name = "Test-ClaimSet" };

            var resultDup = await _repository.InsertClaimSet(claimSetDup);
            resultDup.Should().BeOfType<ClaimSetInsertResult.FailureDuplicateClaimSetName>();
        }
    }

    [TestFixture]
    public class UpdateTests : ClaimSetTests
    {
        private ClaimSetInsertCommand _insertClaimSet = null!;
        private ClaimSetUpdateCommand _updateClaimSet = null!;
        private long _applicationId;
        private IApplicationRepository _applicationRepository;
        private IClaimsHierarchyRepository _claimsHierarchyRepository;
        private ClaimSetInsertResult _insertSystemReservedResult;

        [SetUp]
        public async Task Setup()
        {
            // Ensure claims data is loaded first
            await EnsureClaimsDataLoaded();

            IVendorRepository repository = new VendorRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<VendorRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            VendorInsertCommand vendor = new()
            {
                Company = "Test Company",
                ContactEmailAddress = "test@test.com",
                ContactName = "Fake Name",
                NamespacePrefixes = "FakePrefix1,FakePrefix2",
            };

            var vendorResult = await repository.InsertVendor(vendor);
            var vendorId = (vendorResult as VendorInsertResult.Success)!.Id;

            // Create the application to contain the claim set to be renamed
            _applicationRepository = new ApplicationRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ApplicationRepository>.Instance,
                new TestAuditContext(),
                new TenantContextProvider()
            );

            ApplicationInsertCommand application = new()
            {
                ApplicationName = "Test Application",
                VendorId = vendorId,
                ClaimSetName = "Test-Insert-ClaimSet",
                EducationOrganizationIds = [1, 255911001, 255911002],
            };

            var applicationResult = await _applicationRepository.InsertApplication(
                application,
                new() { ClientId = Guid.NewGuid().ToString(), ClientUuid = Guid.NewGuid() }
            );

            _applicationId = (applicationResult as ApplicationInsertResult.Success)?.Id ?? 0;

            // Insert claim set
            _insertClaimSet = new ClaimSetInsertCommand() { Name = "Test-Insert-ClaimSet" };
            var insertResult = await _repository.InsertClaimSet(_insertClaimSet);
            insertResult.Should().BeOfType<ClaimSetInsertResult.Success>();

            // Insert system-reserved claim set
            var insertSystemReservedClaimSet = new ClaimSetInsertCommand()
            {
                Name = "Test-Insert-System-Reserved-ClaimSet",
                IsSystemReserved = true,
            };
            _insertSystemReservedResult = await _repository.InsertClaimSet(insertSystemReservedClaimSet);
            _insertSystemReservedResult.Should().BeOfType<ClaimSetInsertResult.Success>();

            // Initialize claims hierarchy
            _claimsHierarchyRepository = new ClaimsHierarchyRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ClaimsHierarchyRepository>.Instance,
                new TestAuditContext()
            );

            // Get the existing claims hierarchy
            var existingHierarchyGetResult = await _claimsHierarchyRepository.GetClaimsHierarchy();
            existingHierarchyGetResult.Should().BeOfType<ClaimsHierarchyGetResult.Success>();
            var existingLastModifiedDate = (
                existingHierarchyGetResult as ClaimsHierarchyGetResult.Success
            )!.LastModifiedDate;

            var claimsHierarchy = new List<Claim>
            {
                new Claim
                {
                    Name = "RootClaim",
                    ClaimSets = new List<ClaimSet> { new ClaimSet { Name = "Test-Insert-ClaimSet" } },
                    Claims = new List<Claim>
                    {
                        new Claim
                        {
                            Name = "ChildClaim",
                            ClaimSets = new List<ClaimSet> { new ClaimSet { Name = "Test-Insert-ClaimSet" } },
                        },
                    },
                },
            };

            var saveResult = await _claimsHierarchyRepository.SaveClaimsHierarchy(
                claimsHierarchy,
                existingLastModifiedDate
            );
            saveResult.Should().BeOfType<ClaimsHierarchySaveResult.Success>();

            // Update the claim set
            _updateClaimSet = new ClaimSetUpdateCommand()
            {
                Id = (insertResult as ClaimSetInsertResult.Success)!.Id,
                Name = "Test-Update-ClaimSet",
            };

            var updateResult = await _repository.UpdateClaimSet(_updateClaimSet);
            updateResult.Should().BeOfType<ClaimSetUpdateResult.Success>();
        }

        [Test]
        public async Task Should_get_updated_and_system_reserved_claimSets_from_get_all()
        {
            var getResult = await _repository.QueryClaimSet(new ClaimSetQuery() { Limit = 100, Offset = 0 });
            getResult.Should().BeOfType<ClaimSetQueryResult.Success>();

            var claimSets = ((ClaimSetQueryResult.Success)getResult).ClaimSetResponses;

            // Find the updated claim set
            var updatedClaimSet = claimSets.FirstOrDefault(cs => cs.Name == "Test-Update-ClaimSet");
            updatedClaimSet.Should().NotBeNull();
            updatedClaimSet!.IsSystemReserved.Should().Be(false);

            // Find the system reserved claim set
            var systemReservedClaimSet = claimSets.FirstOrDefault(cs =>
                cs.Name == "Test-Insert-System-Reserved-ClaimSet"
            );
            systemReservedClaimSet.Should().NotBeNull();
            systemReservedClaimSet!.IsSystemReserved.Should().Be(true);
        }

        [Test]
        public async Task Should_get_test_claimSet_from_get_by_id()
        {
            var getByIdResult = await _repository.GetClaimSet(_updateClaimSet.Id);
            getByIdResult.Should().BeOfType<ClaimSetGetResult.Success>();

            object claimSetFromDb = ((ClaimSetGetResult.Success)getByIdResult).ClaimSetResponse;
            claimSetFromDb.Should().BeOfType<ClaimSetResponse>();

            var reducedResponse = (ClaimSetResponse)claimSetFromDb;
            reducedResponse.Name.Should().Be("Test-Update-ClaimSet");
            reducedResponse.IsSystemReserved.Should().Be(false);
        }

        [Test]
        public async Task Should_get_renamed_claimSet_from_application_by_id()
        {
            var getByIdResult = await _applicationRepository.GetApplication(_applicationId);
            getByIdResult.Should().BeOfType<ApplicationGetResult.Success>();

            var applicationResponse = ((ApplicationGetResult.Success)getByIdResult).ApplicationResponse;

            applicationResponse.ClaimSetName.Should().Be("Test-Update-ClaimSet");
        }

        [Test]
        public async Task Should_update_all_occurrences_of_claimSet_name_in_claims_hierarchy()
        {
            // Retrieve the updated claims hierarchy
            var hierarchyResult = await _claimsHierarchyRepository.GetClaimsHierarchy();
            hierarchyResult.Should().BeOfType<ClaimsHierarchyGetResult.Success>();

            var claims = ((ClaimsHierarchyGetResult.Success)hierarchyResult).Claims;

            // Verify that "Test-Insert-ClaimSet" no longer exists
            bool containsOldClaimSet = claims.Exists(c => ContainsClaimSet(c, "Test-Insert-ClaimSet"));
            containsOldClaimSet.Should().BeFalse();

            // Verify that "Test-Update-ClaimSet" exists in all appropriate places
            bool containsNewClaimSet = claims.Exists(c => ContainsClaimSet(c, "Test-Update-ClaimSet"));
            containsNewClaimSet.Should().BeTrue();
        }

        [Test]
        public async Task Should_return_failure_when_attempting_to_update_system_reserved_claim_set()
        {
            // Attempt to update the system-reserved claim set
            var updateSystemReserved = new ClaimSetUpdateCommand()
            {
                Id = (_insertSystemReservedResult as ClaimSetInsertResult.Success)!.Id,
                Name = "Test-Update-System-Reserved-ClaimSet",
            };

            var updateSystemReservedResult = await _repository.UpdateClaimSet(updateSystemReserved);
            updateSystemReservedResult.Should().BeOfType<ClaimSetUpdateResult.FailureSystemReserved>();
        }

        private bool ContainsClaimSet(Claim claim, string claimSetName)
        {
            if (claim.ClaimSets.Exists(cs => cs.Name == claimSetName))
            {
                return true;
            }

            if (claim.Claims.Exists(c => ContainsClaimSet(c, claimSetName)))
            {
                return true;
            }

            return false;
        }
    }

    [TestFixture]
    public class UpdateRetryPolicyTests : ClaimSetTests
    {
        private ClaimSetInsertCommand _insertClaimSet = null!;
        private ClaimSetUpdateCommand _updateClaimSet = null!;

        [SetUp]
        public async Task Setup()
        {
            // Ensure claims data is loaded first
            await EnsureClaimsDataLoaded();

            // Insert claim set
            _insertClaimSet = new ClaimSetInsertCommand() { Name = "Test-Retry-ClaimSet" };
            var insertResult = await _repository.InsertClaimSet(_insertClaimSet);
            insertResult.Should().BeOfType<ClaimSetInsertResult.Success>();

            await InitializeClaimsHierarchy();

            // Prepare update command
            _updateClaimSet = new ClaimSetUpdateCommand() { Name = "Test-Retry-Updated-ClaimSet" };
            _updateClaimSet.Id = (insertResult as ClaimSetInsertResult.Success)!.Id;

            async Task InitializeClaimsHierarchy()
            {
                var claimsHierarchyRepository = CreateClaimsHierarchyRepository();

                var claimsHierarchy = new List<Claim>
                {
                    new Claim
                    {
                        Name = "RootClaim",
                        ClaimSets = new List<ClaimSet> { new ClaimSet { Name = "Test-Retry-ClaimSet" } },
                        Claims = new List<Claim>
                        {
                            new Claim
                            {
                                Name = "ChildClaim",
                                ClaimSets = new List<ClaimSet>
                                {
                                    new ClaimSet { Name = "Test-Retry-ClaimSet" },
                                },
                            },
                        },
                    },
                };

                await claimsHierarchyRepository.SaveClaimsHierarchy(claimsHierarchy, default);
            }
        }

        [TestCase(4, false)]
        [TestCase(3, true)]
        public async Task Should_retry_3_times_on_lastmodifieddate_conflict(
            int multiUserConflictCount,
            bool expectSuccess
        )
        {
            // Arrange

            // Wrap the repository to introduce multi-user conflicts during save
            var claimsHierarchyRepository = new ClaimsHierarchyRepositoryMultiUserTestDecorator(
                CreateClaimsHierarchyRepository(),
                multiUserConflictCount
            );

            var claimSetRepository = new ClaimSetRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ClaimSetRepository>.Instance,
                claimsHierarchyRepository,
                new ClaimsHierarchyManager(),
                new TestAuditContext(),
                new TenantContextProvider()
            );

            // Act

            // Attempt to update the claim set, expecting retries
            var updateResult = await claimSetRepository.UpdateClaimSet(_updateClaimSet);

            // Assert

            // Ensure the correct number of attempts were made to apply the claim name change to the hierarchy
            claimsHierarchyRepository
                .SaveClaimsHierarchyInvocationCount.Should()
                .Be(Math.Min(4, multiUserConflictCount + 1));

            if (expectSuccess)
            {
                // Verify that the update succeeded after retries were attempted
                updateResult.Should().BeOfType<ClaimSetUpdateResult.Success>();
            }
            else
            {
                // Verify that the update failed after 3 retries
                updateResult.Should().BeOfType<ClaimSetUpdateResult.FailureMultiUserConflict>();
            }
        }

        private static ClaimsHierarchyRepository CreateClaimsHierarchyRepository()
        {
            // Initialize claims hierarchy
            var claimsHierarchyRepository = new ClaimsHierarchyRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ClaimsHierarchyRepository>.Instance,
                new TestAuditContext()
            );

            return claimsHierarchyRepository;
        }

        private class ClaimsHierarchyRepositoryMultiUserTestDecorator(
            IClaimsHierarchyRepository _claimsHierarchyRepository,
            int _conflictingUpdateCount = int.MaxValue
        ) : IClaimsHierarchyRepository
        {
            private readonly IClaimsHierarchyRepository _multiUserClaimsHierarchyRepository =
                new ClaimsHierarchyRepository(
                    MssqlTestConfiguration.DatabaseOptions,
                    NullLogger<ClaimsHierarchyRepository>.Instance,
                    new TestAuditContext()
                );

            private int _remainingConflictingUpdateCount = _conflictingUpdateCount;

            public Task<ClaimsHierarchyGetResult> GetClaimsHierarchy(DbTransaction? transaction = null)
            {
                // Pass the call through unmodified
                return _claimsHierarchyRepository.GetClaimsHierarchy(transaction);
            }

            public int SaveClaimsHierarchyInvocationCount;

            public async Task<ClaimsHierarchySaveResult> SaveClaimsHierarchy(
                List<Claim> claimsHierarchy,
                DateTime existingLastModifiedDate,
                DbTransaction? transaction = null
            )
            {
                // Increment invocation counter for inspection by tests
                SaveClaimsHierarchyInvocationCount++;

                // If the call is part of an existing transaction, force a multi-user conflict on a separate connection
                if (transaction != null && _remainingConflictingUpdateCount > 0)
                {
                    _remainingConflictingUpdateCount--;

                    var hierarchyResult = GetCurrentClaimsHierarchy();

                    hierarchyResult.claims.Add(
                        new Claim() { Name = $"Test-MultiUser-Claim-{Random.Shared.Next(100000)}" }
                    );

                    var result = await _multiUserClaimsHierarchyRepository.SaveClaimsHierarchy(
                        hierarchyResult.claims,
                        hierarchyResult.lastModifiedDate
                    );

                    result.Should().BeOfType<ClaimsHierarchySaveResult.Success>();
                }

                // Pass the call through
                return await _claimsHierarchyRepository.SaveClaimsHierarchy(
                    claimsHierarchy,
                    existingLastModifiedDate,
                    transaction
                );

                (List<Claim> claims, DateTime lastModifiedDate) GetCurrentClaimsHierarchy()
                {
                    var claimsResult = _multiUserClaimsHierarchyRepository
                        .GetClaimsHierarchy()
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();

                    claimsResult.Should().BeOfType<ClaimsHierarchyGetResult.Success>();

                    var success = (claimsResult as ClaimsHierarchyGetResult.Success)!;

                    return (success.Claims, success.LastModifiedDate);
                }
            }
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
            await EnsureClaimsDataLoaded();

            var insertResult1 = await _repository.InsertClaimSet(
                new ClaimSetInsertCommand() { Name = "Test-One" }
            );
            _id1 = ((ClaimSetInsertResult.Success)insertResult1).Id;

            var insertResult2 = await _repository.InsertClaimSet(
                new ClaimSetInsertCommand() { Name = "Test-Two", IsSystemReserved = true }
            );

            _id2 = ((ClaimSetInsertResult.Success)insertResult2).Id;

            var result = await _repository.DeleteClaimSet(_id1);
            result.Should().BeOfType<ClaimSetDeleteResult.Success>();
        }

        [Test]
        public async Task Should_get_one_test_claimSet_from_get_all()
        {
            var result = await _repository.QueryClaimSet(new ClaimSetQuery() { Limit = 25, Offset = 0 });
            result.Should().BeOfType<ClaimSetQueryResult.Success>();

            ((ClaimSetQueryResult.Success)result)
                .ClaimSetResponses.Should()
                .ContainSingle(claimSet => claimSet.Name == "Test-Two");
        }

        [Test]
        public async Task Should_return_not_found_for_deleted_claim_set()
        {
            var result1 = await _repository.GetClaimSet(_id1);
            result1.Should().BeOfType<ClaimSetGetResult.FailureNotFound>();
        }

        [Test]
        public async Task Should_get_remaining_test_claimSet_successfully()
        {
            var result2 = await _repository.GetClaimSet(_id2);
            result2.Should().BeOfType<ClaimSetGetResult.Success>();
        }

        [Test]
        public async Task Should_return_not_found_when_attempting_to_delete_non_existing_claim_set_id()
        {
            var deleteResult = await _repository.DeleteClaimSet(int.MaxValue);
            deleteResult.Should().BeOfType<ClaimSetDeleteResult.FailureNotFound>();
        }

        [Test]
        public async Task Should_return_system_reserved_error_when_attempting_to_delete_system_reserved_claim_set()
        {
            var deleteResult = await _repository.DeleteClaimSet(_id2);
            deleteResult.Should().BeOfType<ClaimSetDeleteResult.FailureSystemReserved>();
        }

        [Test]
        public async Task Should_remove_claim_set_from_hierarchy_when_deleted()
        {
            await ClaimsHierarchyTestHelper.ReinitializeClaimsHierarchy();

            var importResult = await _repository.Import(
                new ClaimSetImportCommand
                {
                    Name = "Delete-With-Permissions",
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
            if (importResult is ClaimSetImportResult.FailureUnknown failureUnknown)
            {
                Assert.Fail(failureUnknown.FailureMessage);
            }
            importResult.Should().BeOfType<ClaimSetImportResult.Success>();
            var id = ((ClaimSetImportResult.Success)importResult).Id;

            var deleteResult = await _repository.DeleteClaimSet(id);
            deleteResult.Should().BeOfType<ClaimSetDeleteResult.Success>();

            var claimsHierarchyRepository = new ClaimsHierarchyRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ClaimsHierarchyRepository>.Instance,
                new TestAuditContext()
            );
            var hierarchy = (ClaimsHierarchyGetResult.Success)
                await claimsHierarchyRepository.GetClaimsHierarchy();

            hierarchy.Claims.Exists(c => ContainsClaimSet(c, "Delete-With-Permissions")).Should().BeFalse();
        }

        private static bool ContainsClaimSet(Claim claim, string claimSetName)
        {
            return claim.ClaimSets.Exists(cs => cs.Name == claimSetName)
                || claim.Claims.Exists(c => ContainsClaimSet(c, claimSetName));
        }
    }

    [TestFixture]
    public class ExportTest : ClaimSetTests
    {
        private long _id;

        [SetUp]
        public async Task Setup()
        {
            await EnsureClaimsDataLoaded();

            ClaimSetInsertCommand claimSet = new() { Name = "Test-Export-ClaimSet" };

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
            valueFromDb.Name.Should().Be("Test-Export-ClaimSet");
        }

        [Test]
        public async Task Should_return_unknown_failure_when_export_has_no_claims_hierarchy()
        {
            await ClaimsHierarchyTestHelper.ReinitializeClaimsHierarchy(clearOnly: true);

            var result = await _repository.Export(_id);

            result.Should().BeOfType<ClaimSetExportResult.FailureUnknown>();
            ((ClaimSetExportResult.FailureUnknown)result)
                .FailureMessage.Should()
                .Be("Claims hierarchy not found.");
        }

        [Test]
        public async Task Should_return_configured_claims_only_with_default_and_override_strategies()
        {
            await ClaimsHierarchyTestHelper.ReinitializeClaimsHierarchy(clearOnly: true);

            var claimsHierarchyRepository = new ClaimsHierarchyRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ClaimsHierarchyRepository>.Instance,
                new TestAuditContext()
            );

            var claimsHierarchy = BuildHierarchy();
            var saveResult = await claimsHierarchyRepository.SaveClaimsHierarchy(
                claimsHierarchy,
                DateTime.UtcNow
            );
            saveResult.Should().BeOfType<ClaimsHierarchySaveResult.Success>();

            var insertResult = await _repository.InsertClaimSet(
                new ClaimSetInsertCommand { Name = "Test-Export-Configured-ClaimSet" }
            );
            insertResult.Should().BeOfType<ClaimSetInsertResult.Success>();
            var claimSetId = ((ClaimSetInsertResult.Success)insertResult).Id;

            var getResult = await _repository.GetClaimSet(claimSetId);
            getResult.Should().BeOfType<ClaimSetGetResult.Success>();

            var exportResult = await _repository.Export(claimSetId);
            exportResult.Should().BeOfType<ClaimSetExportResult.Success>();

            var getResponse = ((ClaimSetGetResult.Success)getResult).ClaimSetResponse;
            var exportResponse = ((ClaimSetExportResult.Success)exportResult).ClaimSetExportResponse;

            AssertExportShape(getResponse);
            AssertExportShape(exportResponse);
        }

        private static List<Claim> BuildHierarchy()
        {
            const string ClaimSetName = "Test-Export-Configured-ClaimSet";
            const string RootClaimName = "http://ed-fi.org/identity/claims/domains/exportRoot";
            const string ChildClaimName = "http://ed-fi.org/identity/claims/ed-fi/exportChild";
            const string GrandchildClaimName = "http://ed-fi.org/identity/claims/ed-fi/exportGrandchild";

            return
            [
                new Claim
                {
                    Name = RootClaimName,
                    DefaultAuthorization = new DefaultAuthorization
                    {
                        Actions =
                        [
                            new DefaultAction
                            {
                                Name = "Read",
                                AuthorizationStrategies =
                                [
                                    new EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy.AuthorizationStrategy
                                    {
                                        Name = "DefaultRootStrategy",
                                    },
                                ],
                            },
                        ],
                    },
                    ClaimSets =
                    [
                        new ClaimSet
                        {
                            Name = ClaimSetName,
                            Actions = [new ClaimSetAction { Name = "Read" }],
                        },
                    ],
                    Claims =
                    [
                        new Claim
                        {
                            Name = ChildClaimName,
                            DefaultAuthorization = new DefaultAuthorization
                            {
                                Actions =
                                [
                                    new DefaultAction
                                    {
                                        Name = "Read",
                                        AuthorizationStrategies =
                                        [
                                            new EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy.AuthorizationStrategy
                                            {
                                                Name = "DefaultChildStrategy",
                                            },
                                        ],
                                    },
                                ],
                            },
                            ClaimSets =
                            [
                                new ClaimSet
                                {
                                    Name = ClaimSetName,
                                    Actions =
                                    [
                                        new ClaimSetAction
                                        {
                                            Name = "Read",
                                            AuthorizationStrategyOverrides =
                                            [
                                                new EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy.AuthorizationStrategy
                                                {
                                                    Name = "OverrideChildStrategy",
                                                },
                                            ],
                                        },
                                        new ClaimSetAction { Name = "Update" },
                                    ],
                                },
                            ],
                            Claims =
                            [
                                new Claim
                                {
                                    Name = GrandchildClaimName,
                                    ClaimSets = [],
                                    Claims = [],
                                },
                            ],
                        },
                    ],
                },
            ];
        }

        private static void AssertExportShape(ClaimSetResponse response)
        {
            const string ClaimSetName = "Test-Export-Configured-ClaimSet";
            const string RootClaimName = "http://ed-fi.org/identity/claims/domains/exportRoot";
            const string ChildClaimName = "http://ed-fi.org/identity/claims/ed-fi/exportChild";
            const string GrandchildClaimName = "http://ed-fi.org/identity/claims/ed-fi/exportGrandchild";

            response.Name.Should().Be(ClaimSetName);
            response.IsSystemReserved.Should().BeFalse();
            response.ResourceClaims.Should().HaveCount(2);
            response.ResourceClaims.Should().NotContain(rc => rc.ClaimName == GrandchildClaimName);

            var rootClaim = response.ResourceClaims!.Single(rc => rc.ClaimName == RootClaimName);
            rootClaim.Name.Should().Be("exportRoot");
            rootClaim.ParentClaimName.Should().BeNull();
            rootClaim.Actions.Should().ContainSingle(action => action.Name == "Read");
            rootClaim.Actions.Should().NotContain(action => action.Name == "Update");
            rootClaim
                .DefaultAuthorizationStrategies.Should()
                .ContainSingle(strategy => strategy.ActionName == "Read");
            rootClaim
                .DefaultAuthorizationStrategies[0]
                .AuthorizationStrategies.Should()
                .ContainSingle(strategy => strategy.AuthorizationStrategyName == "DefaultRootStrategy");
            rootClaim.AuthorizationStrategyOverrides.Should().BeEmpty();

            var childClaim = response.ResourceClaims!.Single(rc => rc.ClaimName == ChildClaimName);
            childClaim.Name.Should().Be("exportChild");
            childClaim.ParentClaimName.Should().Be(RootClaimName);
            childClaim.Actions.Should().ContainSingle(action => action.Name == "Read");
            childClaim.Actions.Should().ContainSingle(action => action.Name == "Update");
            childClaim
                .DefaultAuthorizationStrategies.Should()
                .ContainSingle(strategy => strategy.ActionName == "Read");
            childClaim
                .DefaultAuthorizationStrategies[0]
                .AuthorizationStrategies.Should()
                .ContainSingle(strategy => strategy.AuthorizationStrategyName == "DefaultChildStrategy");
            childClaim
                .AuthorizationStrategyOverrides.Should()
                .ContainSingle(strategy => strategy.ActionName == "Read");
            childClaim
                .AuthorizationStrategyOverrides[0]
                .AuthorizationStrategies.Should()
                .ContainSingle(strategy => strategy.AuthorizationStrategyName == "OverrideChildStrategy");
        }
    }

    [TestFixture]
    public class ImportTest : ClaimSetTests
    {
        private long _id;

        [SetUp]
        public async Task Setup()
        {
            // Ensure claims data is loaded first
            await EnsureClaimsDataLoaded();

            ClaimSetImportCommand claimSet = new() { Name = "Test-Import-ClaimSet", ResourceClaims = [] };

            var result = await _repository.Import(claimSet);
            result.Should().BeOfType<ClaimSetImportResult.Success>();
            _id = (result as ClaimSetImportResult.Success)!.Id;
            _id.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task Should_get_multiple_test_claimSet_from_get_all()
        {
            var result = await _repository.QueryClaimSet(new ClaimSetQuery() { Limit = 10, Offset = 0 });
            result.Should().BeOfType<ClaimSetQueryResult.Success>();

            ((ClaimSetQueryResult.Success)result)
                .ClaimSetResponses.Count()
                .Should()
                .BeGreaterThan(0)
                .And.BeLessOrEqualTo(10);
        }

        [Test]
        public async Task Should_get_test_claimSet_from_get_by_id()
        {
            var getByIdResult = await _repository.GetClaimSet(_id);
            getByIdResult.Should().BeOfType<ClaimSetGetResult.Success>();

            object claimSetFromDb = ((ClaimSetGetResult.Success)getByIdResult).ClaimSetResponse;
            claimSetFromDb.Should().BeOfType<ClaimSetResponse>();

            var response = (ClaimSetResponse)claimSetFromDb;
            response.Name.Should().Be("Test-Import-ClaimSet");
            response.IsSystemReserved.Should().BeFalse();
        }

        [Test]
        public async Task Should_upsert_existing_claim_set_on_import()
        {
            ClaimSetImportCommand claimSetDup = new() { Name = "Test-Import-ClaimSet", ResourceClaims = [] };

            var resultDup = await _repository.Import(claimSetDup);
            resultDup.Should().BeOfType<ClaimSetImportResult.Success>();
            ((ClaimSetImportResult.Success)resultDup).Id.Should().Be(_id);
        }

        [Test]
        public async Task Should_get_unknown_failure_when_no_claims_hierarchy_exists()
        {
            // Don't reinitialize the claims hierarchy
            await ClaimsHierarchyTestHelper.ReinitializeClaimsHierarchy(clearOnly: true);

            ClaimSetImportCommand command = new() { Name = "Test-New-ClaimSet", ResourceClaims = [] };

            var resultNoHierarchy = await _repository.Import(command);
            resultNoHierarchy.Should().BeOfType<ClaimSetImportResult.FailureUnknown>();

            (resultNoHierarchy as ClaimSetImportResult.FailureUnknown)!
                .FailureMessage.Should()
                .Be("Claims hierarchy not found.");
        }

        [Test]
        public async Task Should_return_system_reserved_failure_when_import_targets_existing_system_reserved_claim_set()
        {
            var systemReservedInsert = await _repository.InsertClaimSet(
                new ClaimSetInsertCommand { Name = "Test-Import-System-Reserved", IsSystemReserved = true }
            );
            var systemReservedId = ((ClaimSetInsertResult.Success)systemReservedInsert).Id;

            var result = await _repository.Import(
                new ClaimSetImportCommand { Name = "Test-Import-System-Reserved", ResourceClaims = [] }
            );

            result.Should().BeOfType<ClaimSetImportResult.FailureSystemReserved>();

            var getByIdResult = await _repository.GetClaimSet(systemReservedId);
            getByIdResult.Should().BeOfType<ClaimSetGetResult.Success>();
            ((ClaimSetGetResult.Success)getByIdResult).ClaimSetResponse.IsSystemReserved.Should().BeTrue();
        }

        [Test]
        public async Task Should_round_trip_export_payload_back_into_import()
        {
            await ClaimsHierarchyTestHelper.ReinitializeClaimsHierarchy(clearOnly: true);

            var claimsHierarchyRepository = new ClaimsHierarchyRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<ClaimsHierarchyRepository>.Instance,
                new TestAuditContext()
            );

            var claimsHierarchy = BuildRoundTripHierarchy();
            var saveResult = await claimsHierarchyRepository.SaveClaimsHierarchy(
                claimsHierarchy,
                DateTime.UtcNow
            );
            saveResult.Should().BeOfType<ClaimsHierarchySaveResult.Success>();

            var command = new ClaimSetImportCommand
            {
                Name = "Test-Import-RoundTrip",
                ResourceClaims =
                [
                    new ResourceClaim
                    {
                        ClaimName = "http://ed-fi.org/identity/claims/domains/roundTripRoot",
                        Actions =
                        [
                            new ResourceClaimAction { Name = "Read", Enabled = true },
                            new ResourceClaimAction { Name = "Update", Enabled = false },
                        ],
                    },
                    new ResourceClaim
                    {
                        ClaimName = "http://ed-fi.org/identity/claims/ed-fi/roundTripChild",
                        ParentClaimName = "http://ed-fi.org/identity/claims/domains/roundTripRoot",
                        Actions =
                        [
                            new ResourceClaimAction { Name = "Read", Enabled = true },
                            new ResourceClaimAction { Name = "Delete", Enabled = true },
                        ],
                        AuthorizationStrategyOverrides =
                        [
                            new ClaimSetResourceClaimActionAuthStrategies
                            {
                                ActionName = "Read",
                                AuthorizationStrategies =
                                [
                                    new EdFi.DmsConfigurationService.DataModel.Model.ClaimSets.AuthorizationStrategy
                                    {
                                        AuthorizationStrategyName = "OverrideRoundTripStrategy",
                                    },
                                ],
                            },
                        ],
                    },
                ],
            };

            var firstImport = await _repository.Import(command);
            firstImport.Should().BeOfType<ClaimSetImportResult.Success>();
            var claimSetId = ((ClaimSetImportResult.Success)firstImport).Id;

            var exportResult = await _repository.Export(claimSetId);
            exportResult.Should().BeOfType<ClaimSetExportResult.Success>();

            var exportResponse = ((ClaimSetExportResult.Success)exportResult).ClaimSetExportResponse;
            exportResponse.ResourceClaims.Should().HaveCount(2);
            exportResponse
                .ResourceClaims.Should()
                .NotContain(rc => rc.Actions!.Any(action => action.Name == "Update"));

            var roundTripJson = JsonSerializer.Serialize(
                exportResponse,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            );
            var roundTripCommand = JsonSerializer.Deserialize<ClaimSetImportCommand>(
                roundTripJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            );

            roundTripCommand.Should().NotBeNull();
            roundTripCommand!.Name.Should().Be(command.Name);
            roundTripCommand.ResourceClaims.Should().HaveCount(2);

            var roundTripRoot = roundTripCommand.ResourceClaims!.Single(rc =>
                rc.ClaimName == "http://ed-fi.org/identity/claims/domains/roundTripRoot"
            );
            roundTripRoot.Actions.Should().ContainSingle(action => action.Name == "Read");
            roundTripRoot.Actions.Should().NotContain(action => action.Name == "Update");

            var roundTripChild = roundTripCommand.ResourceClaims!.Single(rc =>
                rc.ClaimName == "http://ed-fi.org/identity/claims/ed-fi/roundTripChild"
            );
            roundTripChild
                .ParentClaimName.Should()
                .Be("http://ed-fi.org/identity/claims/domains/roundTripRoot");
            roundTripChild
                .AuthorizationStrategyOverrides.Should()
                .ContainSingle(overrideStrategy => overrideStrategy.ActionName == "Read");
            roundTripChild
                .AuthorizationStrategyOverrides[0]
                .AuthorizationStrategies.Should()
                .ContainSingle(strategy => strategy.AuthorizationStrategyName == "OverrideRoundTripStrategy");

            var secondImport = await _repository.Import(roundTripCommand);
            secondImport.Should().BeOfType<ClaimSetImportResult.Success>();
            ((ClaimSetImportResult.Success)secondImport).Id.Should().Be(claimSetId);

            var finalGet = await _repository.GetClaimSet(claimSetId);
            finalGet.Should().BeOfType<ClaimSetGetResult.Success>();
            ((ClaimSetGetResult.Success)finalGet).ClaimSetResponse.ResourceClaims.Should().HaveCount(2);
        }

        private static List<Claim> BuildRoundTripHierarchy()
        {
            return
            [
                new Claim
                {
                    Name = "http://ed-fi.org/identity/claims/domains/roundTripRoot",
                    ClaimSets = [new ClaimSet { Name = "Test-Import-RoundTrip" }],
                    Claims =
                    [
                        new Claim
                        {
                            Name = "http://ed-fi.org/identity/claims/ed-fi/roundTripChild",
                            ClaimSets = [new ClaimSet { Name = "Test-Import-RoundTrip" }],
                            Claims = [],
                        },
                    ],
                },
            ];
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
            await EnsureClaimsDataLoaded();

            ClaimSetInsertCommand claimSet = new() { Name = "Original-ClaimSet" };

            var result = await _repository.InsertClaimSet(claimSet);
            result.Should().BeOfType<ClaimSetInsertResult.Success>();
            _id = (result as ClaimSetInsertResult.Success)!.Id;
            _id.Should().BeGreaterThan(0);

            ClaimSetCopyCommand command = new() { OriginalId = _id, Name = "Copy-Test-ClaimSet" };

            var copy = await _repository.Copy(command);
            copy.Should().BeOfType<ClaimSetCopyResult.Success>();
            _idCopy = (copy as ClaimSetCopyResult.Success)!.Id;
            _idCopy.Should().BeGreaterThan(0);
        }

        [Test]
        public async Task Should_get_two_claimSet_from_get_all()
        {
            var result = await _repository.QueryClaimSet(new ClaimSetQuery() { Limit = 25, Offset = 0 });
            result.Should().BeOfType<ClaimSetQueryResult.Success>();

            var claimSetNames = ((ClaimSetQueryResult.Success)result)
                .ClaimSetResponses.Select(cs => cs.Name)
                .ToList();
            claimSetNames.Should().Contain("Original-ClaimSet");
            claimSetNames.Should().Contain("Copy-Test-ClaimSet");
        }

        [Test]
        public async Task Should_get_claimSet_from_get_by_id()
        {
            var getByIdResult1 = await _repository.GetClaimSet(_id);
            getByIdResult1.Should().BeOfType<ClaimSetGetResult.Success>();

            object claimSetFromDb1 = ((ClaimSetGetResult.Success)getByIdResult1).ClaimSetResponse;
            claimSetFromDb1.Should().BeOfType<ClaimSetResponse>();

            var reducedResponse1 = (ClaimSetResponse)claimSetFromDb1;
            reducedResponse1.Name.Should().Be("Original-ClaimSet");

            var getByIdResult2 = await _repository.GetClaimSet(_idCopy);
            getByIdResult2.Should().BeOfType<ClaimSetGetResult.Success>();

            object claimSetFromDb2 = ((ClaimSetGetResult.Success)getByIdResult2).ClaimSetResponse;
            claimSetFromDb2.Should().BeOfType<ClaimSetResponse>();

            var reducedResponse2 = (ClaimSetResponse)claimSetFromDb2;
            reducedResponse2.Name.Should().Be("Copy-Test-ClaimSet");
        }

        [Test]
        public async Task Should_clone_configured_hierarchy_assignments_when_copying_claim_set()
        {
            await ClaimsHierarchyTestHelper.ReinitializeClaimsHierarchy();

            var importResult = await _repository.Import(
                new ClaimSetImportCommand
                {
                    Name = "Original-With-Permissions",
                    ResourceClaims =
                    [
                        new ResourceClaim
                        {
                            ClaimName = "http://ed-fi.org/identity/claims/ed-fi/school",
                            Actions = [new ResourceClaimAction { Name = "Read", Enabled = true }],
                            AuthorizationStrategyOverrides =
                            [
                                new ClaimSetResourceClaimActionAuthStrategies
                                {
                                    ActionName = "Read",
                                    AuthorizationStrategies =
                                    [
                                        new EdFi.DmsConfigurationService.DataModel.Model.ClaimSets.AuthorizationStrategy
                                        {
                                            AuthorizationStrategyName = "NoFurtherAuthorizationRequired",
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                }
            );
            if (importResult is ClaimSetImportResult.FailureUnknown failureUnknown)
            {
                Assert.Fail(failureUnknown.FailureMessage);
            }
            importResult.Should().BeOfType<ClaimSetImportResult.Success>();
            var originalId = ((ClaimSetImportResult.Success)importResult).Id;

            var copyResult = await _repository.Copy(
                new ClaimSetCopyCommand { OriginalId = originalId, Name = "Copied-With-Permissions" }
            );
            var copiedId = ((ClaimSetCopyResult.Success)copyResult).Id;

            var copied = (ClaimSetGetResult.Success)await _repository.GetClaimSet(copiedId);
            var resourceClaim = copied
                .ClaimSetResponse.ResourceClaims.Should()
                .ContainSingle(rc => rc.ClaimName == "http://ed-fi.org/identity/claims/ed-fi/school")
                .Which;
            resourceClaim.Actions.Should().ContainSingle(a => a.Name == "Read");
            resourceClaim.AuthorizationStrategyOverrides.Should().ContainSingle(o => o.ActionName == "Read");
        }

        [Test]
        public async Task Should_copy_system_reserved_claim_set_as_non_system_reserved()
        {
            var insertReservedResult = await _repository.InsertClaimSet(
                new ClaimSetInsertCommand { Name = "Original-System-Reserved", IsSystemReserved = true }
            );
            var originalReservedId = ((ClaimSetInsertResult.Success)insertReservedResult).Id;

            var copyResult = await _repository.Copy(
                new ClaimSetCopyCommand
                {
                    OriginalId = originalReservedId,
                    Name = "Copied-From-System-Reserved",
                }
            );
            copyResult.Should().BeOfType<ClaimSetCopyResult.Success>();
            var copiedId = ((ClaimSetCopyResult.Success)copyResult).Id;

            var copied = await _repository.GetClaimSet(copiedId);
            copied.Should().BeOfType<ClaimSetGetResult.Success>();
            ((ClaimSetGetResult.Success)copied).ClaimSetResponse.IsSystemReserved.Should().BeFalse();

            var updateResult = await _repository.UpdateClaimSet(
                new ClaimSetUpdateCommand { Id = copiedId, Name = "Copied-System-Reserved-Updated" }
            );
            updateResult.Should().BeOfType<ClaimSetUpdateResult.Success>();
        }
    }

    [TestFixture]
    public class QueryPagingTests : ClaimSetTests
    {
        [SetUp]
        public async Task Setup()
        {
            await EnsureClaimsDataLoaded();

            for (int i = 1; i <= 12; i++)
            {
                ClaimSetInsertCommand claimSetCommand = new() { Name = $"ClaimSet-{i:D2}" };
                var insertResult = await _repository.InsertClaimSet(claimSetCommand);
                insertResult
                    .Should()
                    .BeOfType<ClaimSetInsertResult.Success>(
                        $"claim set {i} (ClaimSet-{i:D2}) should insert successfully"
                    );
            }
        }

        [Test]
        public async Task Should_return_all_results_when_no_paging_params_provided()
        {
            var result = await _repository.QueryClaimSet(new ClaimSetQuery());
            result.Should().BeOfType<ClaimSetQueryResult.Success>();
            ((ClaimSetQueryResult.Success)result)
                .ClaimSetResponses.Should()
                .HaveCountGreaterThanOrEqualTo(12);
        }

        [Test]
        public async Task Should_apply_limit_when_limit_is_provided()
        {
            var result = await _repository.QueryClaimSet(new ClaimSetQuery { Limit = 5 });
            result.Should().BeOfType<ClaimSetQueryResult.Success>();
            ((ClaimSetQueryResult.Success)result).ClaimSetResponses.Should().HaveCount(5);
        }

        [Test]
        public async Task Should_apply_offset_when_offset_is_provided()
        {
            var result = await _repository.QueryClaimSet(new ClaimSetQuery { Offset = 10 });
            result.Should().BeOfType<ClaimSetQueryResult.Success>();
            ((ClaimSetQueryResult.Success)result).ClaimSetResponses.Should().HaveCountGreaterThanOrEqualTo(2);
        }
    }

    [TestFixture]
    public class QuerySortTests : ClaimSetTests
    {
        [SetUp]
        public async Task Setup()
        {
            await EnsureClaimsDataLoaded();

            foreach (var name in new[] { "Zebra-ClaimSet", "Apple-ClaimSet", "Mango-ClaimSet" })
            {
                ClaimSetInsertCommand claimSetCommand = new() { Name = name };
                var insertResult = await _repository.InsertClaimSet(claimSetCommand);
                insertResult
                    .Should()
                    .BeOfType<ClaimSetInsertResult.Success>($"claim set '{name}' should insert successfully");
            }
        }

        [Test]
        public async Task Should_return_ascending_order_by_name()
        {
            var result = await _repository.QueryClaimSet(
                new ClaimSetQuery { OrderBy = "name", Direction = "ASC" }
            );
            result.Should().BeOfType<ClaimSetQueryResult.Success>();
            var names = ((ClaimSetQueryResult.Success)result).ClaimSetResponses.Select(c => c.Name).ToList();
            var testNames = names.Where(n => n.Contains("-ClaimSet")).ToList();
            testNames.Should().HaveCount(3);
            testNames.Should().ContainInOrder("Apple-ClaimSet", "Mango-ClaimSet", "Zebra-ClaimSet");
        }

        [Test]
        public async Task Should_return_descending_order_by_name()
        {
            var result = await _repository.QueryClaimSet(
                new ClaimSetQuery { OrderBy = "name", Direction = "DESC" }
            );
            result.Should().BeOfType<ClaimSetQueryResult.Success>();
            var names = ((ClaimSetQueryResult.Success)result).ClaimSetResponses.Select(c => c.Name).ToList();
            var testNames = names.Where(n => n.Contains("-ClaimSet")).ToList();
            testNames.Should().HaveCount(3);
            testNames.Should().ContainInOrder("Zebra-ClaimSet", "Mango-ClaimSet", "Apple-ClaimSet");
        }

        [Test]
        public async Task Should_default_to_ascending_order_when_direction_is_omitted()
        {
            var result = await _repository.QueryClaimSet(new ClaimSetQuery { OrderBy = "name" });
            result.Should().BeOfType<ClaimSetQueryResult.Success>();
            var names = ((ClaimSetQueryResult.Success)result).ClaimSetResponses.Select(c => c.Name).ToList();
            var testNames = names.Where(n => n.Contains("-ClaimSet")).ToList();
            testNames.Should().HaveCount(3);
            testNames.Should().ContainInOrder("Apple-ClaimSet", "Mango-ClaimSet", "Zebra-ClaimSet");
        }
    }
}
