VERSION := 2.2.4
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

