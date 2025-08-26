---
description: This page describes how to manually build the plugin for plugin development
---

# 🧪 Regtest Setup

First, set up [Boltz Regtest](https://github.com/BoltzExchange/regtest). Then clone this repository and build the plugin:

```
git clone https://github.com/BoltzExchange/boltz-btcpay-plugin --recurse-submodules
cd boltz-btcpay-plugin/
dotnet build BTCPayServer.Plugins.Boltz
```

Next, exit the directory and set up BTCPay Server:

```
git clone https://github.com/btcpayserver/btcpayserver
cd btcpayserver/BTCPayServer
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

In order to run with the previously setup regtest environnment, you need to open `launchsettings.json`:

```
cd BTCPayServer/Properties
nano launchsettings.json
```

Add the following launch profile to use the boltz regtest:

```json
    "Bitcoin-Boltz": {
      "commandName": "Project",
      "launchBrowser": true,
      "environmentVariables": {
        "BTCPAY_NETWORK": "regtest",
        "BTCPAY_LAUNCHSETTINGS": "true",
        "BTCPAY_BTCLIGHTNING": "type=lnd-rest;server=https://127.0.0.1:8081/;macaroonfilepath=/home/USER/regtest/data/lnd1/data/chain/bitcoin/regtest/admin.macaroon;certfilepath=/home/USER/regtest/data/lnd1/tls.cert;allowinsecure=true",
        "BTCPAY_BTCLIGHTNINGLND1": "type=lnd-rest;server=https://127.0.0.1:8081/;macaroonfilepath=/home/USER/regtest/data/lnd1/data/chain/bitcoin/regtest/admin.macaroon;certfilepath=/home/USER/regtest/data/lnd1/tls.cert;allowinsecure=true",
        "BTCPAY_BTCLIGHTNINGLND2": "type=lnd-rest;server=https://127.0.0.1:8181/;macaroonfilepath=/home/USER/regtest/data/lnd2/data/chain/bitcoin/regtest/admin.macaroon;allowinsecure=true",
        "BTCPAY_BTCLIGHTNINGCLN1": "type=clightning;server=unix://home/USER/regtest/data/cln1/regtest/lightning-rpc",
        "BTCPAY_BTCEXPLORERURL": "http://127.0.0.1:32838/",
        "BTCPAY_ALLOW-ADMIN-REGISTRATION": "true",
        "BTCPAY_DISABLE-REGISTRATION": "false",
        "ASPNETCORE_ENVIRONMENT": "Development",
        "BTCPAY_CHAINS": "btc",
        "BTCPAY_VERBOSE": "true",
        "BTCPAY_POSTGRES": "User ID=boltz;Password=boltz;Include Error Detail=true;Host=127.0.0.1;Port=5432;Database=btcpayserver",
        "BTCPAY_DEBUGLOG": "debug.log",
        "BTCPAY_UPDATEURL": "",
        "BTCPAY_DOCKERDEPLOYMENT": "true",
        "BTCPAY_RECOMMENDED-PLUGINS": "",
        "BTCPAY_CHEATMODE": "true",
        "BTCPAY_EXPLORERPOSTGRES": "User ID=boltz;Password=boltz;Include Error Detail=true;Host=127.0.0.1;Port=5432;Database=nbxplorer"
      },
      "applicationUrl": "http://localhost:14142/"
    },
```

Where `/home/USER/regtest` is the path of your [Boltz Regtest](https://github.com/BoltzExchange/regtest) repository.
