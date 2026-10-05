// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.EducationOrganizationProjection;

/// <summary>
/// The stages the projection handler passes through after the set has been read, in order.
/// </summary>
internal enum ProjectionProcessingStage
{
    /// <summary>Whole-set validation: identifiers, names, references and parent cycles.</summary>
    Validation,

    /// <summary>Parent selection for every row of the validated set.</summary>
    Precedence,

    /// <summary>The digest of the projected set.</summary>
    Hashing,

    /// <summary>Selecting the page's items after the cursor position.</summary>
    Slicing,

    /// <summary>Writing the response.</summary>
    Publishing,
}

/// <summary>
/// Reports the projection handler's progress through a page. Internal test seam: production uses
/// <see cref="NoOpProjectionProcessingObserver"/>. A test can cancel the request from a callback at a
/// named point and observe exactly which stages ran.
/// </summary>
internal interface IProjectionProcessingObserver
{
    /// <summary>Called when the handler enters a stage, before that stage's first cancellation check.</summary>
    void Stage(ProjectionProcessingStage stage);

    /// <summary>
    /// Called every <see cref="ProjectionProcessing.CheckpointInterval"/> rows inside a stage that
    /// walks the set, immediately before the cancellation check for that checkpoint.
    /// </summary>
    /// <param name="stage">The stage in progress.</param>
    /// <param name="rowsProcessed">How many rows the stage has processed so far.</param>
    void Checkpoint(ProjectionProcessingStage stage, int rowsProcessed);
}

/// <summary>
/// The production observer, which observes nothing.
/// </summary>
internal sealed class NoOpProjectionProcessingObserver : IProjectionProcessingObserver
{
    public static NoOpProjectionProcessingObserver Instance { get; } = new();

    private NoOpProjectionProcessingObserver() { }

    public void Stage(ProjectionProcessingStage stage) { }

    public void Checkpoint(ProjectionProcessingStage stage, int rowsProcessed) { }
}

/// <summary>
/// The cancellation checkpoints of the work the handler does over the whole set.
/// </summary>
internal static class ProjectionProcessing
{
    /// <summary>How many rows a stage processes between cancellation checks.</summary>
    public const int CheckpointInterval = 1000;

    /// <summary>
    /// Reports a stage transition and checks for cancellation.
    /// </summary>
    public static void EnterStage(
        IProjectionProcessingObserver observer,
        ProjectionProcessingStage stage,
        CancellationToken cancellationToken
    )
    {
        observer.Stage(stage);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// Reports a checkpoint and checks for cancellation when <paramref name="rowsProcessed"/> is a
    /// whole multiple of <see cref="CheckpointInterval"/>; otherwise does nothing.
    /// </summary>
    public static void Checkpoint(
        IProjectionProcessingObserver observer,
        ProjectionProcessingStage stage,
        int rowsProcessed,
        CancellationToken cancellationToken
    )
    {
        if (rowsProcessed % CheckpointInterval != 0)
        {
            return;
        }

        observer.Checkpoint(stage, rowsProcessed);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
