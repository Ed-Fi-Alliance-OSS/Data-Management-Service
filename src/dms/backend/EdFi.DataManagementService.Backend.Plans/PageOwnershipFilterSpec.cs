// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Security;

namespace EdFi.DataManagementService.Backend.Plans;

/// <summary>
/// The <c>OwnershipBased</c> page filter planned for a GET-many or partition request.
/// </summary>
/// <param name="RawConfiguredIndex">
/// Zero-based position of the earliest <c>OwnershipBased</c> occurrence in the CMS-configured strategy list.
/// Diagnostic only: it says which configured entry produced the filter. It does not order execution, because
/// the filter always runs last among the AND strategies whatever position CMS gave it, and it is never carried
/// into a failure payload, because the filter never denies.
/// </param>
/// <param name="StrategyName">The configured strategy name — always <c>OwnershipBased</c>.</param>
/// <remarks>
/// <para>
/// A filter, not a check. The single-record operations plan an <see cref="OwnershipAuthorizationCheckSpec"/>
/// that authorizes one stored row and answers a mismatch with a 403; a page query instead restricts its
/// candidate relation to documents whose <c>dms.Document.CreatedByOwnershipTokenId</c> is one of the caller's
/// tokens, and a stored null or a non-matching token simply leaves the row out of the page. There is no
/// <c>AUTH1</c> payload, no failure kind, and no per-row outcome to attribute, so this record deliberately
/// carries none of the single-record check's machinery and is never planned by
/// <see cref="OwnershipAuthorizationPlanner"/>.
/// </para>
/// <para>
/// Carries no token list. The tokens are request state that the repository turns into the dialect-specific
/// <see cref="OwnershipTokenParameterization"/> after every preflight terminal has been ruled out, and an empty
/// list is the repository's empty-page result rather than anything this spec can express.
/// </para>
/// </remarks>
public sealed record PageOwnershipFilterSpec(
    int RawConfiguredIndex,
    string StrategyName = AuthorizationStrategyNameConstants.OwnershipBased
);
