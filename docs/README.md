---
cover: .gitbook/assets/boltz-btcpay-plugin.png
coverY: 0
---

# 👋 Introduction

Our [BTCPayServer](https://github.com/btcpayserver/btcpayserver) Plugin provides a tight integration with boltz to allow for easy setup of
self-custodial lightning payments or rebalancing your existing lightning node.

## How does it work?

The plugin is "just" a frontend for [boltz-client](https://docs.boltz.exchange/v/boltz-client), our long running swap daemon for end users.
The plugins default behaviour is to download the latest release binaries from github, check the pgp signature and run it.

## Building

To build and package the plugin for uploading to your btcpay server, run the following commands inside the git repository:

```
git submodule update --init
dotnet publish BTCPayServer.Plugins.Boltz -o ./publish
dotnet run --project btcpayserver/BTCPayServer.PluginPacker ./publish BTCPayServer.Plugins.Boltz ./release
```

## Regtest setup

You will need to setup https://github.com/BoltzExchange/regtest first.

Then, clone this repository and build the plugin

```
git clone https://github.com/jackstar12/btcpayserver-boltz --recurse-submodules
cd btcpayserver-boltz/
dotnet build BTCPayServer.Plugins.Boltz
```

Then exit the directory and setup BTCPayServer

```
git clone https://github.com/btcpayserver/btcpayserver
cd btcpayserver
bash -c "cd BTCPayServer.Tests && docker compose -p btcpay up -d dev"
cd BTCPayServer
echo "{
   \"DEBUG_PLUGINS\": \"/home/jacksn/dev/btcpayserver-boltz/BTCPayServer.Plugins.Boltz/bin/Debug/net8.0/BTCPayServer.Plugins.Boltz.dll\"
}" > appsettings.dev.json
dotnet run --launch-profile Bitcoin -c Debug
```

Where `/home/jacksn/dev/btcpayserver-boltz` is the path to this repository.

You might have to update the client binaries to latest master.

```
git clone https://github.com/BoltzExchange/boltz-client
cd boltz-client
make
cp ./boltzd ./boltzcli ~/.btcpayserver/RegTest/LocalStorage/Boltz/bin/linux_amd64/
```

If you want to run against a local lightning node from your btcpayserver repository.

```
cd BTCPayServer/Properties
nano launchsettings.json
```

In the launchsettings, change the `BTCPAY_BTCLIGHTNING` entry to

`type=clightning;server=unix://home/jacksn/regtest/data/cln1/regtest/lightning-rpc`

Where `/home/jacksn/regtest` is the path of your https://github.com/BoltzExchange/regtest repository.
