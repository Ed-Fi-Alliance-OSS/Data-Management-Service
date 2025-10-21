import http from 'k6/http';
import { check } from 'k6';

export class DependencyResolver {
    constructor(apiBaseUrl, authManager) {
        this.apiBaseUrl = apiBaseUrl;
        this.authManager = authManager;
        this.dependencies = null;
        this.resourceOrder = null;
    }

    fetchDependencies() {
        if (this.dependencies) {
            return this.dependencies;
        }

        // Metadata endpoint is at the root API level, not under /data
        const metadataUrl = this.apiBaseUrl.replace('/api/data', '/api');
        const url = `${metadataUrl}/metadata/dependencies`;
        const headers = this.authManager.getAuthHeaders();

        console.log(`Fetching dependencies from: ${url}`);
        const response = http.get(url, { headers });

        const success = check(response, {
            'dependencies request successful': (r) => r.status === 200,
            'dependencies data received': (r) => r.body.length > 0,
        });

        if (!success) {
            console.error(`Failed to fetch dependencies: ${response.status} - ${response.body}`);
            throw new Error('Failed to fetch resource dependencies');
        }

        const responseData = response.json();
        
        // The response appears to be an object with numeric keys, each containing resource info
        // Let's parse it correctly
        this.dependencies = {};
        
        if (Array.isArray(responseData)) {
            // If it's an array, convert to object
            responseData.forEach((item, index) => {
                if (item && item.resource) {
                    // Remove /ed-fi/ prefix and convert to camelCase
                    const resourceName = item.resource.replace('/ed-fi/', '');
                    this.dependencies[resourceName] = item.order;
                }
            });
        } else if (typeof responseData === 'object') {
            // If it's an object with numeric keys
            for (const [key, value] of Object.entries(responseData)) {
                if (value && value.resource) {
                    // Remove /ed-fi/ prefix 
                    const resourceName = value.resource.replace('/ed-fi/', '');
                    this.dependencies[resourceName] = value.order;
                }
            }
        }
        
        console.log(`Dependencies structure:`, Object.keys(this.dependencies).slice(0, 5));
        console.log(`First few dependencies:`, Object.entries(this.dependencies).slice(0, 5));
        console.log(`Total dependencies parsed: ${Object.keys(this.dependencies).length}`);
        
        this.buildResourceOrder();
        return this.dependencies;
    }

    buildResourceOrder() {
        // Group resources by order
        const orderMap = new Map();
        
        for (const [resource, order] of Object.entries(this.dependencies)) {
            if (!orderMap.has(order)) {
                orderMap.set(order, []);
            }
            orderMap.get(order).push(resource);
        }

        // Sort by order number and create flat array
        this.resourceOrder = [];
        const sortedOrders = Array.from(orderMap.keys()).sort((a, b) => a - b);
        
        for (const order of sortedOrders) {
            this.resourceOrder.push(...orderMap.get(order));
        }

        console.log(`Resource load order established: ${this.resourceOrder.length} resources in ${sortedOrders.length} dependency levels`);
    }

    getResourceOrder() {
        if (!this.resourceOrder) {
            this.fetchDependencies();
        }
        return this.resourceOrder;
    }

    getResourcesAtOrder(orderLevel) {
        if (!this.dependencies) {
            this.fetchDependencies();
        }

        return Object.entries(this.dependencies)
            .filter(([_, order]) => order === orderLevel)
            .map(([resource, _]) => resource);
    }

    getDependencyOrder(resourceName) {
        if (!this.dependencies) {
            this.fetchDependencies();
        }
        return this.dependencies[resourceName];
    }

    // Filter resources by domain focus
    filterByDomains(domains) {
        const domainKeywords = {
            'enrollment': ['student', 'enrollment', 'section', 'studentsection', 'school', 'educationorganization'],
            'studentAcademicRecord': ['grade', 'gradebook', 'studentacademic', 'report', 'academicrecord'],
            'teachingAndLearning': ['course', 'section', 'class', 'learningstandard', 'learningobjective', 'staff', 'teacher'],
            'assessment': ['assessment', 'studentassessment', 'objectiveassessment'],
            'studentIdentification': ['student', 'studentidentification', 'studentdemographic', 'contact', 'parent']
        };

        const filteredResources = [];
        const order = this.getResourceOrder();
        
        console.log(`Total resources to filter: ${order.length}`);
        console.log(`First 10 resources:`, order.slice(0, 10));

        for (const resource of order) {
            const resourceLower = resource.toLowerCase();
            for (const domain of domains) {
                const keywords = domainKeywords[domain] || [];
                if (keywords.some(keyword => resourceLower.includes(keyword))) {
                    filteredResources.push(resource);
                    break;
                }
            }
        }

        // Always include descriptors as they're foundational
        const descriptors = order.filter(r => r.toLowerCase().includes('descriptor'));
        const combined = [...new Set([...descriptors, ...filteredResources])];

        console.log(`Filtered to ${combined.length} resources for domains: ${domains.join(', ')}`);
        console.log(`Descriptors found: ${descriptors.length}`);
        console.log(`Domain matches found: ${filteredResources.length}`);
        
        return combined;
    }
}