VERSION := 2.2.15
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

