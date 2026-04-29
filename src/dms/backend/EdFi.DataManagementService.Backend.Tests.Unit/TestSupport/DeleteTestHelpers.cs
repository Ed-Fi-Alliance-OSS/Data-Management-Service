// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace EdFi.DataManagementService.Backend.Tests.Unit.TestSupport;

/// <summary>
/// Configurable <see cref="IRelationalWriteExceptionClassifier"/> double used by delete-path
/// fixtures. Exposes a preset classification plus a call count so individual tests can assert
/// both classification output and that the classifier was invoked the expected number of times.
/// </summary>
internal sealed class ConfigurableRelationalWriteExceptionClassifier : IRelationalWriteExceptionClassifier
{
    /// <summary>
    /// When <c>true</c>, <see cref="IsForeignKeyViolation"/> returns <c>true</c> regardless of
    /// <see cref="ClassificationToReturn"/>. Set this to test the branch where the FK guard fires
    /// but the classifier cannot extract a constraint name (e.g., returns
    /// <see cref="RelationalWriteExceptionClassification.UnrecognizedWriteFailure"/>).
    /// </summary>
    public bool IsForeignKeyViolationToReturn { get; set; }

    public bool IsTransientFailureToReturn { get; set; }

    public RelationalWriteExceptionClassification? ClassificationToReturn { get; set; }

    public int TryClassifyCallCount { get; private set; }

    public bool TryClassify(
        DbException exception,
        [NotNullWhen(true)] out RelationalWriteExceptionClassification? classification
    )
    {
        TryClassifyCallCount++;
        classification = ClassificationToReturn;
        return classification is not null;
    }

    public bool IsForeignKeyViolation(DbException exception) =>
        IsForeignKeyViolationToReturn
        || ClassificationToReturn is RelationalWriteExceptionClassification.ForeignKeyConstraintViolation;

    public bool IsUniqueConstraintViolation(DbException exception) =>
        ClassificationToReturn is RelationalWriteExceptionClassification.UniqueConstraintViolation;

    public bool IsTransientFailure(DbException exception) => IsTransientFailureToReturn;
}
