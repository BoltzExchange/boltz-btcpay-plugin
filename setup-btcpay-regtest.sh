#!/bin/bash
# setup-btcpay-regtest.sh - Set up a BTCPay user, store, and API key from scratch
# Usage: ./setup-btcpay-regtest.sh [BTCPAY_URL]
#
# This script creates:
# 1. A new user account
# 2. A new store
# 3. An API key with required permissions for Boltz API testing
#
# Prerequisites:
# - BTCPay Server running in regtest mode
# - Server must allow new user registration OR you need admin credentials

set -e

# Configuration
BTCPAY_URL="${1:-${BTCPAY_URL:-http://localhost:14142}}"
USER_EMAIL="${USER_EMAIL:-regtest@bol.tz}"
USER_PASSWORD="${USER_PASSWORD:-boltz123}"
STORE_NAME="${STORE_NAME:-Boltz Test Store}"

# Colors for output
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m'

echo_step() { echo -e "\n${YELLOW}>>> $1${NC}"; }
echo_info() { echo -e "${BLUE}    $1${NC}"; }
echo_success() { echo -e "${GREEN}    ✓ $1${NC}"; }
echo_error() { echo -e "${RED}    ✗ $1${NC}"; }
echo_value() { echo -e "${CYAN}    $1${NC}"; }

# Check if jq is installed
if ! command -v jq &> /dev/null; then
    echo_error "jq is required but not installed. Please install jq first."
    exit 1
fi

echo -e "${BLUE}==========================================${NC}"
echo -e "${BLUE}  BTCPay Server Regtest Setup${NC}"
echo -e "${BLUE}==========================================${NC}"
echo_info "BTCPay URL: ${BTCPAY_URL}"
echo_info "User Email: ${USER_EMAIL}"
echo ""

# Step 1: Check server health
echo_step "1. Checking server health..."
HEALTH_RESPONSE=$(curl -s -w "\n%{http_code}" -X GET "${BTCPAY_URL}/api/v1/health" 2>/dev/null || echo -e "\n000")
HTTP_CODE=$(echo "$HEALTH_RESPONSE" | tail -n1)
if [ "$HTTP_CODE" != "200" ]; then
    echo_error "Server not reachable at ${BTCPAY_URL}"
    echo_info "Make sure BTCPay Server is running"
    exit 1
fi
echo_success "Server is healthy"

# Step 2: Check server info
echo_step "2. Getting server info..."
SERVER_INFO=$(curl -s -X GET "${BTCPAY_URL}/api/v1/server/info")
NETWORK=$(echo "$SERVER_INFO" | jq -r '.networkType // "unknown"')
echo_info "Network: ${NETWORK}"
if [ "$NETWORK" != "Regtest" ]; then
    echo -e "${YELLOW}    Warning: Server is not running in Regtest mode (found: ${NETWORK})${NC}"
fi

# Step 3: Create user account
echo_step "3. Creating user account..."
CREATE_USER_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "${BTCPAY_URL}/api/v1/users" \
    -H "Content-Type: application/json" \
    -d "{
        \"email\": \"${USER_EMAIL}\",
        \"password\": \"${USER_PASSWORD}\",
        \"isAdministrator\": true
    }")
HTTP_CODE=$(echo "$CREATE_USER_RESPONSE" | tail -n1)
BODY=$(echo "$CREATE_USER_RESPONSE" | sed '$d')

if [ "$HTTP_CODE" = "200" ] || [ "$HTTP_CODE" = "201" ]; then
    USER_ID=$(echo "$BODY" | jq -r '.id')
    echo_success "User created: ${USER_ID}"
elif [ "$HTTP_CODE" = "400" ] || [ "$HTTP_CODE" = "422" ]; then
    # User might already exist, try to proceed with login
    echo_info "User may already exist, attempting to continue..."
else
    echo_error "Failed to create user (HTTP ${HTTP_CODE})"
    echo "$BODY" | jq . 2>/dev/null || echo "$BODY"
    exit 1
fi

# Step 4: Get basic auth token (for initial API key creation)
echo_step "4. Authenticating..."
BASIC_AUTH=$(echo -n "${USER_EMAIL}:${USER_PASSWORD}" | base64)

# Step 5: Create API key with required permissions
echo_step "5. Creating API key..."
API_KEY_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "${BTCPAY_URL}/api/v1/api-keys" \
    -H "Content-Type: application/json" \
    -H "Authorization: Basic ${BASIC_AUTH}" \
    -d '{
        "label": "Boltz API Test Key",
        "permissions": [
            "btcpay.store.canmodifystoresettings",
            "btcpay.store.canviewstoresettings",
            "btcpay.store.cancreateinvoice",
            "btcpay.store.canviewinvoices",
            "btcpay.user.canmodifyprofile",
            "btcpay.user.canviewprofile",
            "unrestricted"
        ]
    }')
HTTP_CODE=$(echo "$API_KEY_RESPONSE" | tail -n1)
BODY=$(echo "$API_KEY_RESPONSE" | sed '$d')

