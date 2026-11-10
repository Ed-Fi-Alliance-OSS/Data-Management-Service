// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;

/// <summary>Why <c>dms.DataStoreIdentity</c> could not be read as a single valid identity (6.5 step 2).</summary>
public enum DmsIdentityReadProblem
{
    TableMissing,
    NoRows,
    MultipleRows,
    Unparsable,
    EmptyGuid,

    /// <summary>The server definitely refused the read for lack of a privilege; <see cref="DmsIdentityRead.Invalid.Diagnostic"/> carries the provider code.</summary>
    AccessDenied,

    /// <summary>The table exists without a <c>SourceIdentity</c> column (PostgreSQL <c>42703</c>, SQL Server <c>207</c>).</summary>
    ColumnMissing,

    /// <summary>The <c>SourceIdentity</c> column has a type the identity query cannot read as text.</summary>
    ColumnTypeInvalid,
}

/// <summary>
/// The result of reading <c>dms.DataStoreIdentity.SourceIdentity</c> with the delivered rule (F-D1,
/// F-P12): exactly one row, parseable as a <c>D</c>-format GUID, not all zero.
/// </summary>
public abstract record DmsIdentityRead
{
    public sealed record Found : DmsIdentityRead
    {
        public Found(Guid sourceIdentity)
        {
            if (sourceIdentity == Guid.Empty)
            {
                throw new ArgumentException(
                    "An all-zero source identity is not a found identity.",
                    nameof(sourceIdentity)
                );
            }

            SourceIdentity = sourceIdentity;
        }

        public Guid SourceIdentity { get; }
    }

    /// <summary>Any of these makes the target unverifiable; nothing is mutated.</summary>
    public sealed record Invalid(DmsIdentityReadProblem Problem) : DmsIdentityRead
    {
        /// <summary>For a problem a server error proved, such as <see cref="DmsIdentityReadProblem.AccessDenied"/>: its type chain and provider code only, never its message.</summary>
        public DmsProvisioningDiagnostic? Diagnostic { get; init; }
    }
}
