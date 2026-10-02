---
name: jellyfin-version-compat
description: Keeps the FullUI plugin working on every supported Jellyfin server version (10.11.6 and later). Use whenever you add or change C# code that calls a Jellyfin/MediaBrowser API (ILibraryManager, IUserManager, IUserDataManager, ISessionManager, IPlaylistManager, scheduled tasks, InternalItemsQuery, DTOs), bump Jellyfin.Controller, edit targetAbi in manifest/make_release, or when someone reports MissingMethodException/TypeLoadException on a particular Jellyfin version.
---

# Supporting Jellyfin 10.11.6 and newer

## Why this needs care
A plugin is a compiled DLL bound to Jellyfin's method signatures. Jellyfin changed public APIs inside the 10.11 line: for example `IUserManager.Users`/`UsersIds` (10.11.6-10.11.8) were replaced by `GetUsers()`/`GetUsersIds()` (10.11.11). A DLL built against either shape throws `MissingMethodException` at runtime on the other, even though it compiled fine. Unit tests never see this because they run against one version.

## The policy
1. **Compile against the oldest supported version.** `Jellyfin.Plugin.FullUI.csproj` pins `Jellyfin.Controller` to `10.11.6`. The compiler then rejects any API newer than that. Do not float it (`10.11.*`) or raise it to make something compile: raising the floor drops users.
2. **Bridge APIs that changed.** Where an API differs across the range, bind at runtime in `Compat/` (see `UserManagerCompat`, which reflects over the interface and caches a delegate). Route every call through the shim and add a unit test.
3. **Prove it with the ABI matrix.** `tools/abi-matrix.sh` builds `tools/abicheck`, restores Jellyfin.Controller for each version (10.11.6 through 10.11.11 by default), and verifies that every Jellyfin/MediaBrowser type, method and field the built plugin references exists with an identical signature. Run it after every change that touches Jellyfin APIs and in CI/release:
   `cd server && dotnet build -c Release && cd .. && tools/abi-matrix.sh`
   Expected output: `0 problem(s)` for each version. A failure names the missing member, e.g. `MISSING/CHANGED MEMBER ...IUserManager::GetUsersIds`.
4. **When a new Jellyfin release appears**, append its version to the matrix (`tools/abi-matrix.sh <dll> 10.11.12`). If it reports problems, add a `Compat/` shim rather than raising the floor.
5. **targetAbi** in the plugin repository manifest is `10.11.6.0`; the installer refuses older servers with a plain-English message.

## Limits of the checker
It compares names and signatures, not behaviour. Behaviour that differs by version (query semantics, event timing, permissions) still needs a live server on the oldest and newest supported version. Note 10.11.1 was never published to NuGet, so it is not in the matrix.

## Checklist before merging a server change
- `dotnet build -c Release` (0 warnings), `dotnet test` from `server/`, `tools/abi-matrix.sh` all green.
- No new direct call to an API that appears only in newer versions (the compiler will reject it; do not work around by bumping the pin).
