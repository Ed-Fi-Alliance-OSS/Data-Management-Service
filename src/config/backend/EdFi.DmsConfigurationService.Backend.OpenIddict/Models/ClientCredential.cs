// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.OpenIddict.Models;

/// <summary>
/// The fields client authentication reads, and nothing else. A distinct type from
/// <see cref="ApplicationInfo"/> so that it can never reach code that issues a token from an
/// application's full metadata.
/// </summary>
/// <param name="ApplicationId">The stored id of the application.</param>
/// <param name="StoredClientId">
/// The client id as stored, which may differ in spelling from the one requested; empty when the
/// store holds none.
/// </param>
/// <param name="StoredClientSecret">The stored (normally hashed) secret; empty when the store holds none.</param>
/// <param name="IsApproved">
/// Whether every API client registered for the application is approved; <c>true</c> when it has
/// none.
/// </param>
public sealed record ClientCredential(
    Guid ApplicationId,
    string StoredClientId,
    string StoredClientSecret,
    bool IsApproved
);
