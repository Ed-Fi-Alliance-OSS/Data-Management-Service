// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

// VERSION: Extracts school years automatically from OpenAPI spec servers array

window.EdFiSchoolYear = function () {

    const dmsPort = window.DMS_HTTP_PORTS || "8080"; // fallback in case DMS_HTTP_PORTS is not set

    let selectedYear = null;
    let schoolYears = [];
    let swaggerUIInstance = null;

    // Extract school years from OpenAPI spec servers section
    const extractSchoolYearsFromSpec = (spec) => {
        try {
            if (!spec || !spec.servers || !Array.isArray(spec.servers)) {
                console.warn('No servers found in OpenAPI spec');
                return [];
            }

            const years = [];

            spec.servers.forEach(server => {
                // Check if server has variables defined (OpenAPI 3.0 server variables)
                if (server.variables) {
                    // Look for School Year variable (case-insensitive search)
                    const schoolYearVar = Object.keys(server.variables).find(key =>
                        key.toLowerCase().includes('school') && key.toLowerCase().includes('year')
                    );

                    if (schoolYearVar && server.variables[schoolYearVar].enum) {
                        // Extract years from the enum array
                        server.variables[schoolYearVar].enum.forEach(year => {
                            const yearNum = parseInt(year);
                            if (!isNaN(yearNum) && !years.includes(yearNum)) {
                                years.push(yearNum);
                            }
                        });
                    }
                }
                // Fallback: try to extract years from server URL (backward compatibility)
                else if (server.url) {
                    const yearRegex = /\/(20\d{2})\//; // Match year pattern like /2024/
                    const match = server.url.match(yearRegex);
                    if (match && match[1]) {
                        const year = parseInt(match[1]);
                        if (!years.includes(year)) {
                            years.push(year);
                        }
                    }
                }
            });

            // Sort years in descending order (newest first)
            years.sort((a, b) => b - a);

            return years;
        } catch (error) {
            console.error('Error extracting school years from spec:', error);
            return [];
        }
    };

    // Initialize the plugin
    const initialize = async (system) => {
        swaggerUIInstance = system;

        // Wait for Swagger UI to be fully loaded
        await waitForSwaggerUI();

        // Wait a bit more for spec to be fully processed
        await new Promise(resolve => setTimeout(resolve, 500));

        try {
            // Extract school years from spec
            // window.ui.spec() returns an Immutable object, use toJS() to get plain object
            const specWrapper = window.ui.spec();

            // Get the actual spec - try both .toJS() and .json properties
            let spec = null;
            if (specWrapper && typeof specWrapper.toJS === 'function') {
                const jsObject = specWrapper.toJS();
                // Check if it has a 'json' property (parsed spec) or use the whole object
                spec = jsObject.json || jsObject;
            } else if (specWrapper && specWrapper.json) {
                spec = specWrapper.json;
            } else {
                spec = specWrapper;
            }

            // If spec is still a string, parse it
            if (typeof spec === 'string') {
                try {
                    spec = JSON.parse(spec);
                } catch (e) {
                    console.error('Failed to parse spec string:', e);
                }
            }

            schoolYears = extractSchoolYearsFromSpec(spec);

            if (schoolYears.length === 0) {
                console.warn('No school years found in OpenAPI spec servers');
                return;
            }

            // Calculate current year based on current date
            // If current month > June (6), use next year; otherwise use current year
            const currentDate = new Date();
            const currentCalendarYear = currentDate.getFullYear();
            const currentMonth = currentDate.getMonth() + 1; // JavaScript months are 0-indexed
            const calculatedCurrentYear = currentMonth > 6 ? currentCalendarYear + 1 : currentCalendarYear;

            // Find the calculated current year in the available school years, or use the most recent
            selectedYear = schoolYears.includes(calculatedCurrentYear)
                ? calculatedCurrentYear
                : schoolYears[0];

            // Create the selector UI
            createSchoolYearSelector();

            // Wait for server selector to be available
            await waitForServerSelector();

            // Populate server selector
            populateServerSelector();
        } catch (error) {
            console.error('Error initializing school year plugin:', error);
        }
    };

    // Wait for Swagger UI to be fully initialized
    const waitForSwaggerUI = () => {
        return new Promise((resolve) => {
            const checkSwaggerUI = () => {
                if (window.ui && window.ui.spec && window.ui.specActions && window.ui.specSelectors) {
                    resolve();
                } else {
                    setTimeout(checkSwaggerUI, 100);
                }
            };
            checkSwaggerUI();
        });
    };

    // Wait for server selector to be available in DOM
    const waitForServerSelector = () => {
        return new Promise((resolve) => {
            const checkSelector = () => {
                const serverSelector = document.querySelector('.servers select');
                if (serverSelector) {
                    resolve();
                } else {
                    setTimeout(checkSelector, 100);
                }
            };
            checkSelector();
        });
    };

    // Create the school year selector UI
    const createSchoolYearSelector = () => {
        // Check if selector already exists
        if (document.querySelector('.school-year-selector')) {
            return;
        }

        const container = document.createElement('div');
        container.className = 'school-year-selector';
        container.style.cssText = `
            padding: 20px;
            background-color: #fafafa;
            border-bottom: 1px solid #d4d4d4;
            display: none;
            align-items: center;
            gap: 15px;
        `;

        const label = document.createElement('label');
        label.textContent = 'School Year:';
        label.style.cssText = `
            font-weight: bold;
            font-size: 14px;
        `;

        const select = document.createElement('select');
        select.className = 'school-year-select';
        select.style.cssText = `
            padding: 8px 12px;
            font-size: 14px;
            border: 1px solid #ccc;
            border-radius: 4px;
            background-color: white;
            cursor: pointer;
        `;

        schoolYears.forEach(year => {
            const option = document.createElement('option');
            option.value = year;
            option.textContent = year;
            if (year === selectedYear) {
                option.selected = true;
            }
            select.appendChild(option);
        });

        select.addEventListener('change', (e) => {
            selectedYear = e.target.value;
            updateServerSelector();
            updateComputedUrl();
        });

        // Add computed URL display
        const computedUrlContainer = document.createElement('div');
        computedUrlContainer.className = 'computed-url-display';
        computedUrlContainer.style.cssText = `
            margin-left: auto;
            font-size: 14px;
            color: #555;
        `;

        // Get initial URL template from spec
        const spec = window.ui.spec().toJS();
        let initialUrl = `http://localhost:${dmsPort}/${selectedYear}/data`;
        if (spec && spec.servers && spec.servers.length > 0) {
            const server = spec.servers[0];
            if (server.url) {
                const varMatch = server.url.match(/\{([^}]*school[^}]*year[^}]*)\}/i);
                if (varMatch && varMatch[1]) {
                    initialUrl = server.url.replace(`{${varMatch[1]}}`, selectedYear);
                }
            }
        }

        computedUrlContainer.innerHTML = `
            <strong>Computed URL:</strong>
            <span class="computed-url-value" style="font-family: monospace; color: #0066cc;">
                ${initialUrl}
            </span>
        `;

        container.appendChild(label);
        container.appendChild(select);
        container.appendChild(computedUrlContainer);

        // Insert at the top of the Swagger UI
        const infoContainer = document.querySelector('.information-container');
        if (infoContainer) {
            infoContainer.parentNode.insertBefore(container, infoContainer);
        } else {
            const wrapper = document.querySelector('.swagger-ui');
            if (wrapper) {
                wrapper.insertBefore(container, wrapper.firstChild);
            }
        }
    };

    // Populate the server selector with year-based URLs
    const populateServerSelector = () => {
        const serverSelector = document.querySelector('.servers select');
        if (!serverSelector) {
            console.warn('Server selector not found');
            return;
        }

        // Get the base URL template from the spec
        const spec = window.ui.spec().toJS();
        let urlTemplate = `http://localhost:${dmsPort}/{schoolYear}/data`;
        let schoolYearVarName = 'schoolYear';

        // Extract URL template and variable name from spec
        if (spec && spec.servers && spec.servers.length > 0) {
            const server = spec.servers[0];
            if (server.url) {
                urlTemplate = server.url;
                // Find the school year variable name in the URL
                const varMatch = urlTemplate.match(/\{([^}]*school[^}]*year[^}]*)\}/i);
                if (varMatch && varMatch[1]) {
                    schoolYearVarName = varMatch[1];
                }
            }
        }

        // Clear existing options
        serverSelector.innerHTML = '';

        // Add options for each school year
        schoolYears.forEach(year => {
            const option = document.createElement('option');
            // Replace the variable placeholder with the actual year
            const yearUrl = urlTemplate.replace(`{${schoolYearVarName}}`, year);
            option.value = yearUrl;
            option.textContent = `${urlTemplate} (${year})`;
            if (year === selectedYear) {
                option.selected = true;
            }
            serverSelector.appendChild(option);
        });

    };

    // Update server selector when year changes
    const updateServerSelector = () => {
        const serverSelector = document.querySelector('.servers select');
        if (serverSelector) {
            // Get the URL template from spec
            const spec = window.ui.spec().toJS();
            let urlTemplate = `http://localhost:${dmsPort}/{schoolYear}/data`;
            let schoolYearVarName = 'schoolYear';

            if (spec && spec.servers && spec.servers.length > 0) {
                const server = spec.servers[0];
                if (server.url) {
                    urlTemplate = server.url;
                    const varMatch = urlTemplate.match(/\{([^}]*school[^}]*year[^}]*)\}/i);
                    if (varMatch && varMatch[1]) {
                        schoolYearVarName = varMatch[1];
                    }
                }
            }

            // Replace the variable with the selected year
            const newUrl = urlTemplate.replace(`{${schoolYearVarName}}`, selectedYear);
            serverSelector.value = newUrl;

            // Trigger change event to update Swagger UI
            const event = new Event('change', { bubbles: true });
            serverSelector.dispatchEvent(event);
        }
    };

    // Update computed URL display
    const updateComputedUrl = () => {
        const computedUrlElement = document.querySelector('.computed-url-value');
        if (computedUrlElement) {
            // Get the URL template from spec
            const spec = window.ui.spec().toJS();
            let urlTemplate = `http://localhost:${dmsPort}/{schoolYear}/data`;
            let schoolYearVarName = 'schoolYear';

            if (spec && spec.servers && spec.servers.length > 0) {
                const server = spec.servers[0];
                if (server.url) {
                    urlTemplate = server.url;
                    const varMatch = urlTemplate.match(/\{([^}]*school[^}]*year[^}]*)\}/i);
                    if (varMatch && varMatch[1]) {
                        schoolYearVarName = varMatch[1];
                    }
                }
            }

            // Replace the variable with the selected year
            const computedUrl = urlTemplate.replace(`{${schoolYearVarName}}`, selectedYear);
            computedUrlElement.textContent = computedUrl;
        }
    };

    // Request interceptor to inject school year into API calls
    const requestInterceptor = (req) => {
        if (!selectedYear) {
            return req;
        }

        // Inject school year into the URL if not already present
        const yearPattern = /\/\d{4}\//;
        if (!yearPattern.test(req.url)) {
            // Insert year after the base URL
            req.url = req.url.replace(
                /(https?:\/\/[^\/]+)(\/.*)?/,
                `$1/${selectedYear}$2`
            );
        }

        // Also update OAuth token URL if present
        if (req.url.includes('/connect/token')) {
            if (!req.url.endsWith(`/${selectedYear}`)) {
                req.url = `${req.url}/${selectedYear}`;
            }
        }

        return req;
    };

    return {
        statePlugins: {
            spec: {
                wrapActions: {
                    updateSpec: (oriAction, system) => (...args) => {
                        // Call original action first
                        const result = oriAction(...args);

                        // Get the spec after Swagger processes it
                        let [spec] = args;

                        // If spec is a string, parse it
                        if (typeof spec === 'string') {
                            try {
                                spec = JSON.parse(spec);
                            } catch (e) {
                                console.error('Failed to parse spec:', e);
                                return result;
                            }
                        }

                        // Replace docker hostname with localhost in servers
                        if (spec && spec.servers && Array.isArray(spec.servers)) {
                            spec.servers = spec.servers.map(server => ({
                                ...server,
                                url: server.url.replace('dms-config-service', 'localhost')
                            }));
                        }

                        return result;
                    }
                }
            }
        },
        fn: {
            requestInterceptor
        },
        afterLoad: initialize
    };
};
