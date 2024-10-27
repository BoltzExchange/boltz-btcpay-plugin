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

To build and package the plugin for manual upload to your BTCPay Server, simply run the `release.sh` script inside the git repository.

The path to the built `.btcpay` file will be shown. You can upload this file to your btcpay server by navigating to `Plugins` and scrolling all the way down to `Upload Plugin`.
