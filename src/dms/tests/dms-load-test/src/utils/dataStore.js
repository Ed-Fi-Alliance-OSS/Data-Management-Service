export class DataStore {
    constructor() {
        // Store resources by type
        this.resources = new Map();
        
        // Store resource references for relationships
        this.references = new Map();
        
        // Track counts by resource type
        this.counts = new Map();
        
        // Store frequently accessed resources for performance
        this.cache = {
            schools: [],
            students: [],
            staff: [],
            courses: [],
            sections: [],
            descriptors: new Map()
        };
    }

    addResource(resourceType, resource) {
        if (!this.resources.has(resourceType)) {
            this.resources.set(resourceType, []);
            this.counts.set(resourceType, 0);
        }

        this.resources.get(resourceType).push(resource);
        this.counts.set(resourceType, this.counts.get(resourceType) + 1);

        // Update cache for frequently accessed types
        this.updateCache(resourceType, resource);

        // Store reference if resource has an ID
        if (resource.id) {
            const key = `${resourceType}:${resource.id}`;
            this.references.set(key, resource);
        }

        return resource;
    }

    updateCache(resourceType, resource) {
        const cacheMap = {
            'schools': 'schools',
            'localEducationAgencies': 'schools',
            'students': 'students',
            'staffs': 'staff',
            'courses': 'courses',
            'sections': 'sections'
        };

        const cacheKey = cacheMap[resourceType];
        if (cacheKey && this.cache[cacheKey]) {
            this.cache[cacheKey].push(resource);
        }

        // Special handling for descriptors
        if (resourceType.includes('Descriptor')) {
            if (!this.cache.descriptors.has(resourceType)) {
                this.cache.descriptors.set(resourceType, []);
            }
            this.cache.descriptors.get(resourceType).push(resource);
        }
    }

    getResource(resourceType, index = null) {
        if (!this.resources.has(resourceType)) {
            return null;
        }

        const resources = this.resources.get(resourceType);
        
        if (index !== null) {
            return resources[index] || null;
        }

        // Return random resource if no index specified
        return resources[Math.floor(Math.random() * resources.length)];
    }

    getRandomSchool() {
        if (this.cache.schools.length === 0) {
            throw new Error('No schools available in data store');
        }
        return this.cache.schools[Math.floor(Math.random() * this.cache.schools.length)];
    }

    getRandomStudent() {
        if (this.cache.students.length === 0) {
            throw new Error('No students available in data store');
        }
        return this.cache.students[Math.floor(Math.random() * this.cache.students.length)];
    }

    getRandomStaff() {
        if (this.cache.staff.length === 0) {
            throw new Error('No staff available in data store');
        }
        return this.cache.staff[Math.floor(Math.random() * this.cache.staff.length)];
    }

    getRandomCourse() {
        if (this.cache.courses.length === 0) {
            throw new Error('No courses available in data store');
        }
        return this.cache.courses[Math.floor(Math.random() * this.cache.courses.length)];
    }

    getRandomSection() {
        if (this.cache.sections.length === 0) {
            throw new Error('No sections available in data store');
        }
        return this.cache.sections[Math.floor(Math.random() * this.cache.sections.length)];
    }

    getRandomDescriptor(descriptorType) {
        if (!this.cache.descriptors.has(descriptorType)) {
            throw new Error(`No descriptors of type ${descriptorType} available`);
        }
        
        const descriptors = this.cache.descriptors.get(descriptorType);
        return descriptors[Math.floor(Math.random() * descriptors.length)];
    }

    getAllResources(resourceType) {
        return this.resources.get(resourceType) || [];
    }

    getResourceCount(resourceType) {
        return this.counts.get(resourceType) || 0;
    }

    getTotalResourceCount() {
        let total = 0;
        for (const count of this.counts.values()) {
            total += count;
        }
        return total;
    }

    getResourceTypes() {
        return Array.from(this.resources.keys());
    }

    clear() {
        this.resources.clear();
        this.references.clear();
        this.counts.clear();
        this.cache = {
            schools: [],
            students: [],
            staff: [],
            courses: [],
            sections: [],
            descriptors: new Map()
        };
    }

    getSummary() {
        const summary = {
            totalResources: this.getTotalResourceCount(),
            resourceTypes: this.getResourceTypes().length,
            breakdown: {}
        };

        for (const [type, count] of this.counts.entries()) {
            summary.breakdown[type] = count;
        }

        return summary;
    }
}

// Global instance for k6 tests
export const dataStore = new DataStore();