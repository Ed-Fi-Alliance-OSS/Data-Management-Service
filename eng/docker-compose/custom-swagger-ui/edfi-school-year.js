// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

window.EdFiSchoolYear = function () {
    let selectedYear = null;
    let schoolYears = [];

    // Load school years configuration
    const loadSchoolYears = async () => {
        try {
            const response = await fetch('school-years.json');
            const config = await response.json();
            schoolYears = config.Years || [];

            // Find and set the default year
            const defaultYear = schoolYears.find(y => y.IsDefault);
            if (defaultYear) {
                selectedYear = defaultYear.Year;
            } else if (schoolYears.length > 0) {
                selectedYear = schoolYears[0].Year;
            }

            console.log('School years loaded:', schoolYears);
            console.log('Default year:', selectedYear);
        } catch (error) {
            console.error('Failed to load school years:', error);
        }
    };

    // Inject the School Year selector into the DOM
    const injectSelector = () => {
        if (schoolYears.length === 0) {
            console.log('School Year selector not injected - no years configured');
            return;
        }

        // Wait for the auth-wrapper to be available
        const checkAndInject = () => {
            // Find the auth wrapper to insert after
            const wrapper = document.querySelector('.auth-wrapper');

            if (wrapper && !document.querySelector('.school-year-wrapper')) {
                console.log('Injecting School Year selector...');
                // Create a container for our selector
                const container = document.createElement('div');
                container.className = 'school-year-wrapper';
                container.style.cssText = 'padding: 10px 0; display: flex; flex-direction: column; width: 100%; clear: both;';

                // Insert before the API Section selector
                wrapper.parentNode.insertBefore(container, wrapper.nextSibling);

                // Create the Computed URL line
                const computedUrlDiv = document.createElement('div');
                computedUrlDiv.id = 'computed-url-display';
                computedUrlDiv.style.cssText = 'font-family: sans-serif; font-size: 14px; color: #3b4151; margin-bottom: 10px; margin-top: 10px;';
                const dmsPort = window.DMS_HTTP_PORTS || "8080";
                computedUrlDiv.textContent = `Computed URL: http://localhost:${dmsPort}/${selectedYear}/data`;

                // Create the Server variables line (bold)
                const serverVarsDiv = document.createElement('div');
                serverVarsDiv.style.cssText = 'font-family: sans-serif; font-size: 16px; font-weight: bold; color: #3b4151; margin-bottom: 10px; margin-top: 10px;';
                serverVarsDiv.textContent = 'Server variables';

                // Create a container for the School Year selector (label + select on same line)
                const selectorRow = document.createElement('div');
                selectorRow.style.cssText = 'display: flex; align-items: center;';

                // Create the selector HTML directly
                const schoolYearLabel = document.createElement('label');
                schoolYearLabel.className = 'select-label';
                schoolYearLabel.style.cssText = 'font-family: sans-serif; font-size: 14px; font-weight: normal; color: #0e0d0dff; margin-right: 10px; margin-top: 10px;';
                const labelSpan = document.createElement('span');
                labelSpan.textContent = 'School Year:';
                schoolYearLabel.appendChild(labelSpan);

                const schoolYearSelect = document.createElement('select');
                schoolYearSelect.className = 'school-year-select';
                schoolYearSelect.id = 'schoolYearSelect';
                schoolYearSelect.style.cssText = 'padding: 5px 40px 5px 10px; font-size: 14px; border: 2px solid #41444e; border-radius: 4px; background-color: #fff; cursor: pointer; outline: none; text-align: left; text-align-last: left; width: auto; min-width: 100px;';

                schoolYears.forEach(year => {
                    const option = document.createElement('option');
                    option.value = year.Year;
                    option.textContent = year.Year;
                    option.style.cssText = 'text-align: left;';
                    if (year.IsDefault) {
                        option.selected = true;
                    }
                    schoolYearSelect.appendChild(option);
                });

                // Function to update computed URL
                const updateComputedUrl = () => {
                    const urlDisplay = document.getElementById('computed-url-display');
                    if (urlDisplay) {
                        urlDisplay.textContent = `Computed URL: http://localhost:${dmsPort}/${selectedYear}/data`;
                    }
                };

                schoolYearSelect.addEventListener('change', (e) => {
                    selectedYear = e.target.value;
                    console.log('School year changed to:', selectedYear);
                    updateComputedUrl();
                    updateServerUrlDisplay();
                });

                selectorRow.appendChild(schoolYearLabel);
                selectorRow.appendChild(schoolYearSelect);

                container.appendChild(computedUrlDiv);
                container.appendChild(serverVarsDiv);
                container.appendChild(selectorRow);

                console.log('School Year selector injected');
            }
        };

        // Try multiple times to inject
        let attempts = 0;
        const intervalId = setInterval(() => {
            checkAndInject();
            if (++attempts > 20 || document.querySelector('.school-year-wrapper')) {
                clearInterval(intervalId);
            }
        }, 300);

        // Also set up a MutationObserver to re-inject if the element is removed
        const observer = new MutationObserver((mutations) => {
            if (!document.querySelector('.school-year-wrapper')) {
                console.log('School Year selector removed, re-injecting...');
                checkAndInject();
            }
        });

        // Observe the body for changes
        observer.observe(document.body, {
            childList: true,
            subtree: true
        });
    };

    // Function to update the server URL display in the Swagger UI
    const updateServerUrlDisplay = () => {
        // Wait for Swagger UI to render the server selector
        const checkAndUpdate = () => {
            const serverSelect = document.querySelector('#servers');
            if (serverSelect && serverSelect.options.length > 0) {
                const dmsPort = window.DMS_HTTP_PORTS || "8080";
                // Update all options to show the dynamic URL with {schoolYear}
                for (let i = 0; i < serverSelect.options.length; i++) {
                    const option = serverSelect.options[i];
                    // Update the display text to show {schoolYear} placeholder
                    option.textContent = `http://localhost:${dmsPort}/{schoolYear}/data`;
                    // Keep the original value for the actual requests (will be intercepted)
                    option.value = `http://localhost:${dmsPort}/data`;
                }
                console.log('Server URL display updated');
            }
        };

        // Try immediately and also after a delay
        checkAndUpdate();
        setTimeout(checkAndUpdate, 500);
        setTimeout(checkAndUpdate, 1000);
        setTimeout(checkAndUpdate, 2000);
    };

    // Get the currently selected school year
    const getSelectedYear = () => {
        return selectedYear;
    };

    // Plugin configuration
    return {
        afterLoad: (system) => {
            console.log('EdFiSchoolYear plugin loaded');
            loadSchoolYears().then(() => {
                injectSelector();
                updateServerUrlDisplay();
            });
        },
        statePlugins: {
            spec: {
                wrapActions: {
                    updateUrl: (oriAction, system) => (...args) => {
                        updateServerUrlDisplay();
                        return oriAction(...args);
                    },
                    updateSpec: (oriAction, system) => (spec) => {
                        // Intercept spec to replace dms-config-service with localhost
                        if (typeof spec === 'string') {
                            try {
                                // Replace all occurrences of dms-config-service with localhost
                                const modifiedSpec = spec.replace(
                                    /http:\/\/dms-config-service:(\d+)/g,
                                    'http://localhost:$1'
                                );
                                if (modifiedSpec !== spec) {
                                    console.log('Spec modified: replaced dms-config-service with localhost');
                                }
                                return oriAction(modifiedSpec);
                            } catch (e) {
                                console.error('Error modifying spec:', e);
                            }
                        }
                        return oriAction(spec);
                    }
                }
            }
        },
        fn: {
            requestInterceptor: (req) => {
                // Intercept all requests and inject the school year into the URL
                if (selectedYear && req.url) {
                    const dmsPort = window.DMS_HTTP_PORTS || "8080";

                    // FIRST: Replace dms-config-service with localhost for ALL requests
                    // This fixes CORS issues since browsers cannot resolve docker internal hostnames
                    if (req.url.includes('dms-config-service')) {
                        req.url = req.url.replace(
                            /http:\/\/dms-config-service:(\d+)/g,
                            'http://localhost:$1'
                        );
                        console.log('Hostname replaced in request URL:', req.url);
                    }

                    // THEN: Check if this is a DMS API request (not metadata or spec files)
                    if (req.url.includes('/data/') && !req.url.includes('/metadata/')) {
                        // Replace /data/ with /{schoolYear}/data/
                        req.url = req.url.replace(
                            /\/data\//,
                            `/${selectedYear}/data/`
                        );
                        console.log('School year added to data request:', req.url);
                    }

                    // FINALLY: Intercept OAuth token requests and add school year
                    if (req.url.includes('/connect/token')) {
                        // Add school year to the path if not already present
                        if (!req.url.match(/\/connect\/token\/\d{4}/)) {
                            req.url = req.url.replace(
                                /\/connect\/token$/,
                                `/connect/token/${selectedYear}`
                            );
                        }
                        console.log('Token request final URL:', req.url);
                    }
                }
                return req;
            }
        }
    };
};
