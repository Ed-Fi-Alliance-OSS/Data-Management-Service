// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// Provides a data-driven selector for tenant and route qualifier segments
// discovered from the OpenAPI server variables definition.

window.EdFiRouteContext = function () {
    const state = {
        routeOrder: [],
        selections: {},
        urlTemplate: '',
        container: null,
        computedValueEl: null,
        rebuildTimeout: null,
    };

    // Shared state for other scripts (e.g., request interceptor)
    const sharedState = {
        getSelections: () => ({ ...state.selections }),
        getRoutePrefix: () => buildRoutePrefix(),
        rewriteRequestUrl: (url) => rewriteRequestUrl(url),
    };
    window.__edfiRouteContextState = sharedState;

    const waitForSwaggerUI = () =>
        new Promise((resolve) => {
            const checkSwaggerUI = () => {
                if (window.ui && window.ui.spec && window.ui.specActions && window.ui.specSelectors) {
                    resolve();
                } else {
                    setTimeout(checkSwaggerUI, 100);
                }
            };
            checkSwaggerUI();
        });

    const extractSpec = () => {
        if (!window.ui || typeof window.ui.spec !== 'function') {
            return null;
        }

        const specWrapper = window.ui.spec();
        if (!specWrapper) {
            return null;
        }

        if (typeof specWrapper.toJS === 'function') {
            const jsObject = specWrapper.toJS();
            return jsObject.json || jsObject;
        }

        if (specWrapper.json) {
            return specWrapper.json;
        }

        return specWrapper;
    };

    const scheduleRebuild = () => {
        if (state.rebuildTimeout) {
            clearTimeout(state.rebuildTimeout);
        }
        state.rebuildTimeout = setTimeout(rebuildSelector, 250);
    };

    const rebuildSelector = async () => {
        await waitForSwaggerUI();
        await new Promise((resolve) => setTimeout(resolve, 150));

        const spec = extractSpec();
        const serverDetails = parseServerDetails(spec);
        if (!serverDetails) {
            teardownSelector();
            return;
        }

        state.routeOrder = serverDetails.order;
        state.urlTemplate = serverDetails.urlTemplate;
    state.selections = buildUpdatedSelections(serverDetails.fields, serverDetails.defaults);

        renderSelector(serverDetails.fields);
        updateComputedUrl();
    };

    const buildUpdatedSelections = (fields, defaults) => {
        const nextSelections = { ...(defaults || {}) };

        if (!Array.isArray(fields) || fields.length === 0) {
            return nextSelections;
        }

        fields.forEach((field) => {
            if (!field || !field.name) {
                return;
            }

            if (!Object.prototype.hasOwnProperty.call(state.selections, field.name)) {
                return;
            }

            const priorValue = stringifyValue(state.selections[field.name]);
            if (priorValue === undefined) {
                return;
            }

            const trimmedPrior = String(priorValue).trim();

            if (Array.isArray(field.options) && field.options.length > 0) {
                if (field.options.includes(trimmedPrior)) {
                    nextSelections[field.name] = trimmedPrior;
                }
                return;
            }

            nextSelections[field.name] = trimmedPrior;
        });

        return nextSelections;
    };

    const parseServerDetails = (spec) => {
        if (!spec || !Array.isArray(spec.servers)) {
            return null;
        }

        const server = spec.servers.find((s) => s && s.variables && Object.keys(s.variables).length > 0) || spec.servers[0];
        if (!server || !server.variables || !server.url) {
            return null;
        }

        const placeholders = extractPlaceholders(server.url);
        if (placeholders.length === 0) {
            return null;
        }

        const fields = [];
        const defaults = {};
        placeholders.forEach((name) => {
            const definition = server.variables[name];
            if (!definition) {
                return;
            }

            const options = Array.isArray(definition.enum)
                ? definition.enum.map((value) => stringifyValue(value)).filter((value) => value !== undefined)
                : [];
            const fallback = options.length > 0 ? options[0] : '';
            const defaultValue = definition.default !== undefined && definition.default !== null
                ? stringifyValue(definition.default)
                : fallback;

            defaults[name] = defaultValue || '';
            fields.push({
                name,
                label: definition.description || formatLabel(name),
                options,
                value: defaults[name],
            });
        });

        if (fields.length === 0) {
            return null;
        }

        return {
            urlTemplate: server.url,
            order: fields.map((field) => field.name),
            fields,
            defaults,
        };
    };

    const extractPlaceholders = (template) => {
        if (typeof template !== 'string') {
            return [];
        }

        const matches = template.match(/\{([^}]+)\}/g);
        if (!matches) {
            return [];
        }

        return matches.map((match) => match.slice(1, -1));
    };

    const renderSelector = (fields) => {
        teardownSelector();

        if (!fields || fields.length === 0) {
            return;
        }

        const container = document.createElement('div');
        container.className = 'route-context-selector';
        container.style.cssText = `
            padding: 20px;
            background-color: #f4f8ff;
            border-bottom: 1px solid #d9e4ff;
            display: flex;
            flex-wrap: wrap;
            gap: 16px;
            align-items: flex-end;
        `;

        fields.forEach((field) => {
            const fieldWrapper = document.createElement('div');
            fieldWrapper.className = 'route-context-field';
            fieldWrapper.style.cssText = `
                display: flex;
                flex-direction: column;
                gap: 4px;
                min-width: 160px;
            `;

            const label = document.createElement('label');
            label.textContent = `${field.label}:`;
            label.style.cssText = `
                font-weight: bold;
                font-size: 13px;
            `;
            label.htmlFor = `route-context-${slugify(field.name)}`;
            fieldWrapper.appendChild(label);

            if (field.options.length > 0) {
                const select = document.createElement('select');
                select.id = label.htmlFor;
                select.className = 'route-context-control';
                select.value = field.value || '';
                select.style.cssText = controlStyles();
                field.options.forEach((optionValue) => {
                    const option = document.createElement('option');
                    option.value = optionValue;
                    option.textContent = optionValue;
                    if (optionValue === field.value) {
                        option.selected = true;
                    }
                    select.appendChild(option);
                });
                select.addEventListener('change', (event) => {
                    handleValueChange(field.name, event.target.value);
                });
                fieldWrapper.appendChild(select);
            } else {
                const input = document.createElement('input');
                input.id = label.htmlFor;
                input.type = 'text';
                input.className = 'route-context-control';
                input.placeholder = field.label;
                input.value = field.value || '';
                input.style.cssText = controlStyles();
                input.addEventListener('input', (event) => {
                    handleValueChange(field.name, event.target.value);
                });
                fieldWrapper.appendChild(input);
            }

            container.appendChild(fieldWrapper);
        });

        const computedWrapper = document.createElement('div');
        computedWrapper.className = 'route-context-computed-url';
        computedWrapper.style.cssText = `
            margin-left: auto;
            min-width: 240px;
            font-size: 13px;
            color: #374151;
            display: flex;
            flex-direction: column;
            gap: 4px;
        `;

        const computedLabel = document.createElement('strong');
        computedLabel.textContent = 'Computed URL:';
        computedWrapper.appendChild(computedLabel);

        const computedValue = document.createElement('span');
        computedValue.className = 'route-context-computed-url-value';
        computedValue.style.cssText = 'font-family: monospace; color: #0b69a3; word-break: break-all;';
        computedWrapper.appendChild(computedValue);
        state.computedValueEl = computedValue;

        container.appendChild(computedWrapper);

        insertContainer(container);
        state.container = container;
    };

    const insertContainer = (container) => {
        const infoContainer = document.querySelector('.information-container');
        if (infoContainer && infoContainer.parentNode) {
            infoContainer.parentNode.insertBefore(container, infoContainer);
            return;
        }

        const wrapper = document.querySelector('.swagger-ui');
        if (wrapper) {
            wrapper.insertBefore(container, wrapper.firstChild);
        }
    };

    const teardownSelector = () => {
        if (state.container && state.container.parentNode) {
            state.container.parentNode.removeChild(state.container);
        }
        state.container = null;
        state.computedValueEl = null;
        state.routeOrder = [];
        state.selections = {};
        state.urlTemplate = '';
    };

    const handleValueChange = (name, rawValue) => {
        state.selections[name] = (rawValue ?? '').trim();
        updateComputedUrl();
    };

    const buildRoutePrefix = () => {
        if (!state.routeOrder.length) {
            return '';
        }

        const segments = state.routeOrder
            .map((segment) => (state.selections[segment] || '').trim())
            .filter((value) => value.length > 0 && !/^\{.*\}$/.test(value));

        return segments.length > 0 ? `/${segments.join('/')}` : '';
    };

    const updateComputedUrl = () => {
        if (!state.computedValueEl || !state.urlTemplate) {
            return;
        }

        let computedUrl = state.urlTemplate;
        state.routeOrder.forEach((name) => {
            const value = state.selections[name] || `{${name}}`;
            const pattern = new RegExp(`\\{${escapeRegex(name)}\\}`, 'g');
            computedUrl = computedUrl.replace(pattern, value);
        });

        state.computedValueEl.textContent = computedUrl;
    };

    const rewriteRequestUrl = (url) => {
        if (!url || typeof url !== 'string') {
            return url;
        }

        let rewrittenUrl = url;

        // Browser cannot resolve Docker DNS names; map to localhost while preserving scheme/port.
        rewrittenUrl = rewrittenUrl.replace(/:\/\/dms-config-service(?=[:/]|$)/g, '://localhost');

        const routePrefix = buildRoutePrefix();
        if (routePrefix && rewrittenUrl.includes('/data/') && !rewrittenUrl.includes('/metadata/')) {
            if (!rewrittenUrl.includes(`${routePrefix}/data/`)) {
                rewrittenUrl = rewrittenUrl.replace(/\/data\//, `${routePrefix}/data/`);
            }
        }

        if (routePrefix && rewrittenUrl.includes('/connect/token')) {
            if (!rewrittenUrl.includes(`/connect/token${routePrefix}`)) {
                rewrittenUrl = rewrittenUrl.replace(/\/connect\/token(?=\/|\?|#|$)/, `/connect/token${routePrefix}`);
            }
        }

        return rewrittenUrl;
    };

    const stringifyValue = (value) => {
        if (value === null || value === undefined) {
            return undefined;
        }
        if (typeof value === 'string') {
            return value;
        }
        if (typeof value === 'number' || typeof value === 'boolean') {
            return String(value);
        }
        return JSON.stringify(value);
    };

    const formatLabel = (name) => {
        if (typeof name !== 'string' || name.length === 0) {
            return 'Value';
        }
        const spaced = name
            .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
            .replace(/-/g, ' ')
            .replace(/_/g, ' ')
            .trim()
            .toLowerCase();
        const words = spaced.split(/\s+/).map((word) => {
            if (word.length === 0) {
                return word;
            }
            return word.charAt(0).toUpperCase() + word.slice(1);
        });
        return words.join(' ');
    };

    const slugify = (value) =>
<<<<<<< HEAD
<<<<<<< HEAD
=======
>>>>>>> f32f4527 (PR feedback)
        String(value || '')
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, '-')
            .replace(/^-|-$/g, '');
<<<<<<< HEAD
=======
            .replace(/-/g, ' ')
            .replace(/_/g, ' ')
            .trim()
            .toLowerCase();
        const words = spaced.split(/\s+/).map((word) => {
            if (word.length === 0) {
                return word;
            }
            return word.charAt(0).toUpperCase() + word.slice(1);
        });
        return words.join(' ');
>>>>>>> dc43acc6 (Apply copilot review notes)
=======
>>>>>>> f32f4527 (PR feedback)

    const escapeRegex = (value) => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

    const controlStyles = () => `
        padding: 8px 12px;
        border: 1px solid #b0c4de;
        border-radius: 4px;
        font-size: 14px;
        min-height: 36px;
        background-color: #fff;
        color: #1f2937;
<<<<<<< HEAD
<<<<<<< HEAD
=======
>>>>>>> f32f4527 (PR feedback)
    `;

    return {
        statePlugins: {
            spec: {
                wrapActions: {
                    updateSpec: (oriAction, system) => (...args) => {
<<<<<<< HEAD
=======
>>>>>>> dc43acc6 (Apply copilot review notes)
=======
>>>>>>> f32f4527 (PR feedback)
                        try {
                            if (args && args.length > 0) {
                                let spec = args[0];
                                const originalWasString = typeof spec === 'string';

                                if (originalWasString) {
                                    try {
                                        spec = JSON.parse(spec);
                                    } catch (parseError) {
                                        // If parsing fails, leave the spec as-is.
                                        spec = null;
                                    }
                                }

                                if (spec && typeof spec === 'object' && Array.isArray(spec.servers)) {
                                    spec.servers = spec.servers.map((server) => ({
                                        ...server,
                                        url:
                                            typeof server.url === 'string'
                                                ? server.url.replace(/:\/\/dms-config-service(?=[:/]|$)/g, '://localhost')
                                                : server.url,
                                    }));

                                    args[0] = originalWasString ? JSON.stringify(spec) : spec;
                                }
<<<<<<< HEAD
=======
                            }
                        } catch (error) {
                            console.warn('Route context plugin failed to normalize server host:', error);
                        }

                        const result = oriAction(...args);
<<<<<<< HEAD
                                        typeof server.url === 'string'
                                            ? server.url.replace('dms-config-service', 'localhost')
                                            : server.url,
                                }));
>>>>>>> dc43acc6 (Apply copilot review notes)
                            }
                        } catch (error) {
                            console.warn('Route context plugin failed to normalize server host:', error);
                        }

                        const result = oriAction(...args);
=======
>>>>>>> f32f4527 (PR feedback)
                        scheduleRebuild();
                        return result;
                    },
                },
            },
        },
    };
};
