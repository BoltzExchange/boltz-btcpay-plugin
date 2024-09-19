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
