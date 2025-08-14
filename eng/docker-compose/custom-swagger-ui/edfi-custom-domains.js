// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

window.EdFiCustomDomains = function () {

    // Helper function to safely get values from schema
    const safeGet = (schema, key) => {
        if (!schema) {
            return undefined;
        }
        if (typeof schema.get === 'function') {
            return schema.get(key);
        }
        // Fallback for plain objects
        return schema[key];
    };

    return {
        wrapComponents: {

            // Store domain information when processing OperationTag
            OperationTag: (Original, system) => {
                return function OperationTagWrapper(props) {
                    const React = system.React || window.React;
                    if (!React) {
                        return React.createElement(Original, props);
                    }

                    // Get tag name and find domains, store them correctly by tag
                    const tagName = props.tag;

                    if (tagName && system.getSystem) {
                        try {
                            const specSelectors = system.getSystem().specSelectors;
                            if (specSelectors) {
                                const spec = specSelectors.spec();
                                if (spec) {
                                    const paths = spec.get ? spec.get('paths') : spec.paths;
                                    if (paths && typeof paths.entrySeq === 'function') {
                                        paths.entrySeq().forEach(([pathKey, pathValue]) => {
                                            if (pathKey.toLowerCase().includes(`/ed-fi/${tagName.toLowerCase()}`)) {
                                                const pathDomains = safeGet(pathValue, "x-Ed-Fi-domains");
                                                if (pathDomains) {
                                                    // Store domains by specific tag name
                                                    if (!window.__edfiDomainsByTag) {
                                                        window.__edfiDomainsByTag = {};
                                                    }
                                                    window.__edfiDomainsByTag[tagName] = pathDomains;
                                                }
                                            }
                                            /*
                                            if (pathKey.toLowerCase().includes(`/tpdm/${tagName.toLowerCase()}`)) {
                                                const pathDomains = safeGet(pathValue, "x-Ed-Fi-domains");
                                                if (pathDomains) {
                                                    // Store domains by specific tag name
                                                    if (!window.__edfiDomainsByTag) {
                                                        window.__edfiDomainsByTag = {};
                                                    }
                                                    window.__edfiDomainsByTag[tagName] = pathDomains;
                                                }
                                            }
                                            */
                                        });
                                    }
                                }
                            }
                        } catch (specError) {
                            console.warn('Error accessing spec for domains:', specError);
                        }
                    }

                    return React.createElement(Original, props);
                };
            },

            // Intercept Markdown component that renders description
            Markdown: (Original, system) => {
                return function MarkdownWrapper(props) {
                    const React = system.React || window.React;
                    if (!React) {
                        return React.createElement(Original, props);
                    }

                    if (
                        props.source &&
                        typeof props.source === 'string' &&
                        //props.source.length > 30 &&
                        //props.source.length < 500 &&
                        !props.source.includes('Note:') &&
                        !props.source.includes('Consumers of DMS') &&
                        !props.source.includes('safeguards') &&
                        window.__edfiDomainsByTag
                    ) {
                        let matchingDomains = null;
                        const availableTags = Object.keys(window.__edfiDomainsByTag);
                        if (availableTags.length > 0) {
                            const tag = availableTags[0];
                            matchingDomains = window.__edfiDomainsByTag[tag];
                            delete window.__edfiDomainsByTag[tag];
                        }

                        if (matchingDomains) {
                            const domainsArray = matchingDomains && typeof matchingDomains.toArray === 'function'
                                ? matchingDomains.toArray()
                                : matchingDomains;

                            const firstDomain = domainsArray[0];
                            const remainingCount = domainsArray.length - 1;

                            // Tooltip
                            const tooltip = remainingCount > 0
                                ? React.createElement(
                                    "span",
                                    {
                                        style: {
                                            backgroundColor: "#263238",
                                            color: "white",
                                            padding: "10px 14px",
                                            borderRadius: "8px",
                                            fontSize: "14px",
                                            fontWeight: "500",
                                            whiteSpace: "normal",
                                            boxShadow: "0 4px 12px rgba(0, 0, 0, 0.2)",
                                            position: "absolute",
                                            top: "-80px",
                                            left: "0px",
                                            display: "none",
                                            zIndex: 1000,
                                            maxWidth: "400px",
                                            lineHeight: "1.3",
                                            pointerEvents: "none"
                                        },
                                        className: "large-domain-tooltip"
                                    },
                                    `Domains: ${domainsArray.join(", ")}`
                                )
                                : null;

                            //`${descriptionDomain.length == 0 ? "" : descriptionDomain} ${firstDomain}${remainingCount > 0 ? ` +${remainingCount}` : ""}`,
                            let descriptionDomain;
                            if (domainsArray.length === 0) {
                                descriptionDomain = "";
                            } else if (domainsArray.length === 1) {
                                descriptionDomain = `Domain: ${firstDomain}`;
                            } else {
                                descriptionDomain = `Domains: ${firstDomain}${remainingCount > 0 ? ` +${remainingCount}` : ""}`;
                            }

                            const domainElement = React.createElement(
                                "span",
                                {
                                    style: {
                                        color: "#1976d2",
                                        fontWeight: "500",
                                        marginLeft: "6px",
                                        cursor: remainingCount > 0 ? "pointer" : "default",
                                        position: "relative",
                                        display: "inline"
                                    },
                                    onMouseEnter: function (e) {
                                        if (remainingCount > 0) {
                                            const tt = e.target.querySelector(".large-domain-tooltip");
                                            if (tt) {
                                                tt.style.display = "block";
                                            }
                                        }
                                    },
                                    onMouseLeave: function (e) {
                                        if (remainingCount > 0) {
                                            const tt = e.target.querySelector(".large-domain-tooltip");
                                            if (tt) {
                                                tt.style.display = "none";
                                            }
                                        }
                                    }
                                },
                                descriptionDomain,
                                tooltip
                            );

                            return React.createElement(
                                "span",
                                { style: { display: "inline" } },
                                React.createElement("span", { style: { display: "inline" } }, props.source),
                                " ",
                                domainElement
                            );
                        }
                    }

                    return React.createElement(Original, props);
                };
            },
        }
    }
};
