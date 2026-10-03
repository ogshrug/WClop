# Releasing WClop

For maintainers: how releases are built and published, and the one-time setup for code signing, winget and Scoop. Users don't need any of this; see the [README](../README.md) to install WClop.

A release is a tag. Bump `<Version>` in `Directory.Build.props`, commit, then:

```powershell
git tag v1.2.3
git push origin v1.2.3
```

[`.github/workflows/release.yml`](../.github/workflows/release.yml) then:

1. **Builds both installers** in parallel: `WClop-<version>-x64.msi` (runs the tests first) and `WClop-<version>-arm64.msi`.
   Each one is `scripts/fetch-tools.ps1 -Arch <arch>`, `scripts/package.ps1 -PublishOnly`, signing of WClop's own binaries,
   `scripts/package.ps1 -MsiOnly`, signing of the MSI, and a `.sha256` file.
2. **Publishes the GitHub release** with both MSIs, their checksums, and fixed-name copies `WClop-x64.msi` and
   `WClop-arm64.msi`, so `releases/latest/download/WClop-x64.msi` (the README's button) always points at the newest one.
   The in-app updater picks the installer for the PC it runs on (`WClop-<version>-arm64.msi` on Windows on Arm, x64
   otherwise, and x64 on Arm if a release has no arm64 installer).
3. **Opens a winget-pkgs pull request** for the new version, if winget is set up (below).

Signing and winget are skipped, with a note in the log, until their secrets exist; releases work without either.
You can also re-run a release for an existing tag from *Actions → Release → Run workflow*.

## Code signing (Azure Artifact Signing)

Unsigned installers get SmartScreen's *Windows protected your PC* warning and more antivirus suspicion. Azure
Artifact Signing (called Trusted Signing until 2025) is Microsoft's managed code-signing service: no certificate file to
keep safe, and the certificate chains to a root Windows trusts. The workflow signs `WClop.exe`, `WClop.dll`,
`WClop.Core.dll`, `wclop-cli.exe` and `wclop-cli.dll` before they go into the MSI, then the MSI itself, and checks every
signature. The bundled tools (ffmpeg, Ghostscript, …) are other projects' binaries and aren't re-signed.

Signing doesn't remove the SmartScreen warning on day one: SmartScreen also uses reputation, which builds up as people
download signed releases. It does make that happen much sooner, and the publisher name shows in the UAC/installer dialogs.

### Cost and eligibility

- **Basic** tier: about **US$9.99 a month**, including 5,000 signatures (one release signs 12 files: 5 binaries and an
  MSI per architecture). **Premium** (about US$99.99 a month, 100,000 signatures) isn't needed. Check the
  [pricing page](https://azure.microsoft.com/pricing/details/artifact-signing/) for current prices.
- **Public Trust** certificates (the kind SmartScreen trusts) are for **individual developers in the US or Canada**, or
  **organisations** in the US, Canada, the EU, the UK and a few other countries. For an individual, the Azure billing
  account must be of type *Individual*, with your legal name and address matching your government ID.
  Identity validation takes from minutes (individuals, with Microsoft Authenticator and an ID check) to 1–20 business
  days (organisations).

### One-time setup in Azure

Follow Microsoft's [quickstart](https://learn.microsoft.com/azure/artifact-signing/quickstart); in short:

1. **Subscription**: an Azure subscription with the `Microsoft.CodeSigning` resource provider registered
   (*Subscriptions → Resource providers*).
2. **Artifact Signing account**: *Artifact Signing Accounts → Create*, Basic tier, in a supported region. Note its
   **endpoint**, which depends on the region, e.g. `https://eus.codesigning.azure.net` (East US),
   `https://weu.codesigning.azure.net` (West Europe), `https://neu.codesigning.azure.net` (North Europe).
3. **Identity validation**: give yourself the *Artifact Signing Identity Verifier* role on the account (*Access control
   (IAM)*), then *Identity validations → New identity → Public*, Individual or Organization, and complete it.
4. **Certificate profile**: *Certificate profiles → Create → Public Trust*, using that identity validation. Note its name.
5. **An app for GitHub to sign in as**: *Microsoft Entra ID → App registrations → New registration* (e.g.
   `wclop-release-signing`). Note its **Application (client) ID** and the **Directory (tenant) ID**. Then either:
   - **Client secret** (simplest): *Certificates & secrets → New client secret*; note its value. It expires (up to 2 years),
     so put a reminder in your calendar.
   - **or OIDC, no secret to rotate**: *Certificates & secrets → Federated credentials → Add credential → GitHub Actions
     deploying Azure resources*, organisation `ogshrug`, repository `WClop`, entity type **Environment**, environment
     `release` (the build job runs in that environment so the subject is the same for every tag). Leave
     `AZURE_CLIENT_SECRET` out and the workflow signs in with `azure/login` instead.
6. **Permission to sign**: on the certificate profile (or the account), *Access control (IAM) → Add role assignment →
   Artifact Signing Certificate Profile Signer*, assigned to the app from step 5.

### Secrets and variables in GitHub

*Repository → Settings → Secrets and variables → Actions*:

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `AZURE_TENANT_ID` | Directory (tenant) ID |
| Secret | `AZURE_CLIENT_ID` | the app's Application (client) ID |
| Secret | `AZURE_CLIENT_SECRET` | the client secret's value (leave out for OIDC) |
| Variable | `AZURE_SIGNING_ENDPOINT` | e.g. `https://eus.codesigning.azure.net` |
| Variable | `AZURE_SIGNING_ACCOUNT` | the Artifact Signing account name |
| Variable | `AZURE_SIGNING_PROFILE` | the certificate profile name |

Signing starts with the next release. Once it works, remove the README's "isn't code-signed yet" paragraph.

Local builds can still sign with a certificate from your certificate store:
`./scripts/package.ps1 -CertThumbprint <thumbprint>` (or `$env:WCLOP_SIGN_THUMBPRINT`).

## winget

The package is `ogshrug.WClop`. The manifest templates are in [`packaging/winget`](../packaging/winget): per-user
(`Scope: user`) WiX MSI, the MSI's ProductCode and UpgradeCode, `winget install --silent` passing `LAUNCHAPP=0` (the
default silent-with-progress mode starts WClop, so `winget upgrade` brings back the copy the installer closed), and
`wclop` as the command. Users can pass the installer's other properties with `--custom`, e.g.
`winget install ogshrug.WClop --custom "EXPLORERMENU=0 LAUNCHATLOGIN=0"`.

### First submission (by hand, once)

The release workflow can only update a package that's already in winget, so the first version goes in by hand:

1. After a release, write its manifests (hashes and ProductCodes come from the published MSIs):

   ```powershell
   ./scripts/winget-manifest.ps1 -Version 1.2.3 -FromRelease
   winget validate --manifest artifacts\winget\manifests\o\ogshrug\WClop\1.2.3
   ```

   To try it: `winget settings --enable LocalManifestFiles` (as administrator), then
   `winget install --manifest artifacts\winget\manifests\o\ogshrug\WClop\1.2.3`.
2. Fork [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), add the folder as
   `manifests/o/ogshrug/WClop/1.2.3/`, and open a pull request (or let
   [wingetcreate](https://github.com/microsoft/winget-create) do it: `wingetcreate submit --token <PAT> <folder>`).
   Microsoft's bots validate and install it in a sandbox; a moderator merges it, usually within a few days.

### Automatic updates

Then, for every release after that:

1. Keep the fork of `winget-pkgs` under the `ogshrug` account.
2. Create a **classic** personal access token with the `public_repo` scope (*GitHub → Settings → Developer settings*).
3. Add it as the repository secret `WINGET_TOKEN`.

The workflow's `winget` job then runs [winget-releaser](https://github.com/vedantmgoyal9/winget-releaser) (Komac),
which reads the versioned MSIs from the release, keeps the rest of the previous manifest (switches, descriptions) and
opens the pull request.

## Scoop

[`packaging/scoop/wclop.json`](../packaging/scoop/wclop.json) is a Scoop manifest. Scoop doesn't run the MSI: it unpacks
it into Scoop's own folder, puts `wclop` on the PATH via a shim, adds a *Scoop Apps* Start menu entry, and runs
`WClop.exe --register` / `--unregister` for the Explorer menu and Send To. Updates come from `scoop update`; WClop's own
updater only updates MSI installs. `checkver` follows the GitHub releases and `autoupdate` fills in the new URLs and
hashes from the `.sha256` files.

Scoop needs the manifest in a bucket (a git repository of manifests). Either:

- **Your own bucket**: create `ogshrug/scoop-bucket` from
  [ScoopInstaller/BucketTemplate](https://github.com/ScoopInstaller/BucketTemplate) and copy the manifest to `bucket/wclop.json`.
  The template's Excavator workflow runs `checkver` every few hours and commits new versions on its own. Users:
  `scoop bucket add wclop https://github.com/ogshrug/scoop-bucket` then `scoop install wclop/wclop`.
- **or the Extras bucket**: open a pull request on [ScoopInstaller/Extras](https://github.com/ScoopInstaller/Extras)
  with the manifest; then users only need `scoop bucket add extras` and `scoop install wclop`.

The manifest lists only the x64 installer because v0.14.2 has no arm64 one, and Scoop's autoupdate only refreshes the
architectures a manifest already has. After the first release with `WClop-<version>-arm64.msi`, add an `arm64` entry next
to `64bit` under `architecture` (the `autoupdate` section already has it) and autoupdate keeps both current.

Once `winget install ogshrug.WClop` or the Scoop bucket works, add those install lines to the README's Install section
(it leaves them out until then, so nobody is sent to a package that doesn't exist yet).

## Windows on Arm

`WClop-<version>-arm64.msi` contains native arm64 builds of WClop, the `wclop` command, and ffmpeg/ffprobe (BtbN's GPL
build, which has every encoder WClop uses; gyan.dev doesn't build for Arm). pngquant, jpegoptim, gifsicle, ExifTool and
Ghostscript have no arm64 Windows builds, so the arm64 installer ships their x64 builds and Windows 11 runs them under
emulation (with the x64 Visual C++ runtime next to them). Windows 10 on Arm can only emulate 32-bit x86, so the arm64
installer refuses to install there ("WClop for Arm needs Windows 11"). Both installers share an UpgradeCode, so
installing one over the other replaces it.

To build it locally (from an x64 PC too):

```powershell
./scripts/fetch-tools.ps1 -Arch arm64     # into tools-arm64\
./scripts/package.ps1 -Arch arm64         # artifacts\WClop-<version>-arm64.msi
```
