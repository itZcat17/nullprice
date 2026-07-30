# Nullprice

Free rebuilds of paid Windows tools, published from a single catalogue.

Ten tools are planned. Each one does the job of something that currently charges for it,
with no subscription, no account, no upload, and no telemetry.

## Layout

```text
store/                             the desktop store — Electron, packaged with NSIS
  catalogue.json                   the feed every view reads from
  src/main.js                      privileged process: window, IPC, install
  src/downloader.js                fetch and verify — no Electron import, testable alone
  src/preload.js                   the entire renderer-facing API surface
  src/renderer/                    catalogue, detail pages, downloads
  test/                            download pipeline tests
  feed/                            local test feed (gitignored, rebuildable)
hub/index.html                     the same catalogue as a standalone web page
apps/Ferry/
  src/Nullprice.Ferry.Core/        engine — no UI dependency, fully testable
  tests/Nullprice.Ferry.Core.Tests/
  app/Nullprice.Ferry.App/         WPF shell
```

Every component follows the same shape: the logic lives in a module with no UI or
framework dependency, has tests against it, and a thin shell drives it. The logic is where
the value is and where the bugs are, so it stays independently testable.

## Building

Requires the .NET 10 SDK and Node 20 or later.

```powershell
# the tools
dotnet build Nullprice.slnx
dotnet test  Nullprice.slnx
dotnet run --project apps/Ferry/app/Nullprice.Ferry.App

# the store
cd store
npm install
.\repair-electron.ps1    # see note below — often needed after install
npm start                # run it
npm test                 # download pipeline tests
npm run dist             # build Nullprice-Setup-0.1.0.exe into store/dist
```

### If `npm start` says "Electron failed to install correctly"

This happened on first install here and is worth knowing about. npm's electron postinstall
exited 0 having downloaded the 136 MB zip into `%LOCALAPPDATA%\electron\Cache` but extracted
almost none of it — `node_modules/electron/dist` ended up containing only `locales`, with no
`electron.exe` and no `path.txt`. Reinstalling does not help, because the zip is already
cached so the postinstall skips straight to a no-op.

`store\repair-electron.ps1` finishes the extraction from the cached zip. It is idempotent
and exits immediately if Electron is already fine.

Separately, if your shell has `ELECTRON_RUN_AS_NODE=1` set, Electron will run as plain Node
and never open a window. Some editor-integrated terminals set it. Clear it first:

```powershell
Remove-Item env:ELECTRON_RUN_AS_NODE -ErrorAction SilentlyContinue
```

`hub/index.html` opens directly in a browser and needs no build.

### Rebuilding the local test feed

`store/feed/` is gitignored because it holds large binaries. To recreate it:

```powershell
dotnet publish apps/Ferry/app/Nullprice.Ferry.App/Nullprice.Ferry.App.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o store/feed
Remove-Item store/feed/*.pdb
Move-Item store/feed/Nullprice.Ferry.App.exe store/feed/Ferry-0.1.0-portable.exe -Force
(Get-FileHash store/feed/Ferry-0.1.0-portable.exe -Algorithm SHA256).Hash.ToLower()
```

Put that hash and the file's byte size into `catalogue.json`. The tests will fail loudly if
they disagree.

## The store

A native window, no server, no account. It reads `catalogue.json`, shows an app-store style
page per tool, and for anything marked available it downloads the release, verifies it
against a published SHA-256, and hands it to the shell to run.

Two decisions worth knowing:

- **The renderer cannot name a URL or a path.** It can only refer to catalogue entries by
  id; the privileged process decides what that means. So a careless or compromised renderer
  cannot be talked into fetching and executing something arbitrary. It runs sandboxed with
  `contextIsolation` on, `nodeIntegration` off, and a restrictive CSP.
- **A download that fails verification is deleted, not quarantined.** That is the opposite
  of Ferry's rule for user data, and deliberately so: a corrupt binary has no evidentiary
  value and every reason not to sit on disk where something might execute it.

Relative release URLs beginning with `./` resolve against `catalogue.json`, which is how the
local feed works. Production entries use absolute `https://` URLs.

## The catalogue

| Tool | Does | Replaces | Their price | Status |
| --- | --- | --- | --- | --- |
| Ferry | Verified file copy | TeraCopy Pro | ~$25 | In development |
| Capture | Screen capture and annotation | Snagit | $39/yr | Planned |
| Sheaf | Local PDF merge, split, compress | Acrobat Standard | ~$155/yr | Planned |
| Compare | File and folder diff | Beyond Compare | $34.30 | Planned |
| Expand | Text expansion and snippets | PhraseExpress | ~$50 | Planned |
| Batch | Bulk image conversion | assorted | ~$30 | Planned |
| Clip | Searchable clipboard history | ClipboardFusion Pro | ~$25 | Planned |
| Purge | Uninstaller with leftover cleanup | Revo Uninstaller Pro | $24.95 | Planned |
| Corral | Desktop icon grouping | Stardock Fences | $29.99 | Planned |
| Span | Multi-monitor taskbars and layouts | DisplayFusion | $34 | Planned |

Prices are as of July 2026; a tilde means approximate. Verified from vendor or reseller
listings: Snagit, Beyond Compare, Fences, DisplayFusion. The rest are estimates and should
be confirmed before they go on the public site.

## How these get built

Each tool is an independent implementation written from scratch. No decompilation, no
borrowed code, no copied icons, names, or trade dress. The paid products are named only to
describe what job the free tool does instead, which is ordinary comparative reference.

This is the same footing GIMP, LibreOffice, Inkscape, and Blender stand on. Functionality
is not copyrightable; specific code, assets, and branding are. Staying on the right side of
that line is a constraint on method, not on ambition.

## Ferry

The first build. Windows Explorer copies files and tells you nothing about whether they
survived; Ferry proves it.

Verification works by re-reading the destination from disk and hashing that, rather than
hashing the buffer on its way out. Hashing what you wrote only confirms what you intended
to write. Hashing what came back confirms what actually landed — which is the entire
failure this tool exists to catch.

Other decisions worth knowing:

- Files are written to a `.ferrypart` neighbour and moved into place only once verified, so
  an interrupted transfer never leaves a truncated file that looks complete.
- A file that fails verification is kept, renamed `.unverified`, and reported. Deleting it
  would destroy the only evidence of what went wrong.
- The whole plan is resolved to a file list before any byte moves, so progress is honest
  instead of a bar that keeps discovering more work.
- `SHA256` is used rather than a faster non-cryptographic hash to avoid a NuGet dependency
  in the scaffold. `XxHash64` from `System.IO.Hashing` would be meaningfully faster and is
  the obvious upgrade.

### Test coverage

16 tests, all passing. They cover round-trip integrity, folder structure preservation, all
four conflict policies, cancellation leaving no partial files, progress reaching totals,
empty files, missing sources, and locale-correct size formatting.

## Known gaps

- Nothing is downloadable. The hub shows every tool's real status and no download button
  goes live until there is a build behind it.
- Ferry's WPF shell is a scaffold — plain code-behind, no MVVM, no drag and drop, no
  persisted settings, no tray behaviour. Built to drive the engine, expected to be replaced.
- Ferry has no resume across application restarts yet. Cancellation is clean but state is
  not written to disk.
- No installer or signing for any app.
- The nine remaining tools are catalogue entries only.
- **This project is not under version control.** Two earlier projects in this workspace
  were permanently lost for exactly that reason. `git init` before doing more work.
