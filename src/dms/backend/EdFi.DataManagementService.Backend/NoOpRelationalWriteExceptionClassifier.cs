// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace EdFi.DataManagementService.Backend;

public sealed class NoOpRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
{
    public bool TryClassify(
        DbException exception,
        [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
    )
    {
        ArgumentNullException.ThrowIfNull(exception);
        classification = null;
        return false;
    }

    public bool IsTransientFailure(DbException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return false;
    }
}
