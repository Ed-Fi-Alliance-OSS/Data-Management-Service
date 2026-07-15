// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const initializerPath = path.join(__dirname, '..', 'swagger-initializer.js');
const routeContextPath = path.join(__dirname, '..', 'edfi-route-contexts-from-spec.js');

const defaultUrls = [
    { name: 'Resources', url: 'http://localhost:8080/metadata/specifications/resources-spec.json' },
    { name: 'Descriptors', url: 'http://localhost:8080/metadata/specifications/descriptors-spec.json' },
];

const loadInitializer = async (metadata, fetchOptions = {}) => {
    const { error = null, ok = true, routeContextState = null, status = 200 } = fetchOptions;
    let configuration;
    const fetchCalls = [];
    const swaggerUiBundle = (options) => {
        configuration = options;
        return {};
    };
    swaggerUiBundle.presets = { apis: {} };
    const document = {
        body: {},
        querySelectorAll: () => [],
        title: '',
    };
    const window = {
        DMS_HTTP_PORTS: '8080',
        EdFiCustomFields: {},
        ...(routeContextState ? { __edfiRouteContextState: routeContextState } : {}),
    };
    const context = {
        AbortController,
        MutationObserver: class {
            observe() {}
        },
        SwaggerUIBundle: swaggerUiBundle,
        SwaggerUIStandalonePreset: {},
        clearInterval: () => {},
        clearTimeout: () => {},
        console: { error: () => {}, log: () => {}, warn: () => {} },
        document,
        fetch: async (url) => {
            fetchCalls.push(url);
            if (error) {
                throw error;
            }
            return {
                json: async () => metadata,
                ok,
                status,
            };
        },
        setInterval: () => 0,
        setTimeout,
        window,
    };

    vm.runInNewContext(fs.readFileSync(initializerPath, 'utf8'), context, { filename: initializerPath });
    window.onload();

    for (let attempt = 0; !configuration && attempt < 10; attempt += 1) {
        await new Promise((resolve) => setTimeout(resolve, 0));
    }

    return { configuration, fetchCalls };
};

test('metadata endpointUri entries are passed directly to Swagger UI', async () => {
    const metadata = [
        {
            endpointUri: 'http://localhost:8080/metadata/specifications/resources-spec.json',
            name: 'Resources',
        },
        {
            endpointUri: 'http://localhost:8080/metadata/changequeries/v1/swagger.json',
            name: 'Change-Queries',
        },
        {
            endpointUri: 'http://localhost:8080/metadata/specifications/discovery-spec.json',
            name: 'Discovery',
        },
    ];

    const { configuration, fetchCalls } = await loadInitializer(metadata);

    assert.deepEqual(JSON.parse(JSON.stringify(configuration.urls)), [
        {
            name: 'Resources',
            url: 'http://localhost:8080/metadata/specifications/resources-spec.json',
        },
        {
            name: 'Change-Queries',
            url: 'http://localhost:8080/metadata/changequeries/v1/swagger.json',
        },
    ]);
    assert.equal(fetchCalls.length, 1);
    assert.equal('responseInterceptor' in configuration, false);
});

test('metadata discovery falls back when the request fails', async () => {
    const { configuration } = await loadInitializer(null, { error: new Error('unavailable') });

    assert.deepEqual(JSON.parse(JSON.stringify(configuration.urls)), defaultUrls);
});

test('metadata discovery falls back when the response is unsuccessful', async () => {
    const { configuration } = await loadInitializer(null, { ok: false, status: 503 });

    assert.deepEqual(JSON.parse(JSON.stringify(configuration.urls)), defaultUrls);
});

test('metadata discovery falls back for an invalid response shape', async () => {
    const { configuration } = await loadInitializer({ unexpected: [] });

    assert.deepEqual(JSON.parse(JSON.stringify(configuration.urls)), defaultUrls);
});

test('metadata discovery falls back for an empty catalog', async () => {
    const { configuration } = await loadInitializer([]);

    assert.deepEqual(JSON.parse(JSON.stringify(configuration.urls)), defaultUrls);
});

test('request interceptor applies route-context rewriting to Try-it-out requests', async () => {
    const routeContextState = {
        getRoutePrefix: () => '/DistrictB/2025',
        getSelections: () => ({ tenant: 'DistrictB', schoolYear: '2025' }),
        rewriteRequestUrl: () => 'http://localhost:8080/DistrictB/2025/changeQueries/v1/availableChangeVersions',
    };
    const { configuration } = await loadInitializer([], { routeContextState });
    const request = configuration.requestInterceptor({
        url: 'http://localhost:8080/changeQueries/v1/availableChangeVersions',
    });

    assert.equal(
        request.url,
        'http://localhost:8080/DistrictB/2025/changeQueries/v1/availableChangeVersions'
    );
});

