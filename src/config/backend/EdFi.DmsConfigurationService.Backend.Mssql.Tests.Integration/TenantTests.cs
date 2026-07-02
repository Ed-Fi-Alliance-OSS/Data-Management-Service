// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Mssql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class TenantTests : DatabaseTest
{
    private readonly ITenantRepository _repository = new TenantRepository(
        MssqlTestConfiguration.DatabaseOptions,
        NullLogger<TenantRepository>.Instance,
        new TestAuditContext()
    );

    private async Task ResetTenants(params string[] names)
    {
        foreach (var name in names)
        {
            await Connection!.ExecuteAsync(
                @"DELETE FROM dmscs.Tenant WHERE Name = @Name;",
                new { Name = name }
            );
        }
    }

    private static readonly string[] EarlySortedTenantNames =
    [
        "000-DMS1074-Alpha",
        "000-DMS1074-Bravo",
        "000-DMS1074-Charlie",
    ];

    private static readonly string[] LateSortedTenantNames =
    [
        "~~~DMS1074-Charlie",
        "~~~DMS1074-Alpha",
        "~~~DMS1074-Bravo",
    ];

    [TestFixture]
    public class QueryPagingTests : TenantTests
    {
        [SetUp]
        public async Task Setup()
        {
            await ResetTenants(EarlySortedTenantNames);
            foreach (var name in EarlySortedTenantNames)
            {
                var result = await _repository.InsertTenant(new TenantInsertCommand { Name = name });
                result.Should().BeOfType<TenantInsertResult.Success>();
            }
        }

        [Test]
        public async Task Should_return_all_results_when_no_paging_params_provided()
        {
            var result = await _repository.QueryTenant(
                new PagingQuery { OrderBy = "name", Direction = "ASC" }
            );
            result.Should().BeOfType<TenantQueryResult.Success>();
            ((TenantQueryResult.Success)result)
                .TenantResponses.Select(t => t.Name)
                .Where(name => name.StartsWith("000-DMS1074-"))
                .Should()
                .ContainInOrder(EarlySortedTenantNames);
        }

        [Test]
        public async Task Should_apply_limit_when_limit_is_provided()
        {
            var result = await _repository.QueryTenant(
                new PagingQuery
                {
                    OrderBy = "name",
                    Direction = "ASC",
                    Limit = 2,
                }
            );
            result.Should().BeOfType<TenantQueryResult.Success>();
            ((TenantQueryResult.Success)result)
                .TenantResponses.Select(t => t.Name)
                .Where(name => name.StartsWith("000-DMS1074-"))
                .Should()
                .ContainInOrder(EarlySortedTenantNames[..2]);
        }

        [Test]
        public async Task Should_apply_offset_when_offset_is_provided()
        {
            var result = await _repository.QueryTenant(
                new PagingQuery
                {
                    OrderBy = "name",
                    Direction = "ASC",
                    Limit = 2,
                    Offset = 1,
                }
            );
            result.Should().BeOfType<TenantQueryResult.Success>();
            ((TenantQueryResult.Success)result)
                .TenantResponses.Select(t => t.Name)
                .Where(name => name.StartsWith("000-DMS1074-"))
                .Should()
                .ContainInOrder(EarlySortedTenantNames[1..]);
        }
    }

    [TestFixture]
    public class QuerySortTests : TenantTests
    {
        [SetUp]
        public async Task Setup()
        {
            await ResetTenants(LateSortedTenantNames);
            foreach (var name in LateSortedTenantNames)
            {
                var result = await _repository.InsertTenant(new TenantInsertCommand { Name = name });
                result.Should().BeOfType<TenantInsertResult.Success>();
            }
        }

        [Test]
        public async Task Should_return_ascending_order_by_name()
        {
            var result = await _repository.QueryTenant(
                new PagingQuery { OrderBy = "name", Direction = "ASC" }
            );
            result.Should().BeOfType<TenantQueryResult.Success>();
            var names = ((TenantQueryResult.Success)result)
                .TenantResponses.Select(t => t.Name)
                .Where(name => name.StartsWith("~~~DMS1074-"))
                .ToList();
            names.Should().ContainInOrder(LateSortedTenantNames.OrderBy(name => name).ToArray());
        }

        [Test]
        public async Task Should_return_descending_order_by_name()
        {
            var result = await _repository.QueryTenant(
                new PagingQuery { OrderBy = "name", Direction = "DESC" }
            );
            result.Should().BeOfType<TenantQueryResult.Success>();
            var names = ((TenantQueryResult.Success)result)
                .TenantResponses.Select(t => t.Name)
                .Where(name => name.StartsWith("~~~DMS1074-"))
                .ToList();
            names.Should().ContainInOrder(LateSortedTenantNames.OrderByDescending(name => name).ToArray());
        }
    }
}
