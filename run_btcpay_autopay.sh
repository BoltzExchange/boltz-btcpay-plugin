#!/bin/bash

# BTCPay Server Invoice Auto-Payment Scheduler (Concurrent)
# This script creates BTCPay invoices and automatically pays them via Lightning Network
# with support for concurrent execution
# 
# Usage: ./run_btcpay_autopay.sh [options]
#
# Options:
#   -h, --help             Show this help message
#   -v, --verbose          Enable verbose output
#   --interval SECONDS     Set custom interval in seconds (default: 120)
#   --max-runs NUMBER      Maximum number of runs before stopping (default: unlimited)
#   --concurrency NUMBER   Number of concurrent invoice jobs (default: 1)
#   --log-file FILE        Log file path (default: btcpay_autopay.log)
#   --lncli-node NODE      LND node number for lncli-sim (default: 1)

set -e

# Default values
INTERVAL=120  # 2 minutes
MAX_RUNS=0    # 0 means unlimited
CONCURRENCY=1 # Number of concurrent jobs
LOG_FILE="btcpay_autopay.log"
VERBOSE=false
LNCLI_NODE=1
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INVOICE_SCRIPT="$SCRIPT_DIR/create_btcpay_invoice.sh"

source $SCRIPT_DIR/regtest/aliases.sh

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Temporary files for tracking concurrent job statistics
STATS_DIR="/tmp/btcpay_autopay_$$"
mkdir -p "$STATS_DIR"

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

# Function to log with timestamp (thread-safe using flock)
log_message() {
    local message="$1"
    local timestamp=$(date '+%Y-%m-%d %H:%M:%S')
    (
        flock -x 200
        echo "[$timestamp] $message" >> "$LOG_FILE"
    ) 200>"$LOG_FILE.lock"
    
    if [[ "$VERBOSE" == "true" ]]; then
        echo -e "${BLUE}[$timestamp]${NC} $message"
    fi
}

# Function to atomically increment counter
increment_counter() {
    local counter_file="$1"
    (
        flock -x 200
        local count=0
        if [[ -f "$counter_file" ]]; then
            count=$(cat "$counter_file")
        fi
        count=$((count + 1))
        echo "$count" > "$counter_file"
        echo "$count"
    ) 200>"$counter_file.lock"
}

# Function to get counter value
get_counter() {
    local counter_file="$1"
    if [[ -f "$counter_file" ]]; then
        cat "$counter_file"
    else
        echo "0"
    fi
}

