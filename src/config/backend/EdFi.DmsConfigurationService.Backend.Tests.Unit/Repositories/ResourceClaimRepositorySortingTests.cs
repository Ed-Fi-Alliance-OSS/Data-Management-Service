// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ResourceClaims;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Repositories;

[TestFixture]
public class ResourceClaimRepositorySortingTests
{
    private List<ResourceClaimResponse> CreateTestClaims()
    {
        return
        [
            new ResourceClaimResponse
            {
                Id = 3,
                Name = "Charlie",
                ParentId = 0,
                ParentName = null,
                Children = [],
            },
            new ResourceClaimResponse
            {
                Id = 1,
                Name = "Alice",
                ParentId = 0,
                ParentName = null,
                Children = [],
            },
            new ResourceClaimResponse
            {
                Id = 2,
                Name = "Bob",
                ParentId = 0,
                ParentName = null,
                Children = [],
            },
        ];
    }

    [Test]
    public void SortAndPage_sorts_by_name_ascending_by_default()
    {
        var claims = CreateTestClaims();
        var query = new ResourceClaimQuery();

        var method = typeof(ResourceClaimRepository).GetMethod(
            "SortAndPage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(IEnumerable<ResourceClaimResponse>), typeof(ResourceClaimQuery)]
        );

        var result = (IEnumerable<ResourceClaimResponse>)method!.Invoke(null, [claims, query])!;

        result.Select(r => r.Name).Should().Equal("Alice", "Bob", "Charlie");
    }

    [Test]
    public void SortAndPage_sorts_by_id_ascending()
    {
        var claims = CreateTestClaims();
        var query = new ResourceClaimQuery { OrderBy = "id", Direction = "asc" };

        var method = typeof(ResourceClaimRepository).GetMethod(
            "SortAndPage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(IEnumerable<ResourceClaimResponse>), typeof(ResourceClaimQuery)]
        );

        var result = (IEnumerable<ResourceClaimResponse>)method!.Invoke(null, [claims, query])!;

        result.Select(r => r.Id).Should().Equal(1, 2, 3);
    }

    [Test]
    public void SortAndPage_sorts_by_id_descending()
    {
        var claims = CreateTestClaims();
        var query = new ResourceClaimQuery { OrderBy = "id", Direction = "desc" };

        var method = typeof(ResourceClaimRepository).GetMethod(
            "SortAndPage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(IEnumerable<ResourceClaimResponse>), typeof(ResourceClaimQuery)]
        );

        var result = (IEnumerable<ResourceClaimResponse>)method!.Invoke(null, [claims, query])!;

        result.Select(r => r.Id).Should().Equal(3, 2, 1);
    }

    [Test]
    public void SortAndPage_sorts_by_name_case_insensitive()
    {
        var claims = new List<ResourceClaimResponse>
        {
            new()
            {
                Id = 1,
                Name = "zebra",
                ParentId = 0,
                ParentName = null,
                Children = [],
            },
            new()
            {
                Id = 2,
                Name = "APPLE",
                ParentId = 0,
                ParentName = null,
                Children = [],
            },
            new()
            {
                Id = 3,
                Name = "banana",
                ParentId = 0,
                ParentName = null,
                Children = [],
            },
        };
        var query = new ResourceClaimQuery { OrderBy = "name" };

        var method = typeof(ResourceClaimRepository).GetMethod(
            "SortAndPage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(IEnumerable<ResourceClaimResponse>), typeof(ResourceClaimQuery)]
        );

        var result = (IEnumerable<ResourceClaimResponse>)method!.Invoke(null, [claims, query])!;

        result.Select(r => r.Name).Should().Equal("APPLE", "banana", "zebra");
    }

    [Test]
    public void SortAndPage_applies_offset()
    {
        var claims = CreateTestClaims();
        var query = new ResourceClaimQuery { OrderBy = "name", Offset = 1 };

        var method = typeof(ResourceClaimRepository).GetMethod(
            "SortAndPage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(IEnumerable<ResourceClaimResponse>), typeof(ResourceClaimQuery)]
        );

        var result = (IEnumerable<ResourceClaimResponse>)method!.Invoke(null, [claims, query])!;

        result.Select(r => r.Name).Should().Equal("Bob", "Charlie");
    }

    [Test]
    public void SortAndPage_applies_limit()
    {
        var claims = CreateTestClaims();
        var query = new ResourceClaimQuery { OrderBy = "name", Limit = 2 };

        var method = typeof(ResourceClaimRepository).GetMethod(
            "SortAndPage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(IEnumerable<ResourceClaimResponse>), typeof(ResourceClaimQuery)]
        );

        var result = (IEnumerable<ResourceClaimResponse>)method!.Invoke(null, [claims, query])!;

        result.Select(r => r.Name).Should().Equal("Alice", "Bob");
    }

    [Test]
    public void SortAndPage_applies_offset_and_limit()
    {
        var claims = CreateTestClaims();
        var query = new ResourceClaimQuery
        {
            OrderBy = "name",
            Offset = 1,
            Limit = 1,
        };

        var method = typeof(ResourceClaimRepository).GetMethod(
            "SortAndPage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(IEnumerable<ResourceClaimResponse>), typeof(ResourceClaimQuery)]
        );

        var result = (IEnumerable<ResourceClaimResponse>)method!.Invoke(null, [claims, query])!;

        result.Should().ContainSingle().Which.Name.Should().Be("Bob");
    }

    [Test]
    public void SortAndPage_sorts_by_parentName()
    {
        var claims = new List<ResourceClaimResponse>
        {
            new()
            {
                Id = 1,
                Name = "Child1",
                ParentId = 10,
                ParentName = "Zebra",
                Children = [],
            },
            new()
            {
                Id = 2,
                Name = "Child2",
                ParentId = 20,
                ParentName = "Apple",
                Children = [],
            },
            new()
            {
                Id = 3,
                Name = "Child3",
                ParentId = 30,
                ParentName = "Banana",
                Children = [],
            },
        };
        var query = new ResourceClaimQuery { OrderBy = "parentName" };

        var method = typeof(ResourceClaimRepository).GetMethod(
            "SortAndPage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(IEnumerable<ResourceClaimResponse>), typeof(ResourceClaimQuery)]
        );

        var result = (IEnumerable<ResourceClaimResponse>)method!.Invoke(null, [claims, query])!;

        result.Select(r => r.ParentName).Should().Equal("Apple", "Banana", "Zebra");
    }

    [Test]
    public void SortAndPage_sorts_by_parentId()
    {
        var claims = new List<ResourceClaimResponse>
        {
            new()
            {
                Id = 1,
                Name = "Child1",
                ParentId = 30,
                ParentName = "C",
                Children = [],
            },
            new()
            {
                Id = 2,
                Name = "Child2",
                ParentId = 10,
                ParentName = "A",
                Children = [],
            },
            new()
            {
                Id = 3,
                Name = "Child3",
                ParentId = 20,
                ParentName = "B",
                Children = [],
            },
        };
        var query = new ResourceClaimQuery { OrderBy = "parentId" };

        var method = typeof(ResourceClaimRepository).GetMethod(
            "SortAndPage",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
            [typeof(IEnumerable<ResourceClaimResponse>), typeof(ResourceClaimQuery)]
        );

        var result = (IEnumerable<ResourceClaimResponse>)method!.Invoke(null, [claims, query])!;

        result.Select(r => r.ParentId).Should().Equal(10, 20, 30);
    }
}
