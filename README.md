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
apps/Carry/                         laptop migration — Documents + VS Code
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
dotnet run --project apps/Carry/app/Nullprice.Carry.App

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

`store/feed/` is gitignored because it holds large binaries. To recreate an entry:

```powershell
# Ferry
dotnet publish apps/Ferry/app/Nullprice.Ferry.App/Nullprice.Ferry.App.csproj `
  -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o store/feed/tmp
Move-Item store/feed/tmp/Nullprice.Ferry.App.exe store/feed/Ferry-0.1.0-portable.exe -Force
Remove-Item store/feed/tmp -Recurse -Force
(Get-FileHash store/feed/Ferry-0.1.0-portable.exe -Algorithm SHA256).Hash.ToLower()

# Batch — same shape, swap the project and output name
```

Put that hash and the file's byte size into `catalogue.json`. The store tests assert that
anything marked `available` has a real 64-character checksum and a non-zero size, so a
stale entry fails loudly rather than shipping a broken download.

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

## Installing, updating, and releases

### What installing actually does

Installing is four things, and a failed install must leave none of them behind:

1. the binary, in a version-stamped folder under `%APPDATA%\Nullprice\apps\<id>\<version>\`
2. a **Desktop shortcut**
3. a **Start Menu shortcut**, under a Nullprice folder
4. an **Add/Remove Programs entry**, so Windows can uninstall it the normal way

Per-user rather than machine-wide on purpose: `HKCU` and `%APPDATA%` need no administrator
prompt, which matters for tools people are only trying out. The cost is that an app is
installed for one account rather than for everyone.

`installer.js` tracks which of the four steps succeeded and undoes them in reverse on
failure, so a crash midway cannot leave an orphan shortcut pointing at nothing or a
phantom entry in Apps & Features. That rollback is tested.

Windows invokes the uninstall through `Nullprice.exe --uninstall <id>`, so the store can
launch headless as an uninstaller rather than only as a window.

### How updates work

Every tool has **its own GitHub repository and its own releases**, so shipping a fix to one
never means re-releasing the other nine. The store polls each app's repo, compares the tag
against what is installed, and offers whatever is newer. The store updates itself the same
way, through `electron-updater`.

GitHub publishes no checksum for release assets, so a release should include a sibling
`<asset>.sha256` file. The store fetches it *before* the payload — discovering afterwards
that the expected hash is unreachable would mean having already written an unverifiable
binary. If no checksum is published the download still works, and the UI says plainly that
it was not verified rather than implying it was.

A malformed tag can never look like an upgrade: unparseable versions sort *below* parseable
ones, so a bad tag on the server cannot nag every user forever. Drafts and prereleases are
ignored.

### Cutting a release

```powershell
.\release-app.ps1 -App Ferry -Version 0.2.0          # build + update local catalogue
.\release-app.ps1 -App Ferry -Version 0.2.0 -Push    # also publish a GitHub release
```

Without `-Push` this only builds and points the local feed at the new version, so the store
can install it without anything being published.

### To go live — what is still needed

Nothing here has been verified against a real GitHub repository, because this repo has no
remote yet. The GitHub path is built and unit-tested against a fake, not proven end to end.

1. Create a repo for the store and one per tool (`nullprice`, `nullprice-ferry`,
   `nullprice-batch`).
2. Replace every `REPLACE-ME` owner in `store/catalogue.json` and
   `store/electron-builder.yml`.
3. Flip each app's `updates.provider` from `local` to `github`.
4. `winget install GitHub.cli`, then `gh auth login`.
5. `git remote add origin …` and push.

## The catalogue

| Tool | Does | Replaces | Their price | Status |
| --- | --- | --- | --- | --- |
| Ferry | Verified file copy | TeraCopy Pro | ~$25 | **Built** |
| Batch | Bulk image conversion | assorted | ~$30 | **Built** |
| Capture | Screen capture and annotation | Snagit | $39/yr | Planned |
| Sheaf | Local PDF merge, split, compress | Acrobat Standard | ~$155/yr | Planned |
| Compare | File and folder diff | Beyond Compare | $34.30 | Planned |
| Expand | Text expansion and snippets | PhraseExpress | ~$50 | Planned |
| Clip | Searchable clipboard history | ClipboardFusion Pro | ~$25 | Planned |
| Purge | Uninstaller with leftover cleanup | Revo Uninstaller Pro | $24.95 | Planned |
| Corral | Desktop icon grouping | Stardock Fences | $29.99 | Planned |
| Span | Multi-monitor taskbars and layouts | DisplayFusion | $34 | Planned |

Nine tools cannot be built at once, and the reason is that each one's hard part is a
different specialist problem: an annotation editor for Capture, a PDF content-stream
writer for Sheaf, a global keyboard hook for Expand, AppBar APIs for Span, drawing over
the Explorer desktop for Corral. Batch was built second because it is the only one whose
hard part is ordinary.

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

## Batch

Ordered image pipeline over a folder tree. Resize, convert, watermark, strip metadata.

`Nullprice.Batch.Core` holds the whole model with no imaging dependency at all — even the
resize arithmetic, so `ResolveFor` is unit-tested without decoding a pixel. The one thing
Core cannot do is turn pixels into other pixels, which is behind `IImageProcessor`. The
real implementation uses Windows Imaging Component via WPF: it ships with Windows, is
hardware accelerated, and adds no dependency or licence to a tool being given away.

"Non-destructive by default" is enforced rather than promised. `BatchPlanner` refuses to
build a runnable plan when:

- the output folder is also a source folder, or
- two inputs would resolve to the same output name — with the fix named in the message
  ("add `{n}` to the name template").

Both are ways to destroy someone's photographs, so both are tested rather than trusted to
the UI. Order in the pipeline is never rearranged automatically, because resizing before
watermarking gives a different result from the reverse.

Two smaller decisions worth knowing:

- An unknown token like `{nope}` is left **visible** in the output name rather than
  dropped. Silently dropping it would collapse every file onto one name.
- Files are written to a `.part` neighbour and moved into place, so an interrupted run
  never leaves a truncated image that looks finished.

33 tests cover naming, resize arithmetic, pipeline ordering, both plan guards, cancellation,
and the promise that sources are byte-identical afterwards.

## Build order

Batch was built second because it is the only one of the ten whose hard part is ordinary.
The remaining eight are ordered by how contained their hard part is — Compare and Clip
next, Corral and Span last among the catalogue tools.

**Carry / Migration is last of all.** It already exists in `apps/Carry` as a working
laptop-migration tool, it is in active use, and it is deliberately excluded from
`Nullprice.slnx` and from the catalogue until everything else is done.

## Known gaps

Verification status is deliberately explicit here, because several things are built and
tested but have never been run against the real world.

**Never verified end to end:**

- **The GitHub update path.** There is no remote on this repo, so release lookup is proven
  only against an injected fake. Nothing has talked to `api.github.com`.
- **A real install.** The install sequence and its rollback are tested with injected
  platform primitives; no Desktop shortcut has actually been written by this code and no
  app has been installed and launched from the store window.
- **Store self-update.** `electron-updater` is wired and only runs in a packaged build.
  No packaged build has been made — `npm run dist` has never been run.
- **Batch processing a real image.** Its Core is well tested, but the WIC path (decode →
  scale → watermark via `RenderTargetBitmap` → encode, on a background thread) has never
  processed a photograph.

**Verified in isolation:** `reg.exe` produces an entry Windows will list in Apps &
Features, and quotes inside an `UninstallString` survive Node's `execFile`, so a path
containing spaces will not break the uninstall button.

**Other gaps:**

- No code signing, so SmartScreen will warn on anything distributed.
- Ferry and Batch shells are scaffolds — plain code-behind, no MVVM, no drag and drop, no
  persisted settings.
- Ferry has no resume across application restarts. Cancellation is clean, but no state is
  written to disk.
- Eight of the ten tools are catalogue entries only.
- The hub duplicates catalogue data that also lives in `store/catalogue.json`. Two places
  to update is a real maintenance smell and should collapse into one source.
