// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Microsoft.AspNetCore.Mvc.Testing;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;

internal sealed class WebApplicationFactoryTracker<TEntryPoint>
    where TEntryPoint : class
{
    private readonly List<WebApplicationFactory<TEntryPoint>> _factories = [];

    public WebApplicationFactory<TEntryPoint> Track(WebApplicationFactory<TEntryPoint> factory)
    {
        _factories.Add(factory);
        return factory;
    }

    public void DisposeTrackedFactories()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        _factories.Clear();
    }
}