if [ "$HTTP_CODE" = "200" ] || [ "$HTTP_CODE" = "201" ]; then
    API_KEY=$(echo "$BODY" | jq -r '.apiKey')
    echo_success "API key created"
else
    echo_error "Failed to create API key (HTTP ${HTTP_CODE})"
    echo "$BODY" | jq . 2>/dev/null || echo "$BODY"
    exit 1
fi

# Step 6: Create store
echo_step "6. Creating store..."
STORE_RESPONSE=$(curl -s -w "\n%{http_code}" -X POST "${BTCPAY_URL}/api/v1/stores" \
    -H "Content-Type: application/json" \
    -H "Authorization: token ${API_KEY}" \
    -d "{
        \"name\": \"${STORE_NAME}\",
        \"defaultCurrency\": \"BTC\",
        \"networkFeeMode\": \"MultiplePaymentsOnly\"
    }")
HTTP_CODE=$(echo "$STORE_RESPONSE" | tail -n1)
BODY=$(echo "$STORE_RESPONSE" | sed '$d')

if [ "$HTTP_CODE" = "200" ] || [ "$HTTP_CODE" = "201" ]; then
    STORE_ID=$(echo "$BODY" | jq -r '.id')
    echo_success "Store created: ${STORE_ID}"
else
    echo_error "Failed to create store (HTTP ${HTTP_CODE})"
    echo "$BODY" | jq . 2>/dev/null || echo "$BODY"
    exit 1
fi

# Step 7: Verify store access
echo_step "7. Verifying store access..."
VERIFY_RESPONSE=$(curl -s -w "\n%{http_code}" -X GET "${BTCPAY_URL}/api/v1/stores/${STORE_ID}" \
    -H "Authorization: token ${API_KEY}")
HTTP_CODE=$(echo "$VERIFY_RESPONSE" | tail -n1)
if [ "$HTTP_CODE" = "200" ]; then
    echo_success "Store access verified"
else
    echo_error "Failed to access store"
fi

# Step 8: Check Boltz plugin status
echo_step "8. Checking Boltz plugin..."
BOLTZ_RESPONSE=$(curl -s -w "\n%{http_code}" -X GET "${BTCPAY_URL}/api/v1/stores/${STORE_ID}/boltz/setup" \
    -H "Authorization: token ${API_KEY}")
HTTP_CODE=$(echo "$BOLTZ_RESPONSE" | tail -n1)
BODY=$(echo "$BOLTZ_RESPONSE" | sed '$d')

if [ "$HTTP_CODE" = "200" ]; then
    echo_success "Boltz plugin is available"
    BOLTZ_ENABLED=$(echo "$BODY" | jq -r '.enabled')
    echo_info "Boltz enabled: ${BOLTZ_ENABLED}"
elif [ "$HTTP_CODE" = "404" ]; then
    echo_info "Boltz plugin not installed or endpoint not available"
else
    echo_info "Boltz status check returned HTTP ${HTTP_CODE}"
fi

# Output summary
echo ""
echo -e "${GREEN}==========================================${NC}"
echo -e "${GREEN}  Setup Complete!${NC}"
echo -e "${GREEN}==========================================${NC}"
echo ""
echo -e "${CYAN}Configuration:${NC}"
echo_value "BTCPAY_URL=${BTCPAY_URL}"
echo_value "STORE_ID=${STORE_ID}"
echo_value "API_KEY=${API_KEY}"
echo ""
echo -e "${CYAN}User Credentials:${NC}"
echo_value "Email: ${USER_EMAIL}"
echo_value "Password: ${USER_PASSWORD}"
echo ""
echo -e "${CYAN}To test the Boltz API, run:${NC}"
echo ""
echo -e "    ${GREEN}./test-boltz-api.sh \"${API_KEY}\" \"${STORE_ID}\"${NC}"
echo ""
echo -e "${CYAN}Or set environment variables:${NC}"
echo ""
echo -e "    ${GREEN}export BTCPAY_URL=\"${BTCPAY_URL}\"${NC}"
echo -e "    ${GREEN}export API_KEY=\"${API_KEY}\"${NC}"
echo -e "    ${GREEN}export STORE_ID=\"${STORE_ID}\"${NC}"
echo -e "    ${GREEN}./test-boltz-api.sh${NC}"
echo ""

# Create a .env file for convenience
ENV_FILE=".env.regtest"
cat > "$ENV_FILE" << EOF
# BTCPay Regtest Configuration
# Generated by setup-btcpay-regtest.sh on $(date)

BTCPAY_URL=${BTCPAY_URL}
STORE_ID=${STORE_ID}
API_KEY=${API_KEY}

# User credentials (for web UI login)
USER_EMAIL=${USER_EMAIL}
USER_PASSWORD=${USER_PASSWORD}
EOF

echo -e "${CYAN}Configuration saved to:${NC}"
echo_value "${ENV_FILE}"
echo ""
echo -e "${CYAN}To load the configuration:${NC}"
echo -e "    ${GREEN}source ${ENV_FILE}${NC}"
echo ""
