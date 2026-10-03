// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.EducationOrganizationProjection;

/// <summary>
/// A projection page request after its query parameters have been accepted.
/// </summary>
/// <param name="DataStoreId">The CMS data store to read.</param>
/// <param name="Limit">The page size: the request's <c>limit</c>, or the deployment maximum.</param>
/// <param name="ContractVersion">The version the response conforms to.</param>
/// <param name="BindingHash">
/// The binding of this request's tenant, contract version and route qualifiers, which every cursor
/// issued for it carries.
/// </param>
/// <param name="Cursor">
/// The accepted continuation cursor, or <see langword="null"/> on the first page of a walk.
/// </param>
internal sealed record EducationOrganizationProjectionRequest(
    int DataStoreId,
    int Limit,
    string ContractVersion,
    string BindingHash,
    ProjectionCursor? Cursor
);
