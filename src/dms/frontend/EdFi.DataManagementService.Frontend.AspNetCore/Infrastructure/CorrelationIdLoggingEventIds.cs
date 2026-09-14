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
/// Deliberately not added to <c>Core.External.Logging.RequestLoggingEventIds</c>. That class is the
/// two-event contract CMS and DMS both implement and keep in lockstep, and CMS has no
/// <c>CorrelationIdHeader</c> setting, reads no correlation header, and therefore can never emit
/// this event. Putting a DMS-only event in the shared class would create a contract CMS cannot
/// honor. The id continues the same <c>1228xxx</c> block so the numbers stay allocated from one
/// place, and docs/LOGGING.md records it as DMS-only.
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
