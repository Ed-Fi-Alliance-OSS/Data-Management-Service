// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FakeItEasy;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
public sealed class Given_CdcFixtureStartupCleanup
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task It_preserves_startup_failure_even_when_cleanup_diagnostics_cannot_be_written(
        bool closedWriter
    )
    {
        using var diagnostics = new StringWriter();
        if (closedWriter)
        {
            await diagnostics.DisposeAsync();
        }
        var resource = A.Fake<IAsyncDisposable>();
        A.CallTo(() => resource.DisposeAsync())
            .Returns(new ValueTask(Task.FromException(new IOException("private-cleanup"))));
        var original = new InvalidOperationException("private-startup");
        Func<Task> startup = async () =>
        {
            try
            {
                throw original;
            }
            catch
            {
                await CdcFixtureStartupCleanup.DisposeAsync(resource, diagnostics);
                throw;
            }
        };
        (await startup.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(original);
        A.CallTo(() => resource.DisposeAsync()).MustHaveHappenedOnceExactly();
        if (!closedWriter)
        {
            diagnostics
                .ToString()
                .Should()
                .Contain("cleanup failed after startup failure")
                .And.NotContain("private-");
        }
    }
}
