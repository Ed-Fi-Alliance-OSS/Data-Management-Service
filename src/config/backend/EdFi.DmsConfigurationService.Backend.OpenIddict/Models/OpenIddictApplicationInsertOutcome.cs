// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// What <c>IOpenIddictDataRepository.InsertApplicationAsync</c> did with the application row.
/// Only the client-key uniqueness constraint is reported as an outcome; every other failure of
/// the insert propagates as the engine exception it is. Callers arrange and compare the named
/// members and never rely on the enum's default value.
/// </summary>
public enum OpenIddictApplicationInsertOutcome
{
    /// <summary>
    /// The application row was written inside the caller's transaction.
    /// </summary>
    Inserted,

    /// <summary>
    /// Nothing was written because <c>UX_OpenIddictApplication_ClientId</c> already holds the
    /// requested client key. The caller's transaction is left to the caller to roll back.
    /// </summary>
    DuplicateClientId,
}