# Worker function that processes a single invoice
process_invoice() {
    local job_id="$1"
    local run_number="$2"
    
    # Set up error handling for this job
    set -E
    trap 'log_message "ERROR: Job #$job_id crashed unexpectedly at line $LINENO"; increment_counter "$STATS_DIR/failed"; exit 1' ERR
    
    log_message "Job #$job_id (Run #$run_number): Started"
    
    # Step 1: Create the invoice
    if [[ "$VERBOSE" == "true" ]]; then
        print_info "Job #$job_id: Step 1 - Creating BTCPay invoice..."
    fi

    local invoice_output
    local invoice_exit_code
    invoice_output=$("$INVOICE_SCRIPT" 2>&1)
    invoice_exit_code=$?

    if [[ $invoice_exit_code -ne 0 ]]; then
        log_message "Job #$job_id failed: Could not create invoice (exit code $invoice_exit_code)"
        if [[ "$VERBOSE" == "true" ]]; then
            print_error "Job #$job_id: Failed to create invoice"
        fi
        (
            flock -x 200
            echo "--- Job #$job_id Invoice Creation Failed ---" >> "$LOG_FILE"
            echo "$invoice_output" >> "$LOG_FILE"
            echo "--- End Job #$job_id ---" >> "$LOG_FILE"
            echo "" >> "$LOG_FILE"
        ) 200>"$LOG_FILE.lock"
        
        increment_counter "$STATS_DIR/failed"
        return 1
    fi

    log_message "Job #$job_id: Invoice created successfully"
    
    if [[ "$VERBOSE" == "true" ]]; then
        print_success "Job #$job_id: Invoice created"
    fi

    # Step 2: Extract checkout link
    if [[ "$VERBOSE" == "true" ]]; then
        print_info "Job #$job_id: Step 2 - Extracting checkout link..."
    fi

    local checkout_link
    checkout_link=$(echo "$invoice_output" | jq -r '.checkoutLink' 2>/dev/null)

    if [[ -z "$checkout_link" || "$checkout_link" == "null" ]]; then
        log_message "Job #$job_id failed: Could not extract checkout link"
        if [[ "$VERBOSE" == "true" ]]; then
            print_error "Job #$job_id: Could not extract checkout link"
        fi
        increment_counter "$STATS_DIR/failed"
        return 1
    fi

    log_message "Job #$job_id: Checkout link extracted"

    # Step 3: Fetch checkout page HTML
    if [[ "$VERBOSE" == "true" ]]; then
        print_info "Job #$job_id: Step 3 - Fetching checkout page..."
    fi

    local checkout_html
    checkout_html=$(curl -s "$checkout_link")

    if [[ $? -ne 0 ]]; then
        log_message "Job #$job_id failed: Could not fetch checkout page"
        if [[ "$VERBOSE" == "true" ]]; then
            print_error "Job #$job_id: Failed to fetch checkout page"
        fi
        increment_counter "$STATS_DIR/failed"
        return 1
    fi

    log_message "Job #$job_id: Checkout page fetched"

    # Step 4: Extract Lightning payment request from HTML
    if [[ "$VERBOSE" == "true" ]]; then
        print_info "Job #$job_id: Step 4 - Extracting payment request..."
    fi

    local payment_request
    # Pattern 1: Look for lnbc/lntb/lnbcrt in the HTML
    payment_request=$(echo "$checkout_html" | grep -oP '(lnbc|lntb|lnbcrt)[a-zA-Z0-9]+' | head -n 1)

    # Pattern 2: Try from lightning: URI
    if [[ -z "$payment_request" ]]; then
        payment_request=$(echo "$checkout_html" | grep -oP 'lightning:(lnbc|lntb|lnbcrt)[a-zA-Z0-9]+' | sed 's/lightning://' | head -n 1)
    fi

    # Pattern 3: Try from JSON data attributes
    if [[ -z "$payment_request" ]]; then
        payment_request=$(echo "$checkout_html" | grep -oP '"destination"\s*:\s*"(lnbc|lntb|lnbcrt)[a-zA-Z0-9]+"' | grep -oP '(lnbc|lntb|lnbcrt)[a-zA-Z0-9]+' | head -n 1)
    fi

    # Pattern 4: Case-insensitive fallback
    if [[ -z "$payment_request" ]]; then
        payment_request=$(echo "$checkout_html" | grep -oiP '(lnbc|lntb|lnbcrt)[a-zA-Z0-9]+' | head -n 1)
    fi

    if [[ -z "$payment_request" ]]; then
        log_message "Job #$job_id failed: Could not extract Lightning payment request"
        if [[ "$VERBOSE" == "true" ]]; then
            print_error "Job #$job_id: Could not extract payment request"
        fi
        increment_counter "$STATS_DIR/failed"
        return 1
    fi

    log_message "Job #$job_id: Payment request extracted: ${payment_request:0:50}..."

    # Step 5: Pay the invoice using lncli-sim
    if [[ "$VERBOSE" == "true" ]]; then
        print_info "Job #$job_id: Step 5 - Paying invoice with lncli-sim $LNCLI_NODE..."
    fi

    local payment_output
    local payment_exit_code

    echo "source $SCRIPT_DIR/regtest/aliases.sh && lncli-sim $LNCLI_NODE payinvoice --force $payment_request" > $job_id.sh   
    chmod +x $job_id.sh
    
    log_message "Job #$job_id: Attempting payment with lncli-sim $LNCLI_NODE..."
    payment_output=$(bash -i "./$job_id.sh")
    payment_exit_code=$?
    
    log_message "Job #$job_id: Payment exit code: $payment_exit_code"

    # Log payment output
    (
        flock -x 200
        echo "--- Job #$job_id Payment ---" >> "$LOG_FILE"
        echo "$payment_output" >> "$LOG_FILE"
        echo "--- End Job #$job_id ---" >> "$LOG_FILE"
        echo "" >> "$LOG_FILE"
    ) 200>"$LOG_FILE.lock"

    if [[ $payment_exit_code -eq 0 ]]; then
        log_message "Job #$job_id completed successfully - Invoice created and paid"
        if [[ "$VERBOSE" == "true" ]]; then
            print_success "Job #$job_id: Invoice paid successfully!"
        fi
        increment_counter "$STATS_DIR/success"
        return 0
    else
        log_message "Job #$job_id failed: Payment failed (exit code $payment_exit_code)"
        if [[ "$VERBOSE" == "true" ]]; then
            print_error "Job #$job_id: Failed to pay invoice"
        fi
        increment_counter "$STATS_DIR/failed"
        return 1
    fi
}

