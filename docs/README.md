---
description: >-
  The Boltz BTCPay Plugin allows any merchant to accept Lightning payments in a
  non-custodial way without running a Lightning node or fully manage liquidity
  of an existing Lightning node.
cover: .gitbook/assets/boltz-btcpay-plugin.png
coverY: 0
---

# 👋 Introduction

## How does it work?

The [Boltz BTCPay Plugin](https://github.com/BoltzExchange/boltz-btcpay-plugin) is mainly a UI for [Boltz Client](https://docs.boltz.exchange/v/boltz-client), our long-running swap daemon. The plugin downloads latest release binaries of Boltz Client by default, checks the PGP signature and, if all checks out, starts the daemon.

## Building

To build and package the plugin for manual upload to your BTCPay Server, run the following commands inside the plugin's git repository:

```
git submodule update --init
dotnet publish BTCPayServer.Plugins.Boltz -o ./publish
dotnet run --project btcpayserver/BTCPayServer.PluginPacker ./publish BTCPayServer.Plugins.Boltz ./release
```

