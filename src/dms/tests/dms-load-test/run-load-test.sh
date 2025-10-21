#!/bin/bash
# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.

# DMS Load Test Runner
# This script runs load tests against a DMS instance with configurable profiles and phases

set -e
set -o pipefail

# Script directory
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
cd "$SCRIPT_DIR"

# Default values
PROFILE="dev"
PHASE="full"
RESULTS_DIR="results"
TIMESTAMP=$(date +%Y%m%d-%H%M%S)

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Function to display usage
usage() {
    echo "Usage: $0 [OPTIONS]"
    echo ""
    echo "Options:"
    echo "  --profile <name>    Load test profile (smoke, dev, staging, prod) [default: dev]"
    echo "  --phase <phase>     Test phase to run (load, readwrite, full) [default: full]"
    echo "  --help              Display this help message"
    echo ""
    echo "Examples:"
    echo "  $0                          # Run full test with dev profile"
    echo "  $0 --profile staging        # Run full test with staging profile"
    echo "  $0 --phase load             # Run only load phase with dev profile"
    echo "  $0 --profile prod --phase readwrite  # Run readwrite phase with prod profile"
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --profile)
            PROFILE="$2"
            shift 2
            ;;
        --phase)
            PHASE="$2"
            shift 2
            ;;
        --help)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            usage
            exit 1
            ;;
    esac
done

echo -e "${BLUE}🚀 DMS Load Test Runner${NC}"
echo "=========================="
echo ""

# Check if k6 is installed
if ! command -v k6 &> /dev/null; then
    echo -e "${RED}❌ k6 is not installed. Please install k6 first.${NC}"
    echo "Visit: https://k6.io/docs/getting-started/installation/"
    exit 1
fi

# Check if npm dependencies are installed
if [ ! -d "node_modules" ]; then
    echo -e "${YELLOW}📦 Installing npm dependencies...${NC}"
    npm install
fi

# Create results directory
mkdir -p "$RESULTS_DIR"

# Function to load profile configuration
load_profile() {
    local profile_name=$1
    local profile_file=".env.profiles/${profile_name}.env"
    
    # Check if profile exists
    if [ ! -f "$profile_file" ]; then
        echo -e "${YELLOW}⚠️  Profile '${profile_name}' not found. Using default configuration.${NC}"
        return
    fi
    
    echo -e "${GREEN}📋 Loading profile: ${profile_name}${NC}"
    
    # Export profile variables (these override .env.load-test)
    set -a
    source "$profile_file"
    set +a
}

# Load base environment variables from .env
if [ -f ".env" ]; then
    echo -e "${GREEN}📋 Loading base configuration from .env${NC}"
    export $(grep -v '^#' .env | grep -v '^/\*' | grep -v '^\*' | xargs)
else
    echo -e "${RED}❌ .env not found${NC}"
    exit 1
fi

# Check if .env.load-test exists, if not create a client
if [ ! -f ".env.load-test" ]; then
    echo -e "${YELLOW}🔧 No load test client found. Setting up authorized client...${NC}"
    node src/utils/setupLoadTestClient.js
    if [ $? -ne 0 ]; then
        echo -e "${RED}❌ Failed to set up load test client${NC}"
        exit 1
    fi
    echo ""
fi

# Load client credentials from .env.load-test (overrides CLIENT_ID and CLIENT_SECRET from .env)
if [ -f ".env.load-test" ]; then
    echo -e "${GREEN}📋 Loading client credentials from .env.load-test${NC}"
    # Only export CLIENT_ID and CLIENT_SECRET from .env.load-test
    while IFS='=' read -r key value; do
        export "$key=$value"
    done < <(grep -E '^(CLIENT_ID|CLIENT_SECRET)=' .env.load-test)
else
    echo -e "${RED}❌ .env.load-test not found after setup${NC}"
    exit 1
fi

# Check if credentials are valid
echo -e "${BLUE}🔐 Checking client credentials...${NC}"
set +e  # Temporarily disable error exit
node src/utils/validateCredentials.js
CRED_CHECK_RESULT=$?
set -e  # Re-enable error exit
if [ $CRED_CHECK_RESULT -ne 0 ]; then
    echo -e "${YELLOW}⚠️  Client credentials are invalid or expired. Refreshing...${NC}"
    
    # Remove the old credentials file
    rm -f .env.load-test
    
    # Generate new credentials
    node src/utils/setupLoadTestClient.js
    if [ $? -ne 0 ]; then
        echo -e "${RED}❌ Failed to refresh client credentials${NC}"
        exit 1
    fi
    
    # Reload the new credentials
    echo -e "${GREEN}📋 Loading refreshed client credentials from .env.load-test${NC}"
    while IFS='=' read -r key value; do
        export "$key=$value"
    done < <(grep -E '^(CLIENT_ID|CLIENT_SECRET)=' .env.load-test)
    
    # Check credentials one more time
    echo -e "${BLUE}🔐 Verifying refreshed credentials...${NC}"
    node src/utils/validateCredentials.js > /dev/null 2>&1
    if [ $? -ne 0 ]; then
        echo -e "${RED}❌ Failed to authenticate with refreshed credentials${NC}"
        echo -e "${RED}Please check your OAuth configuration and try again${NC}"
        exit 1
    fi