# Function to show usage
show_usage() {
    cat << EOF
BTCPay Server Invoice Auto-Payment Scheduler (Concurrent)

Usage: $0 [options]

This script creates BTCPay invoices and automatically pays them via Lightning Network
using lncli-sim at regular intervals with support for concurrent execution.

Options:
  -h, --help             Show this help message
  -v, --verbose          Enable verbose output
  --interval SECONDS     Set custom interval in seconds (default: 120)
  --max-runs NUMBER      Maximum number of runs before stopping (default: unlimited)
  --concurrency NUMBER   Number of concurrent invoice jobs (default: 1)
  --log-file FILE        Log file path (default: btcpay_autopay.log)
  --lncli-node NODE      LND node number for lncli-sim (default: 1)

Examples:
  # Run every 2 minutes (default, sequential)
  $0

  # Run 5 concurrent jobs every 30 seconds
  $0 --interval 30 --concurrency 5 --verbose

  # Run 10 concurrent jobs continuously
  $0 --concurrency 10

  # Run maximum 100 jobs with 5 concurrent workers
  $0 --interval 60 --max-runs 100 --concurrency 5

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
        --concurrency)
            CONCURRENCY="$2"
            shift 2
            ;;
        --log-file)
            LOG_FILE="$2"
            shift 2
            ;;
        --lncli-node)
            LNCLI_NODE="$2"
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

# Validate concurrency
if ! [[ "$CONCURRENCY" =~ ^[0-9]+$ ]] || [[ "$CONCURRENCY" -lt 1 ]]; then
    print_error "Concurrency must be a positive integer"
    exit 1
fi

# Validate lncli node
if ! [[ "$LNCLI_NODE" =~ ^[0-9]+$ ]] || [[ "$LNCLI_NODE" -lt 1 ]]; then
    print_error "LND node must be a positive integer"
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

# Check if lncli-sim is available
if ! command -v lncli-sim &> /dev/null; then
    print_error "lncli-sim command not found. Make sure it's in your PATH."
    exit 1
fi

# Check if jq is available (needed to parse JSON)
if ! command -v jq &> /dev/null; then
    print_error "jq command not found. Please install jq to parse JSON responses."
    exit 1
fi

# Create log file if it doesn't exist
touch "$LOG_FILE"

# Initialize counter files
echo "0" > "$STATS_DIR/success"
echo "0" > "$STATS_DIR/failed"

# Function to handle script termination
cleanup() {
    log_message "Auto-payment scheduler stopped by user (SIGINT/SIGTERM)"
    print_info "Cleaning up background jobs..."
    
    # Kill all background jobs
    jobs -p | xargs -r kill 2>/dev/null || true
    
    # Clean up stats directory
    rm -rf "$STATS_DIR"
    
    print_info "Scheduler stopped. Check $LOG_FILE for details."
    exit 0
}

# Set up signal handlers
trap cleanup SIGINT SIGTERM EXIT

# Display configuration
print_info "BTCPay Server Invoice Auto-Payment Scheduler Configuration:"
echo "  Invoice Script: $INVOICE_SCRIPT"
echo "  LND Node: $LNCLI_NODE"
echo "  Interval: $INTERVAL seconds ($(($INTERVAL / 60)) minutes)"
echo "  Concurrency: $CONCURRENCY concurrent jobs"
echo "  Max Runs: $([ "$MAX_RUNS" -eq 0 ] && echo "unlimited" || echo "$MAX_RUNS")"
echo "  Log File: $LOG_FILE"
echo "  Verbose: $VERBOSE"
echo

