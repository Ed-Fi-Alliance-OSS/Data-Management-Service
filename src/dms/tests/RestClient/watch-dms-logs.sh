#!/bin/bash
# Watch DMS logs in real-time
# Run this in a separate terminal while running the claim upload test

echo "Watching DMS logs..."
echo "====================="
echo "Look for authorization-related messages like:"
echo "  - ClaimSetName"
echo "  - Authorization denied"
echo "  - Resource claims"
echo ""

# Check which container name to use
if docker ps | grep -q "dms-local-dms-1"; then
    CONTAINER="dms-local-dms-1"
elif docker ps | grep -q "dms-local_dms_1"; then
    CONTAINER="dms-local_dms_1"
else
    echo "Could not find DMS container. Available containers:"
    docker ps --format "table {{.Names}}\t{{.Image}}\t{{.Status}}"
    echo ""
    echo "Please specify container name:"
    read CONTAINER
fi

echo "Monitoring container: $CONTAINER"
echo "Press Ctrl+C to stop"
echo "---------------------"

# Follow logs and highlight important patterns
docker logs -f "$CONTAINER" 2>&1 | grep --line-buffered -E "(Authorization|Claim|403|students|Student|WARN|ERROR)" --color=always