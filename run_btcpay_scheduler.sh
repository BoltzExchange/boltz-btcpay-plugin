#!/bin/bash

# BTCPay Server Invoice Scheduler
# This script runs create_btcpay_invoice.sh every 2 minutes
# 
# Usage: ./run_btcpay_scheduler.sh [options]
#
# Options:
#   -h, --help          Show this help message
#   -v, --verbose       Enable verbose output
#   --interval SECONDS  Set custom interval in seconds (default: 120)
#   --max-runs NUMBER   Maximum number of runs before stopping (default: unlimited)
#   --log-file FILE     Log file path (default: btcpay_scheduler.log)

set -e

# Default values
INTERVAL=120  # 2 minutes
MAX_RUNS=0    # 0 means unlimited
LOG_FILE="btcpay_scheduler.log"
VERBOSE=false
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INVOICE_SCRIPT="$SCRIPT_DIR/create_btcpay_invoice.sh"

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

# Function to log with timestamp
log_message() {
    local message="$1"
    local timestamp=$(date '+%Y-%m-%d %H:%M:%S')
    echo "[$timestamp] $message" >> "$LOG_FILE"
    if [[ "$VERBOSE" == "true" ]]; then
        echo -e "${BLUE}[$timestamp]${NC} $message"
    fi
}

# Function to show usage
show_usage() {
    cat << EOF
BTCPay Server Invoice Scheduler

Usage: $0 [options]

This script runs create_btcpay_invoice.sh at regular intervals.

Options:
  -h, --help          Show this help message
  -v, --verbose       Enable verbose output
  --interval SECONDS  Set custom interval in seconds (default: 120)
  --max-runs NUMBER   Maximum number of runs before stopping (default: unlimited)
  --log-file FILE     Log file path (default: btcpay_scheduler.log)

Examples:
  # Run every 2 minutes (default)
  $0

  # Run every 5 minutes with verbose output
  $0 --interval 300 --verbose

  # Run maximum 10 times, every 1 minute
  $0 --interval 60 --max-runs 10

  # Use custom log file
  $0 --log-file /var/log/btcpay_scheduler.log

Environment Variables:
  The script will pass through all environment variables to create_btcpay_invoice.sh
  Make sure to set the required variables:
    BTCPAY_API_TOKEN
    BTCPAY_SERVER_URL
    STORE_ID
    AMOUNT

EOF
}

# Parse command line arguments
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
        --interval)
            INTERVAL="$2"
            shift 2
            ;;
        --max-runs)
            MAX_RUNS="$2"
            shift 2
            ;;
        --log-file)
            LOG_FILE="$2"
            shift 2
            ;;
        *)
            print_error "Unknown option: $1"
            show_usage
            exit 1
            ;;
    esac
done

# Validate interval
if ! [[ "$INTERVAL" =~ ^[0-9]+$ ]] || [[ "$INTERVAL" -lt 1 ]]; then
    print_error "Interval must be a positive integer (seconds)"
    exit 1
fi

# Validate max runs
if ! [[ "$MAX_RUNS" =~ ^[0-9]+$ ]]; then
    print_error "Max runs must be a non-negative integer"
    exit 1
fi

# Check if invoice script exists
if [[ ! -f "$INVOICE_SCRIPT" ]]; then
    print_error "Invoice script not found: $INVOICE_SCRIPT"
    exit 1
fi

# Check if invoice script is executable
if [[ ! -x "$INVOICE_SCRIPT" ]]; then
    print_warning "Making invoice script executable..."
    chmod +x "$INVOICE_SCRIPT"
fi

# Create log file if it doesn't exist
touch "$LOG_FILE"

# Function to handle script termination
cleanup() {
    log_message "Scheduler stopped by user (SIGINT/SIGTERM)"
    print_info "Scheduler stopped. Check $LOG_FILE for details."
    exit 0
}

# Set up signal handlers
trap cleanup SIGINT SIGTERM

# Display configuration
print_info "BTCPay Server Invoice Scheduler Configuration:"
echo "  Invoice Script: $INVOICE_SCRIPT"
echo "  Interval: $INTERVAL seconds ($(($INTERVAL / 60)) minutes)"
echo "  Max Runs: $([ "$MAX_RUNS" -eq 0 ] && echo "unlimited" || echo "$MAX_RUNS")"
echo "  Log File: $LOG_FILE"
echo "  Verbose: $VERBOSE"
echo

# Log start
log_message "Scheduler started - Interval: ${INTERVAL}s, Max Runs: $([ "$MAX_RUNS" -eq 0 ] && echo "unlimited" || echo "$MAX_RUNS")"

# Main loop
run_count=0
while true; do
    # Check if we've reached max runs
    if [[ "$MAX_RUNS" -gt 0 && "$run_count" -ge "$MAX_RUNS" ]]; then
        log_message "Maximum runs ($MAX_RUNS) reached. Stopping scheduler."
        print_info "Maximum runs reached. Scheduler stopped."
        break
    fi

    run_count=$((run_count + 1))
    log_message "Starting run #$run_count"

    # Run the invoice creation script
    if [[ "$VERBOSE" == "true" ]]; then
        print_info "Executing: $INVOICE_SCRIPT"
    fi

    # Capture the output and exit code
    if output=$("$INVOICE_SCRIPT" 2>&1); then
        log_message "Run #$run_count completed successfully"
        if [[ "$VERBOSE" == "true" ]]; then
            echo "$output"
        fi
    else
        exit_code=$?
        log_message "Run #$run_count failed with exit code $exit_code"
        print_error "Run #$run_count failed. Check $LOG_FILE for details."
        if [[ "$VERBOSE" == "true" ]]; then
            echo "$output"
        fi
    fi

    # Log the output to file
    echo "--- Run #$run_count Output ---" >> "$LOG_FILE"
    echo "$output" >> "$LOG_FILE"
    echo "--- End Run #$run_count ---" >> "$LOG_FILE"
    echo "" >> "$LOG_FILE"

    # Wait for the specified interval
    if [[ "$VERBOSE" == "true" ]]; then
        print_info "Waiting $INTERVAL seconds until next run..."
    fi
    sleep "$INTERVAL"
done

log_message "Scheduler finished"
print_success "Scheduler completed successfully!"