fi
echo -e "${GREEN}✅ Client credentials validated${NC}"

# Load profile configuration (overrides base config)
load_profile "$PROFILE"

# Display configuration
echo ""
echo -e "${BLUE}Configuration:${NC}"
echo "  Profile: $PROFILE"
echo "  Phase: $PHASE"
echo "  API URL: $API_BASE_URL"
echo "  Client: $CLIENT_ID"
echo ""
echo -e "${BLUE}Test Scale:${NC}"
echo "  Schools: $SCHOOL_COUNT"
echo "  Students: $STUDENT_COUNT"
echo "  Staff: $STAFF_COUNT"
echo "  Courses per school: $COURSES_PER_SCHOOL"
echo "  Sections per course: $SECTIONS_PER_COURSE"
echo ""
echo -e "${BLUE}Load Test Parameters:${NC}"
echo "  Load Phase: $VUS_LOAD_PHASE VUs for $DURATION_LOAD_PHASE"
echo "  Read-Write Phase: $VUS_READWRITE_PHASE VUs for $DURATION_READWRITE_PHASE"
echo ""

# Function to run smoke test
run_smoke_test() {
    echo -e "${BLUE}🔧 Running smoke test to verify connectivity...${NC}"
    k6 run \
        --quiet \
        --out json="$RESULTS_DIR/smoke-$TIMESTAMP.json" \
        src/scenarios/smoke.js
    
    if [ $? -ne 0 ]; then
        echo -e "${RED}❌ Smoke test failed. Please check your configuration.${NC}"
        exit 1
    fi
    echo -e "${GREEN}✅ Smoke test passed!${NC}"
    echo ""
}

# Function to run load phase
run_load_phase() {
    echo -e "${BLUE}📊 Phase 1: Load Test (Creating Resources)${NC}"
    echo "----------------------------------------"
    echo "This phase will create resources following Ed-Fi dependencies."
    echo ""
    
    k6 run \
        --out json="$RESULTS_DIR/load-$PROFILE-$TIMESTAMP.json" \
        src/scenarios/load.js
    
    if [ $? -ne 0 ]; then
        echo -e "${YELLOW}⚠️  Load phase encountered errors. Check $RESULTS_DIR/load-$PROFILE-$TIMESTAMP.json${NC}"
        return 1
    fi
    
    echo -e "${GREEN}✅ Load phase completed!${NC}"
    echo ""
    return 0
}

# Function to run readwrite phase
run_readwrite_phase() {
    echo -e "${BLUE}🔄 Phase 2: Read-Write Test (Mixed CRUD Operations)${NC}"
    echo "-------------------------------------------------"
    echo "This phase performs mixed CRUD operations on existing data."
    echo ""
    
    k6 run \
        --out json="$RESULTS_DIR/readwrite-$PROFILE-$TIMESTAMP.json" \
        src/scenarios/readwrite.js
    
    if [ $? -ne 0 ]; then
        echo -e "${YELLOW}⚠️  Read-write phase encountered errors. Check $RESULTS_DIR/readwrite-$PROFILE-$TIMESTAMP.json${NC}"
        return 1
    fi
    
    echo -e "${GREEN}✅ Read-write phase completed!${NC}"
    echo ""
    return 0
}

# Main execution
echo -e "${YELLOW}Starting $PHASE test with $PROFILE profile...${NC}"
echo ""

# Always run smoke test first
run_smoke_test

# Run requested phases
case $PHASE in
    load)
        run_load_phase
        ;;
    readwrite)
        run_readwrite_phase
        ;;
    full)
        run_load_phase
        if [ $? -eq 0 ]; then
            sleep 5
            run_readwrite_phase
        fi
        ;;
    *)
        echo -e "${RED}Unknown phase: $PHASE${NC}"
        usage
        exit 1
        ;;
esac

# Summary
echo ""
echo -e "${GREEN}🎉 Load test completed!${NC}"
echo -e "${BLUE}📁 Results saved in: $RESULTS_DIR/*-$PROFILE-$TIMESTAMP.json${NC}"
echo ""
echo "To analyze results:"
echo "  - Use k6's cloud service: k6 cloud $RESULTS_DIR/load-$PROFILE-$TIMESTAMP.json"
echo "  - Convert to HTML: k6 convert $RESULTS_DIR/load-$PROFILE-$TIMESTAMP.json --output $RESULTS_DIR/report.html"
echo ""