test('a single untagged operation uses its path as the group title', async () => {
    const { configuration } = await loadInitializer([]);
    const singleOperationGroupPlugin = configuration.plugins[0];
    const updateSpec = singleOperationGroupPlugin().statePlugins.spec.wrapActions.updateSpec(
        (specification) => specification,
        {}
    );
    const specification = {
        paths: {
            '/availableChangeVersions': {
                get: {
                    summary: 'Retrieves the available change version range.',
                },
            },
        },
    };

    const updated = updateSpec(specification);

    assert.deepEqual(JSON.parse(JSON.stringify(updated.paths['/availableChangeVersions'].get.tags)), ['availableChangeVersions']);
    assert.deepEqual(JSON.parse(JSON.stringify(updated.tags)), [
        {
            description: 'Retrieves the available change version range.',
            name: 'availableChangeVersions',
        },
    ]);
});

test('Change Queries route context is applied once', async () => {
    let specCalls = 0;
    const swaggerUi = {
        firstChild: null,
        insertBefore(element) {
            element.parentNode = this;
        },
    };
    const createElement = () => ({
        addEventListener: () => {},
        appendChild: () => {},
        style: {},
    });
    const document = {
        createElement,
        querySelector: (selector) => selector === '.swagger-ui' ? swaggerUi : null,
    };
    const window = {
        ui: {
            spec: () => {
                specCalls += 1;
                return {
                    toJS: () => ({
                        json: {
                            servers: [
                                {
                                    url: 'http://localhost:8080/{tenant}/{schoolYear}/changeQueries/v1',
                                    variables: {
                                        schoolYear: { default: '2024' },
                                        tenant: { default: 'DistrictA' },
                                    },
                                },
                            ],
                        },
                    }),
                };
            },
            specActions: {},
            specSelectors: {},
        },
    };
    const context = {
        clearTimeout,
        console: { warn: () => {} },
        document,
        setTimeout,
        window,
    };

    vm.runInNewContext(fs.readFileSync(routeContextPath, 'utf8'), context, { filename: routeContextPath });

    const plugin = window.EdFiRouteContext();
    const updateSpec = plugin.statePlugins.spec.wrapActions.updateSpec((specification) => specification, {});
    updateSpec({ servers: [] });
    await new Promise((resolve) => setTimeout(resolve, 450));

    assert.equal(specCalls, 1);
    assert.equal(window.__edfiRouteContextState.getRoutePrefix(), '/DistrictA/2024');
    assert.equal(
        window.__edfiRouteContextState.rewriteRequestUrl(
            'http://localhost:8080/changeQueries/v1/availableChangeVersions'
        ),
        'http://localhost:8080/DistrictA/2024/changeQueries/v1/availableChangeVersions'
    );
    assert.equal(
        window.__edfiRouteContextState.rewriteRequestUrl(
            'http://localhost:8080/DistrictA/2024/changeQueries/v1/availableChangeVersions'
        ),
        'http://localhost:8080/DistrictA/2024/changeQueries/v1/availableChangeVersions'
    );
    assert.equal(
        window.__edfiRouteContextState.rewriteRequestUrl(
            'http://localhost:8080/DistrictB/2025/changeQueries/v1/availableChangeVersions'
        ),
        'http://localhost:8080/DistrictB/2025/changeQueries/v1/availableChangeVersions'
    );
    assert.equal(
        window.__edfiRouteContextState.rewriteRequestUrl(
            'http://localhost:8080/DistrictB/2025/data/ed-fi/students'
        ),
        'http://localhost:8080/DistrictB/2025/data/ed-fi/students'
    );
    assert.equal(
        window.__edfiRouteContextState.rewriteRequestUrl(
            'http://localhost:8080/data/ed-fi/students'
        ),
        'http://localhost:8080/DistrictA/2024/data/ed-fi/students'
    );
    assert.equal(
        window.__edfiRouteContextState.rewriteRequestUrl(
            'http://localhost:8080/DistrictA/2024/data/ed-fi/students'
        ),
        'http://localhost:8080/DistrictA/2024/data/ed-fi/students'
    );
});
