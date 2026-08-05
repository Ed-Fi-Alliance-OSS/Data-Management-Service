// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.Backend;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Backend;

/// <summary>
/// Change Query endpoints page traditionally only. These assertions keep cursor paging off the
/// tracked-change request contracts, so /deletes and /keyChanges neither acquire cursor behavior nor
/// reserve cursor parameter names.
/// </summary>
[TestFixture]
[Parallelizable]
public class TrackedChangeQueryRequestPagingContractTests
{
    private static readonly Type[] _trackedChangeRequestContracts =
    [
        typeof(ITrackedChangeQueryRequest),
        typeof(TrackedChangeQueryRequest),
        typeof(RelationalTrackedChangeQueryRequest),
    ];

    private static readonly Type[] _cursorPagingContracts =
    [
        typeof(CollectionPaging),
        typeof(CursorRange),
        typeof(PageSize),
    ];

    private static IEnumerable<PropertyInfo> PropertiesOf(Type contract) =>
        contract.GetProperties(
            BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.FlattenHierarchy
        );

    [TestCaseSource(nameof(_trackedChangeRequestContracts))]
    public void It_exposes_traditional_pagination_parameters(Type contract)
    {
        PropertiesOf(contract)
            .Should()
            .Contain(property =>
                property.Name == nameof(ITrackedChangeQueryRequest.PaginationParameters)
                && property.PropertyType == typeof(PaginationParameters)
            );
    }

    [TestCaseSource(nameof(_trackedChangeRequestContracts))]
    public void It_exposes_no_cursor_paging_contract(Type contract)
    {
        PropertiesOf(contract)
            .Should()
            .NotContain(property =>
                _cursorPagingContracts.Any(cursorContract =>
                    cursorContract.IsAssignableFrom(property.PropertyType)
                )
            );
    }
}
