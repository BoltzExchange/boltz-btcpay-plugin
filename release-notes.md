# Summary

TODO

# Uploading to BTCPay

You can upload the `BTCPayServer.Plugins.Boltz.btcpay` file to your btcpay server by navigating to `Plugins` and scrolling all the way down to `Upload Plugin`.

# Verifying the Release

In order to verify the release, you'll need to have `gpg` or `gpg2` installed on your system. You'll first need to import the keys that have signed this release if you haven't done so already:

```
curl https://raw.githubusercontent.com/BoltzExchange/boltz-btcpay-plugin/master/keys/jackstar.asc | gpg --import
```

Once you have the required PGP keys, you can verify the release (assuming `SHA256SUMS` and `SHA256SUMS.sig` are in the current directory) with:

```
gpg --verify SHA256SUMS.sig
```

You should see the following if the verification was successful:

```
gpg: Signature made Do 31 Okt 2024 13:56:07 CET
gpg:                using RSA key A73B8D6D8C23D4D2943B67A837308B01365311D1
gpg: Good signature from "jackstar12 <jkranawetter05@gmail.com>" [ultimate]
```

You should also verify that the hashes still match with the archive you've downloaded.

```
sha256sum --ignore-missing -c SHA256SUMS
```

If your archive is valid, you should see the following output (depending on the archive you've downloaded):

```
BTCPayServer.Plugins.Boltz.btcpay.json: OK
BTCPayServer.Plugins.Boltz.btcpay: OK
```
