# Code signing & notarization

The release workflow (`.github/workflows/build.yml`) signs the Windows `.exe` and signs + notarizes the macOS
`.app` bundles **only when the secrets below are present**. Until then, releases build and publish exactly as
before — unsigned. Nothing here changes the app's behavior; it changes what the operating systems say when a new
user first runs it.

Why it matters: an unsigned `.exe` triggers Windows SmartScreen ("Windows protected your PC"), and an unsigned
`.app` is blocked outright by macOS Gatekeeper. For a non-technical patient audience, both read as "this is malware."

All secrets go in **GitHub → repo Settings → Secrets and variables → Actions → New repository secret**. The exact
name on the left must match; the workflow keys off them.

---

## Windows — Azure Trusted Signing (recommended)

Microsoft's cloud signing service. **~$9.99/month**, no hardware token, and the signature accrues SmartScreen
reputation over time. (The alternative — a traditional OV/EV code-signing certificate from a CA like DigiCert or
Sectigo, ~$200–400/yr, EV requiring a USB token — also works, but Trusted Signing is cheaper and CI-friendly.)

### One-time setup

1. **Azure account:** sign in at <https://portal.azure.com> (a free Azure account is fine; you pay only for the
   Trusted Signing resource).
2. **Register the resource provider:** Portal → Subscriptions → your subscription → *Resource providers* → search
   **Microsoft.CodeSigning** → Register.
3. **Create a Trusted Signing account:** Portal → search **Trusted Signing Accounts** → Create. Pick a region, a
   resource group, and a name (this is `TRUSTED_SIGNING_ACCOUNT`). Note the account's **endpoint** URI shown on its
   Overview page, e.g. `https://eus.codesigning.azure.net` (this is `TRUSTED_SIGNING_ENDPOINT`).
4. **Identity validation:** in the Trusted Signing account → *Identity validations* → start one. As an individual,
   you validate your legal identity (ID + a few details); this can take a few days. **Signing can't begin until this
   is Approved.**
5. **Create a Certificate Profile:** once identity is approved → *Certificate profiles* → Create → type
   **Public Trust** → give it a name (this is `TRUSTED_SIGNING_PROFILE`).
6. **Create an App Registration (so CI can authenticate):** Portal → *Microsoft Entra ID* → *App registrations* →
   New registration → name it e.g. `gci-signing`. On its Overview copy the **Application (client) ID**
   (`AZURE_CLIENT_ID`) and **Directory (tenant) ID** (`AZURE_TENANT_ID`). Then → *Certificates & secrets* → New
   client secret → copy the secret **Value** immediately (`AZURE_CLIENT_SECRET`).
7. **Grant that app permission to sign:** Trusted Signing account → *Access control (IAM)* → Add role assignment →
   role **Code Signing Certificate Profile Signer** → assign to the `gci-signing` app registration.

### Secrets to set

| Secret | Value |
|---|---|
| `TRUSTED_SIGNING_ENDPOINT` | the account's endpoint URI (step 3) |
| `TRUSTED_SIGNING_ACCOUNT` | the Trusted Signing account name (step 3) |
| `TRUSTED_SIGNING_PROFILE` | the certificate profile name (step 5) |
| `AZURE_TENANT_ID` | Directory (tenant) ID (step 6) |
| `AZURE_CLIENT_ID` | Application (client) ID (step 6) |
| `AZURE_CLIENT_SECRET` | client secret Value (step 6) |

The workflow enables Windows signing automatically once `TRUSTED_SIGNING_ACCOUNT` is set. Signing happens **before**
the `gci.exe.sha256` is computed, so the checksum (and the in-app updater) match the signed binary.

---

## macOS — Apple Developer ID + notarization

### One-time setup

1. **Enroll in the Apple Developer Program:** <https://developer.apple.com/programs/> — **$99/year**. Individual
   enrollment is fine. After enrollment, find your **Team ID** at
   <https://developer.apple.com/account> → Membership details (`APPLE_TEAM_ID`, a 10-character string).
2. **Create a "Developer ID Application" certificate.** Easiest via Xcode on your Mac: Xcode → Settings → Accounts →
   add your Apple ID → *Manage Certificates* → **+** → **Developer ID Application**. (Or do it manually at
   <https://developer.apple.com/account/resources/certificates> by uploading a CSR from Keychain Access.)
3. **Export the cert as a `.p12`:** open **Keychain Access** → *My Certificates* → right-click the
   "Developer ID Application: <your name> (<team id>)" entry → **Export** → `.p12` → set a password
   (this is `MACOS_CERT_PASSWORD`). Then base64-encode it for the secret:
   ```bash
   base64 -i DeveloperID.p12 | pbcopy   # now paste as MACOS_CERT_P12_BASE64
   ```
4. **Create an app-specific password for notarization:** <https://account.apple.com> → Sign-In & Security →
   *App-Specific Passwords* → generate one named e.g. `gci-notary` (this is `APPLE_APP_PASSWORD`). Your Apple ID
   email is `APPLE_ID`.

### Secrets to set

| Secret | Value |
|---|---|
| `MACOS_CERT_P12_BASE64` | base64 of the exported `.p12` (step 3) |
| `MACOS_CERT_PASSWORD` | the `.p12` export password (step 3) |
| `APPLE_TEAM_ID` | your 10-char Team ID (step 1) |
| `APPLE_ID` | your Apple ID email (step 4) |
| `APPLE_APP_PASSWORD` | the app-specific password (step 4) |

The workflow enables macOS signing automatically once `MACOS_CERT_P12_BASE64` is set. It signs each `.app` with the
hardened runtime, submits it to Apple's notary service (`notarytool ... --wait`), and staples the ticket, so the
first launch on any Mac has no Gatekeeper warning.

---

## How to test it

Signing runs only on a version tag (`v*`). After adding the secrets, cut a normal release tag and watch the run:

- The Windows job's summary prints `signed: true`.
- The macOS "Sign & notarize" step prints a signing identity and the notary result; a failure there means a secret
  is wrong or identity validation isn't approved yet.

To verify a downloaded build by hand:

```bash
# Windows (PowerShell): should show a valid signature
Get-AuthenticodeSignature .\gci.exe

# macOS: should say "accepted" / "Notarized Developer ID"
codesign --verify --deep --strict --verbose=2 /Applications/GCI.app
spctl -a -vvv -t install /Applications/GCI.app
```

Nothing is signed until the secrets exist, so you can add Windows and macOS independently, in either order.
