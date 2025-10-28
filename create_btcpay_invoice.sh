#!/bin/bash

# BTCPay Server Invoice Creator
# This script creates a BTCPay Server invoice via the Greenfield API
# The invoice can accept Lightning payments along with other payment methods
# 
# Usage: ./create_btcpay_invoice.sh [options]
#
# Required environment variables:
#   BTCPAY_API_TOKEN - Your BTCPay Server API token
#   BTCPAY_SERVER_URL - Your BTCPay Server URL (e.g., https://your-btcpay-server.com)
#   STORE_ID - Your store ID
#
# Optional environment variables:
#   AMOUNT - Amount in the store's default currency (e.g., "10.00")
#   CURRENCY - Currency code (default: uses store's default currency)
#   DESCRIPTION - Invoice description (default: "Test Invoice")
#   ORDER_ID - Order ID for tracking (optional)
#   REDIRECT_URL - URL to redirect after payment (optional)
#   NOTIFICATION_URL - Webhook URL for notifications (optional)
#   PAYMENT_METHODS - Comma-separated payment methods (default: all enabled methods)

set -e

# Default values
DESCRIPTION=${DESCRIPTION:-"Test Invoice"}
NOTIFICATION_URL=${NOTIFICATION_URL:-""}
REDIRECT_URL=${REDIRECT_URL:-""}
ORDER_ID=${ORDER_ID:-""}
PAYMENT_METHODS=${PAYMENT_METHODS:-""}

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Function to print colored output
print_info() {
    echo -e "${BLUE}[INFO]${NC} $1"
}

