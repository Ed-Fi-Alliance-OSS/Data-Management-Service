// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>
/// An engine read the server definitely refused for lack of a privilege, raised by a read whose result type
/// has no place for the refusal (the facts read): nothing more can be learned on that session until an
/// operator changes a grant, so retrying cannot succeed. It carries only <see cref="Diagnostic"/> (type
/// chain, provider code, fixed operation name); the provider's exception is deliberately not kept as the
/// inner exception, because its message may quote the server's data or the connection. The orchestrator
/// classifies it by stage; anywhere it is not classified it stays an unknown outcome.
/// </summary>
public sealed class DmsAccessDeniedException : Exception
{
    public DmsAccessDeniedException(DmsProvisioningDiagnostic diagnostic)
        : base("The server refused the read for lack of a privilege.")
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        Diagnostic = diagnostic;
    }

    public DmsProvisioningDiagnostic Diagnostic { get; }
}
