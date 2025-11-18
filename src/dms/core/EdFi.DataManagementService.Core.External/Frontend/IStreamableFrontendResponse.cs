// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EdFi.DataManagementService.Core.External.Frontend;

/// <summary>
/// Describes a frontend response that writes its body directly to a stream.
/// </summary>
public interface IStreamableFrontendResponse : IFrontendResponse
{
    /// <summary>
    /// Delegate invoked by the frontend to stream the response body.
    /// </summary>
    Func<Stream, CancellationToken, Task> WriteBodyAsync { get; }
}
