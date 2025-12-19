# Summary

TODO

**Do not attempt to install on BTCPay v1**

# Uploading to BTCPay

You can upload the `BTCPayServer.Plugins.Boltz.btcpay` file to your btcpay server by navigating to `Plugins` and scrolling all the way down to `Upload Plugin`.

# Verifying the Release

In order to verify the release, you'll need to have `gpg` or `gpg2` installed on your system. You'll first need to import the keys that have signed this release if you haven't done so already:

```
curl https://boltz.exchange/static/boltz.asc | gpg --import
```

Once you have the required PGP keys, you can verify the release (assuming `SHA256SUMS` and `SHA256SUMS.sig` are in the current directory) with:

```
gpg --verify SHA256SUMS.sig
```

You should see the following if the verification was successful:

```
gpg: assuming signed data in 'boltz-client-manifest-v2.10.2.txt'
gpg: Signature made Wed Dec 17 23:38:35 2025 CET
gpg:                using RSA key 8918FFBFFB49E93EF256D930542A7F22A3BD9CB0
gpg: Good signature from "Boltz (Boltz signing key) <admin@bol.tz>" [unknown]
Primary key fingerprint: 8918 FFBF FB49 E93E F256  D930 542A 7F22 A3BD 9CB0

```

You should also verify that the hashes still match with the archive you've downloaded.

```
sha256sum --ignore-missing -c SHA256SUMS
```

If your archive is valid, you should see the following output:

```
BTCPayServer.Plugins.Boltz.btcpay.json: OK
BTCPayServer.Plugins.Boltz.btcpay: OK
```