# Log start
log_message "Auto-payment scheduler started - Interval: ${INTERVAL}s, Max Runs: $([ "$MAX_RUNS" -eq 0 ] && echo "unlimited" || echo "$MAX_RUNS"), Concurrency: $CONCURRENCY, LND Node: $LNCLI_NODE"

# Array to track background jobs
declare -a job_pids=()

# Main loop
run_count=0
batch_count=0

while true; do
    # Check if we've reached max runs
    if [[ "$MAX_RUNS" -gt 0 && "$run_count" -ge "$MAX_RUNS" ]]; then
        log_message "Maximum runs ($MAX_RUNS) reached. Waiting for jobs to complete..."
        print_info "Maximum runs reached. Waiting for remaining jobs to complete..."
        
        # Wait for all background jobs to finish
        for pid in "${job_pids[@]}"; do
            if wait "$pid"; then
                log_message "Job with PID $pid completed successfully"
            else
                local exit_code=$?
                log_message "WARNING: Job with PID $pid exited with code $exit_code"
                if [[ "$VERBOSE" == "true" ]]; then
                    print_error "Job PID $pid crashed or failed unexpectedly (exit code: $exit_code)"
                fi
            fi
        done
        
        break
    fi

    batch_count=$((batch_count + 1))
    print_info "=========================================="
    print_info "Batch #$batch_count - Starting $CONCURRENCY concurrent jobs"
    print_info "=========================================="

    # Clear the job_pids array
    job_pids=()

    # Launch concurrent jobs
    for ((i=1; i<=CONCURRENCY; i++)); do
        # Check if we've reached max runs
        if [[ "$MAX_RUNS" -gt 0 && "$run_count" -ge "$MAX_RUNS" ]]; then
            break
        fi
        
        run_count=$((run_count + 1))
        job_id="$batch_count.$i"
        
        # Export necessary variables for the worker function
        export VERBOSE LOG_FILE LNCLI_NODE INVOICE_SCRIPT STATS_DIR
        
        # Launch job in background
        # Note: stdout/stderr are not redirected, so any unexpected output will be visible
        (process_invoice "$job_id" "$run_count") 2>&1 &
        job_pids+=($!)
        
        log_message "Batch #$batch_count: Launched job #$job_id (PID: $!)"
    done

    # Wait for all jobs in this batch to complete
    print_info "Waiting for batch #$batch_count jobs to complete..."
    for pid in "${job_pids[@]}"; do
        if wait "$pid"; then
            log_message "Job with PID $pid completed successfully"
        else
            exit_code=$?
            log_message "WARNING: Job with PID $pid exited with code $exit_code"
            if [[ "$VERBOSE" == "true" ]]; then
                print_error "Job PID $pid crashed or failed unexpectedly (exit code: $exit_code)"
            fi
        fi
    done

    # Get current statistics
    success_count=$(get_counter "$STATS_DIR/success")
    failed_count=$(get_counter "$STATS_DIR/failed")
    total_completed=$((success_count + failed_count))

    # Display batch statistics
    print_success "Batch #$batch_count completed!"
    print_info "Statistics: Total Started: $run_count | Completed: $total_completed | Success: $success_count | Failed: $failed_count"
    echo

    # Check if we should continue
    if [[ "$MAX_RUNS" -gt 0 && "$run_count" -ge "$MAX_RUNS" ]]; then
        break
    fi

    # Wait for the specified interval before next batch
    if [[ "$VERBOSE" == "true" ]] || [[ "$CONCURRENCY" -gt 1 ]]; then
        print_info "Waiting $INTERVAL seconds until next batch..."
        echo
    fi
    sleep "$INTERVAL"
done

# Final statistics
success_count=$(get_counter "$STATS_DIR/success")
failed_count=$(get_counter "$STATS_DIR/failed")
total_completed=$((success_count + failed_count))

log_message "Auto-payment scheduler finished - Total Started: $run_count | Completed: $total_completed | Success: $success_count | Failed: $failed_count"
print_success "Scheduler completed successfully!"
print_info "Final Statistics:"
echo "  Total Runs Started: $run_count"
echo "  Total Completed: $total_completed"
echo "  Successful: $success_count"
echo "  Failed: $failed_count"

# Cleanup is handled by trap
