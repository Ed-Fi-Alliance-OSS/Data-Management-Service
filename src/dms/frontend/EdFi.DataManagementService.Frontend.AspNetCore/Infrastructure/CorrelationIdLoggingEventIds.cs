// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure;

/// <summary>
/// Event ids for correlation-ID lifecycle notices. The value is defined in docs/LOGGING.md, which
/// is the source of truth, and pinned by a unit test in this assembly.
/// </summary>
/// <remarks>
/// Deliberately not in <c>Core.External.Logging.RequestLoggingEventIds</c>, which is the
/// two-event contract CMS and DMS keep in lockstep: CMS reads no correlation header and so could
/// never emit this one. The id continues the same <c>1228xxx</c> block so the numbers stay
/// allocated from one place.
/// </remarks>
public static class CorrelationIdLoggingEventIds
{
    /// <summary>
    /// A client-supplied correlation ID was not usable as sent and was adjusted by normalization,
    /// or was discarded in favour of the server-generated trace identifier. Never carries the
    /// original client-supplied value - only derived facts about it. See docs/LOGGING.md.
    /// </summary>
    public static readonly EventId CorrelationIdModified = new(1228003, "CorrelationIdModified");
}
