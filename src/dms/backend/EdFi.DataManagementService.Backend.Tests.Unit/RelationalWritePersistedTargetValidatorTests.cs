// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalWritePersistedTargetValidator
{
    [Test]
    public void It_throws_when_the_persisted_ContentVersion_is_not_positive()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetContext = new RelationalWriteTargetContext.ExistingDocument(345L, documentUuid, 44L);
        var persistedTarget = new RelationalWritePersistResult(345L, documentUuid, ContentVersion: 0);

        var act = () => RelationalWritePersistedTargetValidator.Validate(targetContext, persistedTarget);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ContentVersion*");
    }

    [Test]
    public void It_does_not_throw_when_the_persisted_ContentVersion_is_positive_and_identity_matches()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var targetContext = new RelationalWriteTargetContext.ExistingDocument(345L, documentUuid, 44L);
        var persistedTarget = new RelationalWritePersistResult(345L, documentUuid, ContentVersion: 77L);

        var act = () => RelationalWritePersistedTargetValidator.Validate(targetContext, persistedTarget);

        act.Should().NotThrow();
    }

    [Test]
    public void It_reports_identity_mismatch_before_the_ContentVersion_guard()
    {
        var documentUuid = new DocumentUuid(Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb"));
        var otherUuid = new DocumentUuid(Guid.Parse("bbbbbbbb-1111-2222-3333-cccccccccccc"));
        var targetContext = new RelationalWriteTargetContext.ExistingDocument(345L, documentUuid, 44L);
        var persistedTarget = new RelationalWritePersistResult(999L, otherUuid, ContentVersion: 0);

        var act = () => RelationalWritePersistedTargetValidator.Validate(targetContext, persistedTarget);

        act.Should().Throw<InvalidOperationException>().WithMessage("*different committed target identity*");
    }
}
