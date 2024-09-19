---
description: >-
  The Boltz BTCPay Plugin allows any merchant to accept Lightning payments in a
  non-custodial way without running a Lightning node or fully manage liquidity
  of an existing Lightning node.
cover: .gitbook/assets/boltz-btcpay-plugin.png
coverY: 0
---

# Introduction

## How does it work?

The [Boltz BTCPay Plugin](https://github.com/BoltzExchange/boltz-btcpay-plugin) is mainly a UI for [Boltz Client](https://docs.boltz.exchange/v/boltz-client), our long-running swap daemon. The plugin downloads latest release binaries of Boltz Client by default, checks the PGP signature and, if all checks out, starts the daemon.

## Building

To build and package the plugin for manual upload to your BTCPay Server, run the following commands inside the plugin's git repository:

```
git submodule update --init
dotnet publish BTCPayServer.Plugins.Boltz -o ./publish
dotnet run --project btcpayserver/BTCPayServer.PluginPacker ./publish BTCPayServer.Plugins.Boltz ./release
```

## Regtest Setup

You will need to setup [Boltz Regtest](https://github.com/BoltzExchange/regtest) first. Then clone this repository and build the plugin:

```
git clone https://github.com/BoltzExchange/boltz-btcpay-plugin --recurse-submodules
cd boltz-btcpay-plugin/
dotnet build BTCPayServer.Plugins.Boltz
```

Next, exit the directory and set up BTCPay Server:

```
git clone https://github.com/btcpayserver/btcpayserver
cd btcpayserver
bash -c "cd BTCPayServer.Tests && docker compose -p btcpay up -d dev"
cd BTCPayServer
echo "{
   \"DEBUG_PLUGINS\": \"/home/USER/boltz-btcpay-plugin/BTCPayServer.Plugins.Boltz/bin/Debug/net8.0/BTCPayServer.Plugins.Boltz.dll\"
}" > appsettings.dev.json
dotnet run --launch-profile Bitcoin -c Debug
```

Where `/home/USER/boltz-btcpay-plugin` is the path to the Boltz BTCPay Plugin directory.

You might have to update the client binaries to latest master.

```
git clone https://github.com/BoltzExchange/boltz-client
cd boltz-client
make
cp ./boltzd ./boltzcli ~/.btcpayserver/RegTest/LocalStorage/Boltz/bin/linux_amd64/
```

If you want to run against a local Lightning node from your btcpayserver repository:

```
cd BTCPayServer/Properties
nano launchsettings.json
```

In the launchsettings, change the `BTCPAY_BTCLIGHTNING` entry to connect to your Lightning node, for [CLN](https://github.com/ElementsProject/lightning) the connection string looks something like this:

`type=clightning;server=unix://home/USER/regtest/data/cln1/regtest/lightning-rpc`

Where `/home/USER/regtest` is the path of your [Boltz Regtest](https://github.com/BoltzExchange/regtest) repository.
