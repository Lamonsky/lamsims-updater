# lamsims-updater

Hello!

This Project is called lamsims-updater, but the truth is that for now it is only DLC Downloader and DLC Unlocker.

What does it mean for you?
You need legit Sims 4 Base Game from EA App.
This app only allow for downloading DLCs and packs and Unlocks them for your Legit Base Game.

## Requirements
App works on Windows, Linux and macOS, you still need a legit base game.
The DLC unlocker needs the EA App or Origin installed. On Windows that is the normal install, on
Linux it is an install inside a Wine or Proton prefix. There is no unlocker on macOS.

## How to use?
1. Download the build for your platform from the Releases section:
   - Windows: `lamsims-updater-win-x64.exe`
   - Linux: `lamsims-updater-linux-x64`
   - macOS (Apple Silicon): `lamsims-updater-osx-arm64`
   - macOS (Intel): `lamsims-updater-osx-x64`

   You can check it against `SHA256SUMS` if you want to be sure the download is good.

   On Linux and macOS you have to make the file executable first, run `chmod +x lamsims-updater-*`.
   The macOS build is not notarized so Gatekeeper will block it. Run
   `xattr -d com.apple.quarantine lamsims-updater-osx-*` to get around that.

   On Linux the unlocker looks through the Wine and Proton prefixes your launchers made — Steam,
   Lutris, Heroic and Bottles, Flatpak installs too — and gives you a row for every EA App or
   Origin it finds. If yours is somewhere it does not look, use the Browse button next to "Wine
   prefix" to point it at the prefix folder. Close the client, the game and the launcher window
   before you install: the override has to be written while the prefix is idle, and the app will
   refuse rather than risk it.

   On macOS you can still download and install packs, the unlocker part is just hidden.
1. Run the app.
1. Paste this into the `Catalog:` box at the top and hit Load:

   ```
   https://raw.githubusercontent.com/Lamonsky/lamsims-updater/master/catalog.live.json
   ```

   That is the pack list, so the table stays empty until it loads. You only do this once — the app
   saves it and picks it up again next time you start.

   It also keeps a copy of the last list it loaded. If GitHub is unreachable later, hit "Use cached
   copy" and carry on with that one.
1. Select Browse and select your Sims 4 Base Game folder (Usually located in C:\Program Files\Electronic Arts\The Sims 4)
1. Select Scan and wait for the app to find your DLCs and Packs that you have already downloaded.
1. Select DLCs you want to download and install.
1. Click Install DLCs and wait for the app to download and install the DLCs you selected.
1. And for last click Install DLC Unlocker and wait for the app to unlock the DLCs you selected.
1. Enjoy your new DLCs and Packs!


## Pretty important info
If you encounter issues with downloading every dlc at one, or a lot of dlc at once (from my research it is either more then 2 EP or more then 5 SP), try downloading individually.
Just remember to hit 'Scan' every time you finish downloading an DLC so app will refresh info and not download the ones already downloaded.

## Disclaimer
Im planning to add updater for the game itself, but it will take more time.

## Patreon
If you want to support my work and let me focus more on the app you can do that on Patreon
[Pateron](https://patreon.com/LamonSky?utm_medium=unknown&utm_source=join_link&utm_campaign=creatorshare_creator&utm_content=copyLink)
