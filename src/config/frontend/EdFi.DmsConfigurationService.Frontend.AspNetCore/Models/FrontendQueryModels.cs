// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using EdFi.DmsConfigurationService.DataModel.Model;
using Microsoft.AspNetCore.Mvc;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Models;

/// <summary>
/// Frontend-specific paging DTO with ASP.NET Core binding metadata.
/// Repository query models stay in the data model layer; endpoint DTOs map to them at the module boundary.
/// </summary>
public class FrontendPagingQuery : PagingQuery
{
    [FromQuery(Name = "offset")]
    [Range(0, int.MaxValue)]
    [Description("Indicates how many items should be skipped before returning results.")]
    public new int? Offset
    {
        get => base.Offset;
        set => base.Offset = value;
    }

    [FromQuery(Name = "limit")]
    [Range(1, int.MaxValue)]
    [Description("Indicates the maximum number of items that should be returned in the results.")]
    public new int? Limit
    {
        get => base.Limit;
        set => base.Limit = value;
    }

    [FromQuery(Name = "orderBy")]
    [Description("Name of the field to sort by.")]
    public new string? OrderBy
    {
        get => base.OrderBy;
        set => base.OrderBy = value;
    }

    [FromQuery(Name = "direction")]
    [Description("Sort direction: ASC or DESC, ascending or descending.")]
    public new string? Direction
    {
        get => base.Direction;
        set => base.Direction = value;
    }

    protected TQuery ApplyPagingTo<TQuery>(TQuery query)
        where TQuery : PagingQuery
    {
        query.Offset = Offset;
        query.Limit = Limit;
        query.OrderBy = OrderBy;
        query.Direction = Direction;
        return query;
    }
}

public class FrontendVendorQuery : FrontendPagingQuery
{
    [FromQuery(Name = "id")]
    public long? Id { get; set; }

    [FromQuery(Name = "company")]
    public string? Company { get; set; }

    [FromQuery(Name = "namespacePrefixes")]
    public string? NamespacePrefixes { get; set; }

    [FromQuery(Name = "contactName")]
    public string? ContactName { get; set; }

    [FromQuery(Name = "contactEmailAddress")]
    public string? ContactEmailAddress { get; set; }

    public VendorQuery ToQuery() =>
        ApplyPagingTo(
            new VendorQuery
            {
                Id = Id,
                Company = Company,
                NamespacePrefixes = NamespacePrefixes,
                ContactName = ContactName,
                ContactEmailAddress = ContactEmailAddress,
            }
        );
}

public class FrontendApplicationQuery : FrontendPagingQuery
{
    [FromQuery(Name = "id")]
    public long? Id { get; set; }

    [FromQuery(Name = "applicationName")]
    public string? ApplicationName { get; set; }

    [FromQuery(Name = "claimSetName")]
    public string? ClaimSetName { get; set; }

    [FromQuery(Name = "ids")]
    public string? Ids { get; set; }

    public ApplicationQuery ToQuery() =>
        ApplyPagingTo(
            new ApplicationQuery
            {
                Id = Id,
                ApplicationName = ApplicationName,
                ClaimSetName = ClaimSetName,
                Ids = Ids,
            }
        );
}

public class FrontendApiClientQuery : FrontendPagingQuery
{
    [FromQuery(Name = "applicationid")]
    public long? ApplicationId { get; set; }

    public ApiClientQuery ToQuery() => ApplyPagingTo(new ApiClientQuery { ApplicationId = ApplicationId });
}

public class FrontendDataStoreQuery : FrontendPagingQuery
{
    [FromQuery(Name = "id")]
    public long? Id { get; set; }

    [FromQuery(Name = "name")]
    public string? Name { get; set; }

    [FromQuery(Name = "dataStoreType")]
    public string? DataStoreType { get; set; }

    public DataStoreQuery ToQuery() =>
        ApplyPagingTo(
            new DataStoreQuery
            {
                Id = Id,
                Name = Name,
                DataStoreType = DataStoreType,
            }
        );
}

public class FrontendClaimSetQuery : FrontendPagingQuery
{
    [FromQuery(Name = "id")]
    public long? Id { get; set; }

    [FromQuery(Name = "name")]
    public string? Name { get; set; }

    public ClaimSetQuery ToQuery() => ApplyPagingTo(new ClaimSetQuery { Id = Id, Name = Name });
}

public class FrontendProfileQuery : FrontendPagingQuery
{
    [FromQuery(Name = "id")]
    [Description("Filter profiles by identifier.")]
    public long? Id { get; set; }

    [FromQuery(Name = "name")]
    [Description("Filter profiles by name.")]
    public string? Name { get; set; }

    public ProfileQuery ToQuery() => ApplyPagingTo(new ProfileQuery { Id = Id, Name = Name });
}

public class FrontendResourceClaimQuery : FrontendPagingQuery
{
    [FromQuery(Name = "id")]
    [Description("Filter resource claims by identifier.")]
    public long? Id { get; set; }

    [FromQuery(Name = "name")]
    [Description("Filter resource claims by name.")]
    public string? Name { get; set; }

    public ResourceClaimQuery ToQuery() => ApplyPagingTo(new ResourceClaimQuery { Id = Id, Name = Name });
}

public class FrontendResourceClaimActionQuery : FrontendPagingQuery
{
    [FromQuery(Name = "resourceName")]
    [Description("Filter resource claim actions by resource name.")]
    public string? ResourceName { get; set; }

    public ResourceClaimActionQuery ToQuery() =>
        ApplyPagingTo(new ResourceClaimActionQuery { ResourceName = ResourceName });
}

public class FrontendResourceClaimActionAuthStrategyQuery : FrontendPagingQuery
{
    [FromQuery(Name = "resourceName")]
    [Description("Filter resource claim action auth strategies by resource name.")]
    public string? ResourceName { get; set; }

    public ResourceClaimActionAuthStrategyQuery ToQuery() =>
        ApplyPagingTo(new ResourceClaimActionAuthStrategyQuery { ResourceName = ResourceName });
}
