---
description:
  How to set up a local regtest environment for Boltz BTCPay Plugin development
  using Boltz Regtest and the BTCPay profile.
next: false
---

# 🧪 Regtest Setup

This page describes how to setup a regtest environment for plugin development.

## Regtest Setup Guide

First, set up [Boltz Regtest](https://github.com/BoltzExchange/regtest). Run
`export COMPOSE_PROFILES=btcpay,default` before starting Boltz Regtest (or add
it to your `.bashrc` or `.zshrc`) in order to initialize it with the BTCPay
profile.

Install the .NET SDK v8 (necessary to build the Plugin).

Then clone this repository and build the Plugin:

```bash
git clone https://github.com/BoltzExchange/boltz-btcpay-plugin --recurse-submodules
cd boltz-btcpay-plugin/
ln --symbolic ~/path/to/regtest
make dev
```

You can also run the latest version of Boltz Client manually.

```bash
git clone https://github.com/BoltzExchange/boltz-client
cd boltz-client
make
cp ./boltzd ./boltzcli ~/.btcpayserver/RegTest/LocalStorage/Boltz/bin/linux_amd64/
```

The Boltz Greenfield Swagger spec is generated manually
`BTCPayServer.Plugins.Boltz/Resources/swagger.boltz.json`. If you make changes
to the Greenfield API endpoints, make sure to update this file as part of the
same change.