print_success() {
    echo -e "${GREEN}[SUCCESS]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Function to show usage
show_usage() {
    cat << EOF
BTCPay Server Invoice Creator

Usage: $0 [options]

Required environment variables:
  BTCPAY_API_TOKEN     Your BTCPay Server API token
  BTCPAY_SERVER_URL    Your BTCPay Server URL (e.g., https://your-btcpay-server.com)
  STORE_ID            Your store ID

Optional environment variables:
  AMOUNT              Amount in the store's default currency (e.g., "10.00")
  CURRENCY            Currency code (default: uses store's default currency)
  DESCRIPTION         Invoice description (default: "Test Invoice")
  ORDER_ID            Order ID for tracking (optional)
  REDIRECT_URL        URL to redirect after payment (optional)
  NOTIFICATION_URL    Webhook URL for notifications (optional)
  PAYMENT_METHODS     Comma-separated payment methods (default: all enabled methods)
                      Examples: "BTC,BTC-LightningNetwork" or "BTC-LightningNetwork"

Examples:
  # Basic usage
  BTCPAY_API_TOKEN="your_token" BTCPAY_SERVER_URL="https://your-btcpay.com" STORE_ID="your_store_id" AMOUNT="10.00" $0

  # With Lightning payment method only
  BTCPAY_API_TOKEN="your_token" BTCPAY_SERVER_URL="https://your-btcpay.com" STORE_ID="your_store_id" AMOUNT="10.00" PAYMENT_METHODS="BTC-LightningNetwork" $0

  # With custom description and order ID
  BTCPAY_API_TOKEN="your_token" BTCPAY_SERVER_URL="https://your-btcpay.com" STORE_ID="your_store_id" AMOUNT="25.50" DESCRIPTION="Custom Order" ORDER_ID="ORDER-123" $0

  # With webhook notification
  BTCPAY_API_TOKEN="your_token" BTCPAY_SERVER_URL="https://your-btcpay.com" STORE_ID="your_store_id" AMOUNT="10.00" NOTIFICATION_URL="https://your-app.com/webhook" $0

Options:
  -h, --help          Show this help message
  -v, --verbose       Enable verbose output
  --dry-run          Show the request that would be sent without making it

EOF
}

# Parse command line arguments
VERBOSE=false
DRY_RUN=false

while [[ $# -gt 0 ]]; do
    case $1 in
        -h|--help)
            show_usage
            exit 0
            ;;
        -v|--verbose)
            VERBOSE=true
            shift
            ;;
        --dry-run)
            DRY_RUN=true
            shift
            ;;
        *)
            print_error "Unknown option: $1"
            show_usage
            exit 1
            ;;
    esac
done

# Check required environment variables
if [[ -z "$BTCPAY_API_TOKEN" ]]; then
    print_error "BTCPAY_API_TOKEN environment variable is required"
    exit 1
fi

if [[ -z "$BTCPAY_SERVER_URL" ]]; then
    print_error "BTCPAY_SERVER_URL environment variable is required"
    exit 1
fi

if [[ -z "$STORE_ID" ]]; then
    print_error "STORE_ID environment variable is required"
    exit 1
fi

if [[ -z "$AMOUNT" ]]; then
    print_error "AMOUNT environment variable is required"
    exit 1
fi

# Remove trailing slash from URL if present
BTCPAY_SERVER_URL=${BTCPAY_SERVER_URL%/}

# Validate amount is numeric
if ! [[ "$AMOUNT" =~ ^[0-9]+\.?[0-9]*$ ]]; then
    print_error "AMOUNT must be a valid number (e.g., '10.00' or '10')"
    exit 1
fi

# Prepare the JSON payload
JSON_PAYLOAD=$(cat << EOF
{
  "amount": "$AMOUNT"$([ -n "$CURRENCY" ] && echo ",\n  \"currency\": \"$CURRENCY\""),
  "description": "$DESCRIPTION"$([ -n "$ORDER_ID" ] && echo ",\n  \"orderId\": \"$ORDER_ID\""),
  "redirectURL": $([ -n "$REDIRECT_URL" ] && echo "\"$REDIRECT_URL\"" || echo "null"),
  "notificationURL": $([ -n "$NOTIFICATION_URL" ] && echo "\"$NOTIFICATION_URL\"" || echo "null")$([ -n "$PAYMENT_METHODS" ] && echo ",\n  \"paymentMethods\": [\"$(echo "$PAYMENT_METHODS" | tr ',' '\n' | sed 's/^/"/;s/$/"/' | tr '\n' ',' | sed 's/,$//' | sed 's/,/", "/g')\"]")
}
EOF
)

# Display the request details
if [[ "$VERBOSE" == "true" || "$DRY_RUN" == "true" ]]; then
    print_info "API Endpoint: $BTCPAY_SERVER_URL/api/v1/stores/$STORE_ID/invoices"
    print_info "Request Method: POST"
    print_info "Request Headers:"
    echo "  Authorization: token $BTCPAY_API_TOKEN"
    echo "  Content-Type: application/json"
    print_info "Request Body:"
    echo "$JSON_PAYLOAD" | jq . 2>/dev/null || echo "$JSON_PAYLOAD"
    echo
fi

# Exit if dry run
if [[ "$DRY_RUN" == "true" ]]; then
    print_info "Dry run completed. No request was sent."
    exit 0
fi

RESPONSE=$(curl -s -w "\n%{http_code}" \
    -X POST \
    -H "Authorization: token $BTCPAY_API_TOKEN" \
    -H "Content-Type: application/json" \
    -d "$JSON_PAYLOAD" \
    "$BTCPAY_SERVER_URL/api/v1/stores/$STORE_ID/invoices")

# Extract HTTP status code and response body
HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
RESPONSE_BODY=$(echo "$RESPONSE" | head -n -1)

# Check if curl command was successful
if [[ $? -ne 0 ]]; then
    print_error "Failed to make API request"
    exit 1
fi

# Check HTTP status code
case $HTTP_CODE in
    200|201)
        echo "$RESPONSE_BODY" | jq . 2>/dev/null || echo "$RESPONSE_BODY"
        ;;
    400)
        print_error "Bad Request (400)"
        echo "$RESPONSE_BODY" | jq . 2>/dev/null || echo "$RESPONSE_BODY"
        exit 1
        ;;
    401)
        print_error "Unauthorized (401) - Check your API token"
        exit 1
        ;;
    403)
        print_error "Forbidden (403) - Check your API token permissions"
        echo "$RESPONSE_BODY" | jq . 2>/dev/null || echo "$RESPONSE_BODY"
        exit 1
        ;;
    404)
        print_error "Not Found (404) - Check your store ID"
        echo "$RESPONSE_BODY" | jq . 2>/dev/null || echo "$RESPONSE_BODY"
        exit 1
        ;;
    422)
        print_error "Validation Error (422)"
        echo "$RESPONSE_BODY" | jq . 2>/dev/null || echo "$RESPONSE_BODY"
        exit 1
        ;;
    *)
        print_error "Unexpected HTTP status code: $HTTP_CODE"
        echo "$RESPONSE_BODY" | jq . 2>/dev/null || echo "$RESPONSE_BODY"
        exit 1
        ;;
esac
