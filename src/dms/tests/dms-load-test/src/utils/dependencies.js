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

        const url = `${this.apiBaseUrl}/metadata/dependencies`;
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
        
        // Check if the response is wrapped in a 'dependencies' property
        if (responseData.dependencies) {
            this.dependencies = responseData.dependencies;
        } else {
            this.dependencies = responseData;
        }
        
        console.log(`Dependencies structure:`, Object.keys(this.dependencies).slice(0, 5));
        console.log(`First few dependencies:`, Object.entries(this.dependencies).slice(0, 5));
        
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