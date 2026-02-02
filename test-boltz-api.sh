#!/bin/bash
# test-boltz-api.sh - Test Boltz Greenfield API endpoints
# Usage: ./test-boltz-api.sh [API_KEY] [STORE_ID]
#
# Environment variables:
#   BTCPAY_URL - Base URL of BTCPay Server (default: http://localhost:14142)
#   API_KEY    - API key with btcpay.store.canmodifystoresettings permission
#   STORE_ID   - Store ID to test against

set -e

# Configuration - adjust these for your setup
BTCPAY_URL="${BTCPAY_URL:-http://localhost:14142}"
API_KEY="${1:-${API_KEY:-YOUR_API_KEY}}"
STORE_ID="${2:-${STORE_ID:-YOUR_STORE_ID}}"

# Colors for output
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

echo_step() { echo -e "\n${YELLOW}>>> $1${NC}"; }
echo_info() { echo -e "${BLUE}    $1${NC}"; }
echo_ok() { echo -e "${GREEN}    OK${NC}"; }
echo_fail() { echo -e "${RED}    FAILED${NC}"; }

# Validate inputs
if [ "$API_KEY" = "YOUR_API_KEY" ] || [ "$STORE_ID" = "YOUR_STORE_ID" ]; then
    echo -e "${RED}Error: Please provide API_KEY and STORE_ID${NC}"
    echo ""
    echo "Usage: $0 <API_KEY> <STORE_ID>"
    echo ""
    echo "Or set environment variables:"
    echo "  export BTCPAY_URL='http://localhost:14142'"
    echo "  export API_KEY='your-api-key'"
    echo "  export STORE_ID='your-store-id'"
    echo "  $0"
    exit 1
fi

BASE_URL="${BTCPAY_URL}/api/v1/stores/${STORE_ID}/boltz"
AUTH_HEADER="Authorization: token ${API_KEY}"

echo -e "${BLUE}==========================================${NC}"
echo -e "${BLUE}  Boltz Greenfield API Test Suite${NC}"
echo -e "${BLUE}==========================================${NC}"
echo_info "BTCPay URL: ${BTCPAY_URL}"
echo_info "Store ID: ${STORE_ID}"
echo ""

# Track created resources for cleanup
CREATED_WALLET_ID=""
IMPORTED_WALLET_ID=""

cleanup() {
    echo_step "Cleanup: Removing test resources..."
    
    # Disable Boltz first (so wallets can be deleted)
    curl -s -X DELETE "${BASE_URL}/setup" \
        -H "${AUTH_HEADER}" \
        -H "Content-Type: application/json" > /dev/null 2>&1 || true
    
    # Remove created wallet
    if [ -n "$CREATED_WALLET_ID" ]; then
        echo_info "Removing wallet ID: $CREATED_WALLET_ID"
        curl -s -X DELETE "${BASE_URL}/wallets/${CREATED_WALLET_ID}" \
            -H "${AUTH_HEADER}" \
            -H "Content-Type: application/json" > /dev/null 2>&1 || true
    fi
    
    # Remove imported wallet
    if [ -n "$IMPORTED_WALLET_ID" ]; then
        echo_info "Removing wallet ID: $IMPORTED_WALLET_ID"
        curl -s -X DELETE "${BASE_URL}/wallets/${IMPORTED_WALLET_ID}" \
            -H "${AUTH_HEADER}" \
            -H "Content-Type: application/json" > /dev/null 2>&1 || true
    fi
    
    echo_ok
}

# Set trap for cleanup on exit
trap cleanup EXIT

# 1. Get current setup status
echo_step "1. GET /boltz/setup - Check current status"
RESPONSE=$(curl -s -w "\n%{http_code}" -X GET "${BASE_URL}/setup" \
    -H "${AUTH_HEADER}" \
    -H "Content-Type: application/json")
HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | sed '$d')
echo "$BODY" | jq .
if [ "$HTTP_CODE" = "200" ]; then
    echo_ok
else
    echo_fail
    echo "HTTP $HTTP_CODE"
fi

# 2. Create a new wallet
echo_step "2. POST /boltz/wallets - Create new L-BTC wallet"
WALLET_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "${BASE_URL}/wallets" \
    -H "${AUTH_HEADER}" \
    -H "Content-Type: application/json" \
    -d '{
        "name": "test-api-wallet",
        "currency": "LBTC"
    }')
HTTP_CODE=$(echo "$WALLET_RESPONSE" | tail -n1)
BODY=$(echo "$WALLET_RESPONSE" | sed '$d')
echo "$BODY" | jq .
if [ "$HTTP_CODE" = "200" ]; then
    CREATED_WALLET_ID=$(echo "$BODY" | jq -r '.id // empty')
    echo_info "Created wallet ID: $CREATED_WALLET_ID"
    # Check if mnemonic was returned
    MNEMONIC=$(echo "$BODY" | jq -r '.mnemonic // empty')
    if [ -n "$MNEMONIC" ]; then
        echo_info "Mnemonic returned (backup this!): ${MNEMONIC:0:20}..."
    fi
    echo_ok
else
    echo_fail
    echo "HTTP $HTTP_CODE"
fi

# 3. List wallets
echo_step "3. GET /boltz/wallets - List all wallets"
RESPONSE=$(curl -s -w "\n%{http_code}" -X GET "${BASE_URL}/wallets" \
    -H "${AUTH_HEADER}" \
    -H "Content-Type: application/json")
HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | sed '$d')
echo "$BODY" | jq .
if [ "$HTTP_CODE" = "200" ]; then
    WALLET_COUNT=$(echo "$BODY" | jq 'length')
    echo_info "Found $WALLET_COUNT wallet(s)"
    echo_ok
else
    echo_fail
    echo "HTTP $HTTP_CODE"
fi

# 4. Enable Boltz with the created wallet
echo_step "4. POST /boltz/setup - Enable Boltz with wallet"
RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "${BASE_URL}/setup" \
    -H "${AUTH_HEADER}" \
    -H "Content-Type: application/json" \
    -d '{
        "walletName": "test-api-wallet"
    }')
HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | sed '$d')
echo "$BODY" | jq .
if [ "$HTTP_CODE" = "200" ]; then
    ENABLED=$(echo "$BODY" | jq -r '.enabled')
    MODE=$(echo "$BODY" | jq -r '.mode')
    echo_info "Enabled: $ENABLED, Mode: $MODE"
    echo_ok
else
    echo_fail
    echo "HTTP $HTTP_CODE"
fi

# 5. Verify setup
echo_step "5. GET /boltz/setup - Verify enabled"
RESPONSE=$(curl -s -w "\n%{http_code}" -X GET "${BASE_URL}/setup" \
    -H "${AUTH_HEADER}" \
    -H "Content-Type: application/json")
HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | sed '$d')
echo "$BODY" | jq .
if [ "$HTTP_CODE" = "200" ]; then
    ENABLED=$(echo "$BODY" | jq -r '.enabled')
    if [ "$ENABLED" = "true" ]; then
        echo_ok
    else
        echo_fail
        echo "Expected enabled=true"
    fi
else
    echo_fail
    echo "HTTP $HTTP_CODE"
fi

# 6. Import wallet with mnemonic
echo_step "6. POST /boltz/wallets - Import wallet with mnemonic"
RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "${BASE_URL}/wallets" \
    -H "${AUTH_HEADER}" \
    -H "Content-Type: application/json" \
    -d '{
        "name": "test-imported-wallet",
        "currency": "LBTC",
        "mnemonic": "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about"
    }')
HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | sed '$d')
echo "$BODY" | jq .
if [ "$HTTP_CODE" = "200" ]; then
    IMPORTED_WALLET_ID=$(echo "$BODY" | jq -r '.id // empty')
    echo_info "Imported wallet ID: $IMPORTED_WALLET_ID"
    echo_ok
elif [ "$HTTP_CODE" = "403" ]; then
    echo_info "Mnemonic import disabled on this server (expected in some configurations)"
    echo_ok
else
    echo_fail
    echo "HTTP $HTTP_CODE"
fi

# 7. Try to delete wallet in use (should fail)
echo_step "7. DELETE /boltz/wallets/{id} - Try delete wallet in use (expect 400)"
if [ -n "$CREATED_WALLET_ID" ]; then
    RESPONSE=$(curl -s -w "\n%{http_code}" -X DELETE "${BASE_URL}/wallets/${CREATED_WALLET_ID}" \
        -H "${AUTH_HEADER}" \
        -H "Content-Type: application/json")
    HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
    BODY=$(echo "$RESPONSE" | sed '$d')
    echo "$BODY" | jq .
    if [ "$HTTP_CODE" = "400" ]; then
        ERROR_CODE=$(echo "$BODY" | jq -r '.code // empty')
        if [ "$ERROR_CODE" = "wallet-in-use" ]; then
            echo_info "Correctly rejected deletion of in-use wallet"
            echo_ok
        else
            echo_fail
            echo "Expected error code 'wallet-in-use', got '$ERROR_CODE'"
        fi
    else
        echo_fail
        echo "Expected HTTP 400, got $HTTP_CODE"
    fi
else
    echo_info "Skipped - no wallet was created"
fi

# 8. Disable Boltz
echo_step "8. DELETE /boltz/setup - Disable Boltz"
RESPONSE=$(curl -s -w "\n%{http_code}" -X DELETE "${BASE_URL}/setup" \
    -H "${AUTH_HEADER}" \
    -H "Content-Type: application/json")
HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
BODY=$(echo "$RESPONSE" | sed '$d')
echo "$BODY" | jq .
if [ "$HTTP_CODE" = "200" ]; then
    ENABLED=$(echo "$BODY" | jq -r '.enabled')
    if [ "$ENABLED" = "false" ]; then
        echo_ok
    else
        echo_fail
        echo "Expected enabled=false"
    fi
else
    echo_fail
    echo "HTTP $HTTP_CODE"
fi

# 9. Now delete the wallet (should succeed)
echo_step "9. DELETE /boltz/wallets/{id} - Delete wallet after disable"
if [ -n "$CREATED_WALLET_ID" ]; then
    RESPONSE=$(curl -s -w "\n%{http_code}" -X DELETE "${BASE_URL}/wallets/${CREATED_WALLET_ID}" \
        -H "${AUTH_HEADER}" \
        -H "Content-Type: application/json")
    HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
    if [ "$HTTP_CODE" = "200" ]; then
        echo_info "Wallet deleted successfully"
        CREATED_WALLET_ID=""  # Clear so cleanup doesn't try again
        echo_ok
    else
        BODY=$(echo "$RESPONSE" | sed '$d')
        echo "$BODY" | jq .
        echo_fail
        echo "HTTP $HTTP_CODE"
    fi
else
    echo_info "Skipped - no wallet was created"
fi

echo ""
echo -e "${GREEN}==========================================${NC}"
echo -e "${GREEN}  All tests completed!${NC}"
echo -e "${GREEN}==========================================${NC}"
