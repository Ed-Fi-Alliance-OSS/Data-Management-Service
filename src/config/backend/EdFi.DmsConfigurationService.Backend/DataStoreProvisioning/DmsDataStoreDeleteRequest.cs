// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// A request to drop a managed DMS data store (AC 10). The target is dropped only when its
/// <c>dms.DataStoreIdentity.SourceIdentity</c> equals <paramref name="ExpectedSourceIdentity"/>; an absent
/// target is idempotent success.
/// </summary>
public sealed record DmsDataStoreDeleteRequest(DmsTargetDatabaseName TargetName, Guid ExpectedSourceIdentity);
