#!/bin/bash

# BTCPayServer Boltz Plugin Test Runner
# This script runs the Boltz plugin tests with proper configuration

export TESTS_BTCRPCCONNECTION="server=http://127.0.0.1:18443;$(cat ./regtest/data/bitcoind/regtest/.cookie)"

set -e

echo "Running BTCPayServer Boltz Plugin Tests..."

# Change to the test project directory
cd "$(dirname "$0")/BTCPayServer.Plugins.Boltz.Tests"

# Run the tests
echo "Running all Boltz plugin tests..."
dotnet test --logger "console;verbosity=normal"

export TESTS_BTCRPCCONNECTION="server=http://127.0.0.1:18443;$(cat ./regtest/data/bitcoind/regtest/.cookie)"
export TESTS_BTCNBXPLORERURL="http://127.0.0.1:32838/"
export TESTS_POSTGRES="User ID=boltz;Password=boltz;Include Error Detail=true;Host=127.0.0.1;Port=5432;Database=btcpayserver"
export TESTS_EXPLORER_POSTGRES="User ID=boltz;Password=boltz;Include Error Detail=true;Host=127.0.0.1;Port=5432;Database=nbxplorer"

echo ""
echo "Running fast tests only..."
dotnet test --filter "Trait=Fast" --logger "console;verbosity=normal"

echo ""
echo "Running integration tests only..."
dotnet test --filter "Trait=Integration" --logger "console;verbosity=normal"

echo ""
echo "All tests completed successfully!"
