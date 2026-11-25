#           ❄︎ WinterIsComing ❄︎

Let it snow on your Mac! ☃️

Snow falls on your desktop and piles up on your windows and Dock. Roll it into snowballs and build a little snowman. It's all one Swift file, and every flake is drawn in code.

<!-- ![demo](docs/demo.gif) -->

## What it can do ✨

- **Cozy snowfall**: three layers of flakes (near, middle and far) drifting in a gentle wind
- **Snow piles up** on top of your windows, along the Dock and at the bottom of the screen
- **Drag a window and *whoosh*!** The snow flies off. The faster you drag, the farther it goes
- **Roll snowballs** just by moving your mouse sideways over the snow (no clicking!). They grow as they roll
- **Snowballs push each other** along, and one that's pushed off the edge falls down
- **Hold a snowball** with the left mouse button and carry it anywhere
- **Stack three snowballs** and they turn into a snowman with a carrot nose and a red scarf
- **A falling snowball goes *poof*** into a soft heap of snow, ready to be rolled again
- **Fairy lights**: colorful twinkling lights that swing into place along the top of your windows. They're off by default, so turn them on from the ❄︎ menu (or press `L`)

## How to play 🎮

- **Roll a snowball**: slide your mouse sideways over snow on a window, the Dock or the ground
- **Push it**: roll one snowball into another and it gets shoved along
- **Carry it**: hold the left mouse button on a snowball and drag
- **Build a snowman**: stack three snowballs on top of each other ⛄
- **Make it fly**: drag a window and watch the snow whoosh off
- **Hang the lights**: ❄︎ menu → **Fairy Lights** ✨
- **Forgot something?** ❄︎ menu → **How to Play**

## Download 🎁

1. Grab `WinterIsComing-mac-vX.zip` from [Releases](https://github.com/yike-shi/WINTER_IS_COMING/releases/latest) (not the "Source code" ones)
2. Unzip it and drag **WinterIsComing** into your Applications folder
3. Double-click it. The first time, macOS will say it can't open the app. Click **Done**, then go to **System Settings → Privacy & Security**, scroll down and click **Open Anyway**. You only need to do this once ✨

> 🙏 **Why does macOS block it?** This is an early version, and it isn't signed with a paid Apple Developer account yet, so macOS doesn't recognize it. It's not a virus, promise! The app never connects to the internet, and all the code is right here for you to read. Thanks for your patience while I work on it 💙

## Build it yourself 🛠️

```sh
git clone https://github.com/yike-shi/WINTER_IS_COMING.git
cd WINTER_IS_COMING
zsh build.sh
```

Use the ❄︎ in the menu bar to pause, pick light, medium or heavy snow, switch the fairy lights on, clear the snow, or quit.

- No `swiftc`? Run `xcode-select --install` first.
- Want snow on the Dock too? Turn on **WinterIsComing** (or **Terminal**, if you run it with `build.sh`) in System Settings → Privacy & Security → Accessibility.
- Want your own `.app`? Run `zsh make_app.sh 1.0`.

## Windows version 🪟 (beta)

You asked for it, so here it is! Snow falls on your Windows desktop and piles up on your windows and the taskbar, and dragging a window still sends it flying. Snowballs and snowmen are coming soon ⛄

```sh
cd WINTER_IS_COMING/windows
dotnet run -c Release
```

Use the ❄ icon in the system tray to pause, pick the snow amount, clear the snow, or quit. You'll need the [.NET 8 SDK](https://dotnet.microsoft.com/download) (`winget install Microsoft.DotNet.SDK.8`).

## Linux version 🐧 (beta)

Linux (beta, X11 preferred). Snow falls across your desktop, piles up on top of windows and along the bottom of the screen (or on your taskbar/dock), and dragging a window still sends the snow flying — the faster you drag, the farther it goes. That full effect needs X11: window-top piles and drag-fling are X11-only and are **not** available on Wayland, where the overlay can't see other windows.

There's no system tray here. A small on-screen control window lets you pause or resume the snow, pick light, medium or heavy, clear the snow, and quit. The same actions have keyboard shortcuts: `p` to pause, `1`/`2`/`3` for the snow amount, `c` to clear and `q` to quit. Snowballs, snowmen and fairy lights aren't included, and this build doesn't aim to match the macOS or Windows versions feature for feature.

Install the Python bindings for GTK 3 and Cairo, plus python-xlib for the window effects:

Ubuntu/Debian:

```sh
sudo apt install python3 python3-gi python3-gi-cairo gir1.2-gtk-3.0 python3-xlib
```

Fedora:

```sh
sudo dnf install python3 python3-gobject python3-cairo gtk3 python3-xlib
```

Arch:

```sh
sudo pacman -S python gtk3 python-gobject python-cairo python-xlib
```

Then run it, or check your setup first:

```sh
python3 linux/winter_snow/main.py --snow medium
python3 linux/winter_snow/main.py --diagnose
```

`--diagnose` just prints what it finds — it never opens a window, so it's handy when something looks off. On Wayland, installing `gtk-layer-shell` gives you a full-screen overlay layer.

## Make it yours 🎨

Every setting lives at the top of `WinterIsComing.swift` (`enum Cfg`): how many flakes fall, how fast they fall, how tall the piles get, how big a snowball can grow… Change a number and run `zsh build.sh` again.

## License

[MIT](LICENSE) · Made with 💙 and a little help from Claude
