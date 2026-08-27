// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.OwnershipToken;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class OwnershipTokenRepositoryTests : DatabaseTest
{
    private readonly IOwnershipTokenRepository _repository = CreateRepository(new TenantContextProvider());

    private static IOwnershipTokenRepository CreateRepository(TenantContextProvider tenantContextProvider) =>
        new OwnershipTokenRepository(
            MssqlTestConfiguration.DatabaseOptions,
            NullLogger<OwnershipTokenRepository>.Instance,
            new TestAuditContext(),
            tenantContextProvider
        );

    [TestFixture]
    public class Given_insert_ownership_token : OwnershipTokenRepositoryTests
    {
        [Test]
        public async Task It_inserts_and_gets_a_single_tenant_token()
        {
            var insert = await _repository.InsertOwnershipToken(
                new OwnershipTokenInsertCommand { Description = "District" }
            );
            insert.Should().BeOfType<OwnershipTokenInsertResult.Success>();
            int id = ((OwnershipTokenInsertResult.Success)insert).Id;

            var get = await _repository.GetOwnershipToken(id);

            get.Should().BeOfType<OwnershipTokenGetResult.Success>();
            var ownershipToken = ((OwnershipTokenGetResult.Success)get).OwnershipToken;
            ownershipToken.Id.Should().Be(id);
            ownershipToken.Description.Should().Be("District");
        }
    }

    [TestFixture]
    public class Given_query_and_update_ownership_tokens : OwnershipTokenRepositoryTests
    {
        private int _bravoId;

        [SetUp]
        public async Task Setup()
        {
            await InsertToken("Alpha");
            _bravoId = await InsertToken("Bravo");

            var update = await _repository.UpdateOwnershipToken(
                new OwnershipTokenUpdateCommand { Id = _bravoId, Description = "Charlie" }
            );
            update.Should().BeOfType<OwnershipTokenUpdateResult.Success>();
        }

        [Test]
        public async Task It_returns_updated_tokens_with_paging_and_ordering()
        {
            var query = await _repository.QueryOwnershipTokens(
                new OwnershipTokenQuery
                {
                    OrderBy = "description",
                    Direction = "DESC",
                    Limit = 1,
                }
            );

            query.Should().BeOfType<OwnershipTokenQueryResult.Success>();
            var ownershipTokens = ((OwnershipTokenQueryResult.Success)query).OwnershipTokens;
            ownershipTokens.Should().HaveCount(1);
            ownershipTokens[0].Id.Should().Be(_bravoId);
            ownershipTokens[0].Description.Should().Be("Charlie");
        }

        [Test]
        public async Task It_returns_not_found_when_updating_a_missing_token()
        {
            var result = await _repository.UpdateOwnershipToken(
                new OwnershipTokenUpdateCommand { Id = 32767, Description = "Missing" }
            );

            result.Should().BeOfType<OwnershipTokenUpdateResult.FailureNotFound>();
        }

        [Test]
        public async Task It_returns_not_found_when_getting_a_missing_token()
        {
            var result = await _repository.GetOwnershipToken(32767);

            result.Should().BeOfType<OwnershipTokenGetResult.FailureNotFound>();
        }

        private async Task<int> InsertToken(string description)
        {
            var insert = await _repository.InsertOwnershipToken(
                new OwnershipTokenInsertCommand { Description = description }
            );
            insert.Should().BeOfType<OwnershipTokenInsertResult.Success>();
            return ((OwnershipTokenInsertResult.Success)insert).Id;
        }
    }

    [TestFixture]
    public class Given_multitenant_ownership_tokens : OwnershipTokenRepositoryTests
    {
        private static async Task<TenantContextProvider> CreateTenantProvider(string suffix)
        {
            var tenantRepository = new TenantRepository(
                MssqlTestConfiguration.DatabaseOptions,
                NullLogger<TenantRepository>.Instance,
                new TestAuditContext()
            );
            var tenantName = $"OwnershipTokenTenant{suffix}-{Guid.NewGuid()}";
            var tenantResult = await tenantRepository.InsertTenant(
                new TenantInsertCommand { Name = tenantName }
            );
            tenantResult.Should().BeOfType<TenantInsertResult.Success>();

            return new TenantContextProvider
            {
                Context = new TenantContext.Multitenant(
                    ((TenantInsertResult.Success)tenantResult).Id,
                    tenantName
                ),
            };
        }

        [Test]
        public async Task It_does_not_expose_another_tenants_token()
        {
            TenantContextProvider tenantA = await CreateTenantProvider("A");
            TenantContextProvider tenantB = await CreateTenantProvider("B");
            var tenantARepository = CreateRepository(tenantA);
            var tenantBRepository = CreateRepository(tenantB);

            var insert = await tenantARepository.InsertOwnershipToken(
                new OwnershipTokenInsertCommand { Description = "Tenant A Token" }
            );
            insert.Should().BeOfType<OwnershipTokenInsertResult.Success>();
            int id = ((OwnershipTokenInsertResult.Success)insert).Id;

            var getFromB = await tenantBRepository.GetOwnershipToken(id);
            var queryFromB = await tenantBRepository.QueryOwnershipTokens(new OwnershipTokenQuery());
            var updateFromB = await tenantBRepository.UpdateOwnershipToken(
                new OwnershipTokenUpdateCommand { Id = id, Description = "Tenant B Update" }
            );

            getFromB.Should().BeOfType<OwnershipTokenGetResult.FailureNotFound>();
            queryFromB.Should().BeOfType<OwnershipTokenQueryResult.Success>();
            ((OwnershipTokenQueryResult.Success)queryFromB).OwnershipTokens.Should().BeEmpty();
            updateFromB.Should().BeOfType<OwnershipTokenUpdateResult.FailureNotFound>();
        }
    }
}
