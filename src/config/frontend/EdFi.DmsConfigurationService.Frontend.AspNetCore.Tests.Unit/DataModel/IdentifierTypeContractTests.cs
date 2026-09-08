// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using EdFi.DmsConfigurationService.DataModel.Model.Application;
using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using EdFi.DmsConfigurationService.DataModel.Model.DataStore;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreContext;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreDerivative;
using EdFi.DmsConfigurationService.DataModel.Model.Profile;
using EdFi.DmsConfigurationService.DataModel.Model.ResourceClaims;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using EdFi.DmsConfigurationService.DataModel.Model.Vendor;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;
using FluentAssertions;
using NUnit.Framework;
using ActionModel = EdFi.DmsConfigurationService.DataModel.Model.Action.Action;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.DataModel;

/// <summary>
/// Pins the declared CLR type of every resource identifier property in the request, response and
/// query models. Most of these types are invisible to the other contract tests: only
/// ApplicationResponse, ApiClientResponse, ApiClientCredentialsResponse and UploadClaimsResponse are
/// emitted through a .Produces&lt;T&gt;() declaration, so VendorResponse, ClaimSetResponse,
/// ProfileResponse, ProfileListResponse, ResourceClaimResponse, DataStoreResponse and Action never
/// reach the OpenAPI document, and the database shape tests cannot see C# types at all.
/// The matrix is explicit rather than a convention scan: a scan would need heuristics for what counts
/// as an identifier and would false-positive on ClientId, ProfileDefinition and similar.
/// </summary>
[TestFixture]
public class Given_the_CMS_resource_identifier_type_contract
{
    /// <summary>
    /// Declaring type, property name, and the exact type the property must declare. Commands expose
    /// collections as T[] and responses as List&lt;T&gt;, and query filters are nullable, so the exact
    /// type is asserted rather than "is it 32-bit".
    /// The typeof references also make the matrix fail to compile - rather than silently cover
    /// nothing - if one of these types is renamed or moved.
    /// </summary>
    private static readonly (
        Type DeclaringType,
        string PropertyName,
        Type ExpectedType
    )[] IdentifierContract =
    [
        // Vendor
        (typeof(VendorResponse), "Id", typeof(int)),
        (typeof(VendorUpdateCommand), "Id", typeof(int)),
        // Application
        (typeof(ApplicationResponse), "Id", typeof(int)),
        (typeof(ApplicationResponse), "VendorId", typeof(int)),
        (typeof(ApplicationResponse), "DataStoreIds", typeof(List<int>)),
        (typeof(ApplicationResponse), "ProfileIds", typeof(List<int>)),
        (typeof(ApplicationInsertCommand), "VendorId", typeof(int)),
        (typeof(ApplicationInsertCommand), "DataStoreIds", typeof(int[])),
        (typeof(ApplicationInsertCommand), "ProfileIds", typeof(int[])),
        (typeof(ApplicationUpdateCommand), "Id", typeof(int)),
        (typeof(ApplicationUpdateCommand), "VendorId", typeof(int)),
        (typeof(ApplicationUpdateCommand), "DataStoreIds", typeof(int[])),
        (typeof(ApplicationUpdateCommand), "ProfileIds", typeof(int[])),
        (typeof(ApplicationCredentialsResponse), "Id", typeof(int)),
        (typeof(ApiClientCommand), "DataStoreIds", typeof(int[])),
        // ApiClient
        (typeof(ApiClientResponse), "Id", typeof(int)),
        (typeof(ApiClientResponse), "ApplicationId", typeof(int)),
        (typeof(ApiClientResponse), "DataStoreIds", typeof(List<int>)),
        (typeof(ApiClientInsertCommand), "ApplicationId", typeof(int)),
        (typeof(ApiClientInsertCommand), "DataStoreIds", typeof(int[])),
        (typeof(ApiClientUpdateCommand), "Id", typeof(int)),
        (typeof(ApiClientUpdateCommand), "ApplicationId", typeof(int)),
        (typeof(ApiClientUpdateCommand), "DataStoreIds", typeof(int[])),
        (typeof(ApiClientCredentialsResponse), "Id", typeof(int)),
        (typeof(ApiClientCredentialsResponse), "ApplicationId", typeof(int)),
        // Claim sets, authorization strategies and actions
        (typeof(ClaimSetResponse), "Id", typeof(int)),
        (typeof(ClaimSetUpdateCommand), "Id", typeof(int)),
        (typeof(ClaimSetCopyCommand), "OriginalId", typeof(int)),
        (typeof(AuthorizationStrategy), "Id", typeof(int)),
        // Already int before DMS-1337; asserted so it cannot drift. ActionId belongs to the third
        // type declared in Model/ClaimSets/ResourceClaim.cs, not to ResourceClaim itself.
        (typeof(ClaimSetResourceClaimActionAuthStrategies), "ActionId", typeof(int?)),
        // Actions have no table and never reach the OpenAPI document, so this is their only coverage.
        (typeof(ActionModel), "Id", typeof(int)),
        // Profile
        (typeof(ProfileResponse), "Id", typeof(int)),
        (typeof(ProfileListResponse), "Id", typeof(int)),
        (typeof(ProfileUpdateCommand), "Id", typeof(int)),
        // Resource claims - five public types share Model/ResourceClaims/ResourceClaimResponse.cs
        (typeof(ResourceClaimResponse), "Id", typeof(int)),
        (typeof(ResourceClaimResponse), "ParentId", typeof(int)),
        (typeof(ResourceClaimActionResponse), "ResourceClaimId", typeof(int)),
        (typeof(ResourceClaimActionAuthStrategyResponse), "ResourceClaimId", typeof(int)),
        (typeof(ActionWithAuthorizationStrategyResponse), "ActionId", typeof(int)),
        (typeof(AuthorizationStrategyForActionResponse), "AuthStrategyId", typeof(int)),
        // Data stores - DataStoreContextItem and DataStoreDerivativeItem are positional records
        (typeof(DataStoreResponse), "Id", typeof(int)),
        (typeof(DataStoreUpdateCommand), "Id", typeof(int)),
        (typeof(DataStoreContextItem), "Id", typeof(int)),
        (typeof(DataStoreContextItem), "DataStoreId", typeof(int)),
        (typeof(DataStoreDerivativeItem), "Id", typeof(int)),
        (typeof(DataStoreDerivativeItem), "DataStoreId", typeof(int)),
        (typeof(DataStoreContextResponse), "Id", typeof(int)),
        (typeof(DataStoreContextResponse), "DataStoreId", typeof(int)),
        (typeof(DataStoreContextInsertCommand), "DataStoreId", typeof(int)),
        (typeof(DataStoreContextUpdateCommand), "Id", typeof(int)),
        (typeof(DataStoreContextUpdateCommand), "DataStoreId", typeof(int)),
        (typeof(DataStoreDerivativeResponse), "Id", typeof(int)),
        (typeof(DataStoreDerivativeResponse), "DataStoreId", typeof(int)),
        (typeof(DataStoreDerivativeInsertCommand), "DataStoreId", typeof(int)),
        (typeof(DataStoreDerivativeUpdateCommand), "Id", typeof(int)),
        (typeof(DataStoreDerivativeUpdateCommand), "DataStoreId", typeof(int)),
        // Repository query filters
        (typeof(VendorQuery), "Id", typeof(int?)),
        (typeof(ApplicationQuery), "Id", typeof(int?)),
        (typeof(ApiClientQuery), "ApplicationId", typeof(int?)),
        (typeof(DataStoreQuery), "Id", typeof(int?)),
        (typeof(ClaimSetQuery), "Id", typeof(int?)),
        (typeof(ProfileQuery), "Id", typeof(int?)),
        (typeof(ResourceClaimQuery), "Id", typeof(int?)),
        // Frontend query filters, which is what binds the query string
        (typeof(FrontendVendorQuery), "Id", typeof(int?)),
        (typeof(FrontendApplicationQuery), "Id", typeof(int?)),
        (typeof(FrontendApiClientQuery), "ApplicationId", typeof(int?)),
        (typeof(FrontendDataStoreQuery), "Id", typeof(int?)),
        (typeof(FrontendClaimSetQuery), "Id", typeof(int?)),
        (typeof(FrontendProfileQuery), "Id", typeof(int?)),
        (typeof(FrontendResourceClaimQuery), "Id", typeof(int?)),
    ];

