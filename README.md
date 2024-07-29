# BTCPay Server Boltz Plugin

## Regtest setup

You will need to setup https://github.com/BoltzExchange/legend-regtest-enviroment first.

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
   "DEBUG_PLUGINS": "/home/jacksn/dev/btcpayserver-boltz/BTCPayServer.Plugins.Boltz/bin/Debug/net8.0/BTCPayServer.Plugins.Boltz.dll"
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

`type=clightning;server=unix://home/jacksn/regtest/data/clightning-1/regtest/lightning-rpc`

Where `/home/jacksn/regtest` is the path of your `legend-regtest-enviroment` repository.
