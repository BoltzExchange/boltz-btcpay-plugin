dotnet publish BTCPayServer.Plugins.Boltz -o ./publish
dotnet run --project btcpayserver/BTCPayServer.PluginPacker ./publish BTCPayServer.Plugins.Boltz ./release
