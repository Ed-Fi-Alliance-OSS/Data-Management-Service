#!/bin/bash
# Run the claim upload test script

echo "Running Claim Upload Authorization Debug Script"
echo "==============================================="

# Check if pwsh is available
if ! command -v pwsh &> /dev/null; then
    echo "PowerShell Core (pwsh) is not installed. Please install it first."
    exit 1
fi

# Parse arguments
SKIP_SETUP=""
VERBOSE=""

for arg in "$@"; do
    case $arg in
        --skip-setup)
            SKIP_SETUP="-SkipSetup"
            shift
            ;;
        --verbose)
            VERBOSE="-Verbose"
            shift
            ;;
        --help)
            echo "Usage: $0 [options]"
            echo "Options:"
            echo "  --skip-setup   Skip vendor/application setup (use after restart)"
            echo "  --verbose      Show detailed debug output"
            echo "  --help         Show this help message"
            exit 0
            ;;
    esac
done

# Run the PowerShell script
pwsh ./test-claim-upload.ps1 $SKIP_SETUP $VERBOSE