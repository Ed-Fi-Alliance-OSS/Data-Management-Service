// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;

namespace EdFi.DmsConfigurationService.Backend.Jobs;

/// <summary>
/// A CMS database transaction that owns its connection (spec D-18). Disposing it without committing rolls
/// back and releases the connection.
/// </summary>
public interface ICmsTransaction : IAsyncDisposable
{
    DbTransaction Transaction { get; }

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
