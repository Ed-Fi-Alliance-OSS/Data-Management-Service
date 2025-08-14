// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

window.EdFiCustomDomainsBubbles = function () {

    // Helper function to safely get values from schema
    const safeGet = (schema, key) => {
        if (!schema) return undefined;
        if (typeof schema.get === 'function') {
            return schema.get(key);
        }
        // Fallback for plain objects
        return schema[key];
    };

    // Helper function to create domain bubbles with hover expand functionality
    const createDomainBubbles = (domains, system) => {
        const React = system.React || window.React;
        if (!React) {
            return null;
        }

        const bubbleStyle = {
            display: "inline-block",
            backgroundColor: "#e3f2fd",
            color: "#1976d2",
            padding: "4px 12px",
            margin: "4px 8px 4px 0",
            borderRadius: "16px",
            fontSize: "12px",
            fontWeight: "500",
            border: "1px solid #bbdefb",
            cursor: "pointer",
            transition: "all 0.2s ease-in-out",
            position: "relative",
            zIndex: "10"
        };

        const expandedStyle = {
            ...bubbleStyle,
            backgroundColor: "#1976d2",
            color: "white",
            boxShadow: "0 2px 8px rgba(25, 118, 210, 0.3)"
        };

        const containerStyle = {
            marginTop: "2px",
            marginBottom: "0px",
            paddingLeft: "0px",
            display: "inline-block"
        };

        // Handle domains with values
        if (Array.isArray(domains) && domains.length > 0) {
            const firstDomain = domains[0];
            const remainingCount = domains.length - 1;
            const allDomains = domains.join(", ");

            return React.createElement(
                "div",
                { style: containerStyle },
                React.createElement(
                    "span",
                    {
                        style: bubbleStyle,
                        onMouseEnter: function (e) {
                            Object.assign(e.target.style, expandedStyle);
                            if (remainingCount > 0) {
                                const tooltip = e.target.querySelector('.large-domain-tooltip');
                                if (tooltip) tooltip.style.display = 'block';
                            }
                        },
                        onMouseLeave: function (e) {
                            Object.assign(e.target.style, bubbleStyle);
                            const tooltip = e.target.querySelector('.large-domain-tooltip');
                            if (tooltip) tooltip.style.display = 'none';
                        }
                    },
                    firstDomain + (remainingCount > 0 ? ` +${remainingCount}` : ""),
                    // Tooltip for multiples domains
                    remainingCount > 0 ? React.createElement(
                        "div",
                        {
                            className: "large-domain-tooltip",
                            style: {
                                position: "absolute",
                                top: "-80px",
                                left: "0px",  // Align to left edge instead of center
                                transform: "none",  // Remove centering transform
                                backgroundColor: "#263238",
                                color: "white",
                                padding: "10px 14px",
                                borderRadius: "8px",
                                fontSize: "14px",
                                fontWeight: "500",
                                whiteSpace: "normal",
                                boxShadow: "0 4px 12px rgba(0, 0, 0, 0.2)",
                                zIndex: "1000",
                                pointerEvents: "none",
                                display: "none",
                                maxWidth: "400px",  // Smaller width to prevent cutoff
                                minWidth: "250px",
                                textAlign: "left",
                                lineHeight: "1.3",
                                wordBreak: "normal",
                                overflowWrap: "break-word"
                            }
                        },
                        `Domains: ${allDomains}`
                    ) : null
                )
            );
        }

        return null;
    };

    return {
        wrapComponents: {

            // Store domain information when processing OperationTag
            OperationTag: (Original, system) => {
                return function OperationTagWrapper(props) {
                    const React = system.React || window.React;
                    if (!React) return React.createElement(Original, props);

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
                                                    if (!window.__edfiDomainsByTag) window.__edfiDomainsByTag = {};
                                                    window.__edfiDomainsByTag[tagName] = pathDomains;
                                                }
                                            }
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
                    if (!React) return React.createElement(Original, props);

                    const originalElement = React.createElement(Original, props);

                    // Check if this is SPECIFICALLY a resource description and we have stored domains
                    if (props.source && typeof props.source === 'string' &&
                        props.source.length > 30 && props.source.length < 500 &&  // Reasonable description length
                        !props.source.includes('Note:') &&  // Exclude general notes
                        !props.source.includes('Consumers of DMS') &&  // Exclude DMS notes
                        !props.source.includes('safeguards') &&  // Exclude security notes
                        window.__edfiDomainsByTag) {

                        // Simple approach: take domains in order they were stored
                        let matchingDomains = null;
                        let matchingTag = null;

                        // Get the first available tag and its domains
                        const availableTags = Object.keys(window.__edfiDomainsByTag);
                        if (availableTags.length > 0) {
                            matchingTag = availableTags[0];  // Take first available
                            matchingDomains = window.__edfiDomainsByTag[matchingTag];
                            // Remove it so next description gets next tag
                            delete window.__edfiDomainsByTag[matchingTag];
                        }

                        if (matchingDomains) {
                            const domainsArray = matchingDomains && typeof matchingDomains.toArray === 'function' ?
                                matchingDomains.toArray() : matchingDomains;
                            const domainBubbles = createDomainBubbles(domainsArray, system);

                            if (domainBubbles) {
                                return React.createElement(
                                    'div',
                                    null,
                                    originalElement,
                                    React.createElement(
                                        'div',
                                        {
                                            style: {
                                                marginTop: '8px',
                                                marginBottom: '8px'
                                            }
                                        },
                                        domainBubbles
                                    )
                                );
                            }
                        }
                    }

                    return originalElement;
                };
            },

            // Wrapper for Col - intercept description area to add bubbles
            Col: (Original, system) => {
                return function ColWrapper(props) {
                    const React = system.React || window.React;
                    if (!React) return React.createElement(Original, props);

                    const originalElement = React.createElement(Original, props);

                    // Check if this Col contains a description in a small tag
                    if (props.children && React.isValidElement(props.children) &&
                        props.children.type === 'small') {

                        // Try to find domains by looking at current tag context
                        let domains = null;
                        let tagName = null;

                        if (system.getSystem) {
                            try {
                                const specSelectors = system.getSystem().specSelectors;
                                if (specSelectors) {
                                    const spec = specSelectors.spec();
                                    if (spec) {
                                        const paths = spec.get ? spec.get('paths') : spec.paths;
                                        if (paths && typeof paths.entrySeq === 'function') {
                                            // Search through all paths to find domains
                                            paths.entrySeq().forEach(([pathKey, pathValue]) => {
                                                if (!domains) {
                                                    const pathDomains = safeGet(pathValue, "x-Ed-Fi-domains");
                                                    if (pathDomains) {
                                                        // Extract tag name from path
                                                        const pathParts = pathKey.split('/');
                                                        const potentialTag = pathParts[pathParts.length - 1];

                                                        domains = pathDomains;
                                                        tagName = potentialTag;
                                                    }
                                                }
                                            });
                                        }
                                    }
                                }
                            } catch (specError) {
                                console.warn('Error accessing spec for domains:', specError);
                            }
                        }

                        if (domains) {
                            const domainsArray = domains && typeof domains.toArray === 'function' ?
                                domains.toArray() : domains;
                            const domainBubbles = createDomainBubbles(domainsArray, system);

                            if (domainBubbles) {
                                return React.createElement(
                                    'div',
                                    null,
                                    originalElement,
                                    React.createElement(
                                        'div',
                                        {
                                            style: {
                                                marginTop: '6px',
                                                marginBottom: '8px'
                                            }
                                        },
                                        domainBubbles
                                    )
                                );
                            }
                        }
                    }

                    return originalElement;
                };
            },
        }
    }
};
