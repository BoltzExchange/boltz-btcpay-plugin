VERSION := 2.2.19
RELEASE_PATH := ./release/BTCPayServer.Plugins.Boltz/$(VERSION)

gh-release:
	./release.sh
	git commit -a -m "chore: bump version to v$(VERSION)"
	git tag -s v$(VERSION) -m "v$(VERSION)"
	git push
	git push --tags
	cd $(RELEASE_PATH) && \
		rm SHA256SUMS.asc && \
		gpg --detach-sig SHA256SUMS
	gh release create v$(VERSION) --title v$(VERSION) --draft --notes-file release-notes-template.md $(RELEASE_PATH)/*

btcpay-appsettings:
	echo "{ \
	\"DEBUG_PLUGINS\": \"$(PWD)/BTCPayServer.Plugins.Boltz/bin/Debug/net8.0/BTCPayServer.Plugins.Boltz.dll\" \
	}" > ./btcpayserver/BTCPayServer/appsettings.dev.json

build:
	dotnet build BTCPayServer.Plugins.Boltz

run:
	cd ./btcpayserver/BTCPayServer && dotnet run --launch-profile "Bitcoin-Boltz"

dev: btcpay-appsettings build run

test:
	$(eval BTC_COOKIE := $(shell docker exec boltz-bitcoind cat /app/bitcoin/regtest/.cookie 2>/dev/null))
	TESTS_BTCRPCCONNECTION="server=http://127.0.0.1:18443;$(BTC_COOKIE)" \
	TESTS_BTCNBXPLORERURL="http://127.0.0.1:32838/" \
	TESTS_POSTGRES="User ID=boltz;Password=boltz;Include Error Detail=true;Host=127.0.0.1;Port=5432;Database=btcpayserver" \
	TESTS_EXPLORER_POSTGRES="User ID=boltz;Password=boltz;Include Error Detail=true;Host=127.0.0.1;Port=5432;Database=nbxplorer" \
	dotnet test ./BTCPayServer.Plugins.Boltz.Tests --logger "console;verbosity=normal"

