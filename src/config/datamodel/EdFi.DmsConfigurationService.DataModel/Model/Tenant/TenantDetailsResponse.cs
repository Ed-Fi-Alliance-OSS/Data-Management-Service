// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.DataModel.Model.Tenant;

/// <summary>
/// A tenant with its data stores and their education organizations. <see cref="Id"/> and <see cref="Name"/> are both
/// the tenant's canonical name.
/// </summary>
public sealed record TenantDetailsResponse(
    string Id,
    string Name,
    IReadOnlyList<TenantDataStoreResponse> DataStores
);

/// <summary>
/// An ordinary data store with its management record, if any, and the education organizations of its published
/// snapshot; or a management record linked to no data store, which has no <see cref="Id"/>, no
/// <see cref="DataStoreType"/> and no education organizations. Every property is written, null included.
/// </summary>
public sealed record TenantDataStoreResponse(
    int? Id,
    int? DataStoreManageId,
    string Name,
    string? DataStoreType,
    string Status,
    string? DatabaseTemplate,
    string? DatabaseName,
    IReadOnlyList<EducationOrganizationResponse> EducationOrganizations
);

/// <summary>An education organization of a data store's published snapshot.</summary>
public sealed record EducationOrganizationResponse(
    long EducationOrganizationId,
    string NameOfInstitution,
    string? ShortNameOfInstitution,
    string Discriminator,
    long? ParentId
);
