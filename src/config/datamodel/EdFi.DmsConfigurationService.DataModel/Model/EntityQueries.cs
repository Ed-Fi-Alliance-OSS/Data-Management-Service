// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model;

public class VendorQuery : PagingQuery
{
    public long? Id { get; set; }

    public string? Company { get; set; }

    public string? NamespacePrefixes { get; set; }

    public string? ContactName { get; set; }

    public string? ContactEmailAddress { get; set; }
}

public class ApplicationQuery : PagingQuery
{
    public long? Id { get; set; }

    public string? ApplicationName { get; set; }

    public string? ClaimSetName { get; set; }

    /// <summary>
    /// Comma-separated list of application IDs. Validated as integers by ApplicationPagingQueryValidator before use.
    /// </summary>
    public string? Ids { get; set; }
}

public class ApiClientQuery : PagingQuery
{
    /// <summary>
    /// Binds from query string key 'applicationid' (case-insensitive).
    /// </summary>
    public long? ApplicationId { get; set; }
}

public class DmsInstanceQuery : PagingQuery
{
    public long? Id { get; set; }

    public string? InstanceName { get; set; }

    public string? InstanceType { get; set; }
}

public class ClaimSetQuery : PagingQuery
{
    public long? Id { get; set; }

    public string? Name { get; set; }
}

public class ProfileQuery : PagingQuery
{
    public long? Id { get; set; }

    public string? Name { get; set; }
}

public class ResourceClaimQuery : PagingQuery
{
    public long? Id { get; set; }

    public string? Name { get; set; }
}

public class ResourceClaimActionQuery : PagingQuery
{
    public string? ResourceName { get; set; }
}

public class ResourceClaimActionAuthStrategyQuery : PagingQuery
{
    public string? ResourceName { get; set; }
}