    /// <summary>
    /// The identifiers that must stay 64-bit, pinned in the same style as the narrowed ones so a
    /// careless sweep of the data model fails loudly.
    /// Education organization ids are Ed-Fi ids rather than CMS resource ids, and the draft
    /// Management API v3 spec declares them int64. Tenants are out of scope: no numeric tenant
    /// identifier exists elsewhere in the Ed-Fi platform to align with, so Tenant.Id and the
    /// DataStore tenant reference stay 64-bit.
    /// </summary>
    private static readonly (
        Type DeclaringType,
        string PropertyName,
        Type ExpectedType
    )[] PreservedExceptions =
    [
        (typeof(ApplicationInsertCommand), "EducationOrganizationIds", typeof(long[])),
        (typeof(ApplicationUpdateCommand), "EducationOrganizationIds", typeof(long[])),
        (typeof(ApplicationResponse), "EducationOrganizationIds", typeof(List<long>)),
        (typeof(DataStoreResponse), "TenantId", typeof(long?)),
        (typeof(TenantResponse), "Id", typeof(long)),
    ];

    [Test]
    public void It_should_declare_identifier_properties_with_the_expected_clr_type()
    {
        AssertDeclaredTypes(IdentifierContract);
    }

    [Test]
    public void It_should_preserve_the_out_of_scope_64_bit_identifiers()
    {
        AssertDeclaredTypes(PreservedExceptions);
    }

    private static void AssertDeclaredTypes(
        (Type DeclaringType, string PropertyName, Type ExpectedType)[] contract
    )
    {
        // An emptied matrix would satisfy the mismatch assertion below while covering nothing.
        contract.Should().NotBeEmpty();

        List<string> mismatches = [];

        foreach ((Type declaringType, string propertyName, Type expectedType) in contract)
        {
            PropertyInfo? property = declaringType.GetProperty(propertyName);

            if (property is null)
            {
                mismatches.Add($"{declaringType.Name}.{propertyName} is not declared");
                continue;
            }

            if (property.PropertyType != expectedType)
            {
                mismatches.Add(
                    $"{declaringType.Name}.{propertyName} declares {Describe(property.PropertyType)}, expected {Describe(expectedType)}"
                );
            }
        }

        mismatches.Should().BeEmpty();
    }

    private static string Describe(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlyingType)
        {
            return $"{Describe(underlyingType)}?";
        }

        if (type.IsArray)
        {
            return $"{Describe(type.GetElementType()!)}[]";
        }

        if (type.IsGenericType)
        {
            string genericArguments = string.Join(", ", type.GetGenericArguments().Select(Describe));
            return $"{type.Name.Split('`')[0]}<{genericArguments}>";
        }

        return type.Name switch
        {
            nameof(Int32) => "int",
            nameof(Int64) => "long",
            _ => type.Name,
        };
    }
}
