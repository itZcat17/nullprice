# Nullprice

Free rebuilds of paid Windows tools, published from a single catalogue.

Ten tools are planned. Each one does the job of something that currently charges for it,
with no subscription, no account, no upload, and no telemetry.

## Layout

```
hub/index.html                     the catalogue site
apps/Ferry/
  src/Nullprice.Ferry.Core/        engine — no UI dependency, fully testable
  tests/Nullprice.Ferry.Core.Tests/
  app/Nullprice.Ferry.App/         WPF shell
```

Every app follows the same shape: a `.Core` library holding all the logic with no UI
types in it, a test project against that library, and a thin shell. The logic is where the
value is and where the bugs are, so it stays independently testable.

## Building

Requires the .NET 10 SDK.

```powershell
dotnet build Nullprice.slnx
dotnet test  Nullprice.slnx
dotnet run --project apps/Ferry/app/Nullprice.Ferry.App
```

Open `hub/index.html` in a browser for the catalogue.

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
