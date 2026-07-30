# Carry

Carry is a small, offline Windows migration app for moving:

- everything inside the current user's Documents folder;
- VS Code settings, keybindings, snippets, and profiles;
- the list of installed VS Code extensions.
- selected reinstallable applications discovered through Windows Package Manager;
- automatically detected system dependencies such as classic .NET Framework 2.x–4.x,
  modern .NET runtimes/SDKs, ASP.NET, Visual C++ redistributables, Node.js, Python,
  and Java inventory;
- a review-only report of files that appear not to have been active recently.

It creates an ordinary, inspectable transfer folder on a USB/external drive or network
share. Copies are first written as `.carrypart` files and renamed only after the write
finishes, so an interrupted transfer does not look complete.

## Use

1. Run `Carry.exe` on the old laptop.
2. Choose **Back up this laptop** and select an external drive.
3. Move the drive and `Carry.exe` to the new laptop.
4. Install VS Code on the new laptop if it is not already installed.
5. Run `Carry.exe`, choose **Restore on new laptop**, then select the created
   `Carry-<computer>-<date>` folder.

Existing files are kept by default during restore. Select the replace option only if the
old laptop's copy should win.

Use **Choose apps & review old files** to select individual applications and inspect
possibly inactive files. Applications are reinstalled on the new laptop rather than copied
from `Program Files`, because installed programs depend on services, registry entries, and
shared runtimes. Carry also copies matched Local, LocalLow, and Roaming AppData folders for
selected apps, including profiles, preferences, history, local databases, and other
application-owned files. Close selected apps before backup so their databases are not
locked or changing during the copy.

The inactive label is an estimate based on the best timestamps Windows provides; Carry
never deletes or silently excludes those files. Passwords, cookies, credentials, and
license tokens that Windows or the application encrypts for the original device/account
are copied but may not decrypt on the new laptop, so some apps can still require sign-in
or reactivation.

System components are recorded automatically in `system-components.json`. Carry installs
supported components through Windows Package Manager before restoring applications.
Classic .NET Framework Windows Features are enabled through Windows itself (which can show
an administrator prompt). Components without a safe automatic package are listed as a
warning for manual installation.

The review window calculates an estimated total before backup: Documents, VS Code user data,
and the data folders belonging to selected apps. The final transfer screen shows the actual
number of bytes copied.

## Build

```powershell
dotnet test apps/Carry/tests/Nullprice.Carry.Core.Tests -c Release
dotnet publish apps/Carry/app/Nullprice.Carry.App -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o apps/Carry/dist
```

The application does not upload data, require an account, or modify the old laptop's
Documents or VS Code data.
