// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

/// <summary>
/// Live GET-many queries page through the typed paging choice, so a backend can never receive a
/// paging mode the request did not select.
/// </summary>
[TestFixture]
[Parallelizable]
public class Given_The_Relational_Query_Request_Contract
{
    private PropertyInfo[] _properties = null!;

    [SetUp]
    public void Setup()
    {
        _properties = typeof(IQueryRequest).GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy
        );
    }

    [Test]
    public void It_exposes_the_typed_collection_paging_choice()
    {
        _properties
            .Should()
            .Contain(property =>
                property.Name == nameof(IQueryRequest.Paging)
                && property.PropertyType == typeof(CollectionPaging)
            );
    }

    [Test]
    public void It_no_longer_exposes_traditional_pagination_parameters_directly()
    {
        _properties.Should().NotContain(property => property.PropertyType == typeof(PaginationParameters));
    }

    /// <summary>
    /// The anchor travels on the request rather than being derived on either side of it, so page
    /// selection and the continuation token cannot disagree about which column the page walks.
    /// </summary>
    [Test]
    public void It_exposes_the_resolved_page_ordering_mode()
    {
        _properties
            .Should()
            .Contain(property =>
                property.Name == nameof(IQueryRequest.PageOrderingMode)
                && property.PropertyType == typeof(PageOrderingMode)
            );
    }
}
