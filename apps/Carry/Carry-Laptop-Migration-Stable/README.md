# Carry — quick start

1. Extract this ZIP.
2. Run `Carry.exe` on the old laptop.
3. Choose **Back up this laptop** and select an external drive or shared folder.
4. Choose the apps to carry, close those apps, and start the transfer.
5. On the new laptop, run the same `Carry.exe`.
6. Choose **Restore on new laptop** and select the created `Carry-...` folder.

Carry transfers Documents, VS Code settings/extensions, selected application data and
profiles, and an automatic inventory of .NET Framework/.NET/ASP.NET/Desktop runtimes,
SDKs, Visual C++ components, Node.js, Python, and Java.

Use **Extra folders outside Documents** to add any other folder. Carry records its original
location and restores it automatically. Locations below the old user profile are translated
to the new user's profile; locations on other drives keep their exact absolute path.
Protected system locations can require administrator permission, and a missing destination
drive must be connected before restore.

The review screen estimates the total selected size. Scanning and size calculation run in
the background, so the window remains usable. App-data sizes are calculated only for apps
you select.

Close selected apps before backup. Device-encrypted passwords, cookies, credentials, and
license activations are copied when present but may still require sign-in or reactivation
on the new Windows account.
