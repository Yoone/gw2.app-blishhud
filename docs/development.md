# Development

## Requirements

- **Syncthing** to share the project between Mac and Windows
    - Windows: [app install](https://github.com/Bill-Stewart/SyncthingWindowsSetup/)
    - Mac: Installed with `brew install syncthing` and `brew services start syncthing`
    - On either device access the control panel [here](http://localhost:8384/) after starting

## Setup

_These steps were copied and adapted from the official [BlishHUD example module](https://github.com/blish-hud/Example-Blish-HUD-Module)._

1. Install [Visual Studio 2022](https://visualstudio.microsoft.com/en/downloads/)
1. Download the [newest Blish HUD version](https://blishhud.com/) extract the Blish ZIP
1. Change the executablePath in `\module\Properties\launchSettings.json` to where the `Blish HUD.exe` from the extracted Blish ZIP is
1. Open `GW2app.sln` in Visual Studio.
1. Right-click on the Solution icon in the Solution Explorer and select **Restore Nuget packages** (may not be necessary when using Visual Studio 2022)
1. Start Guild Wars 2
1. In the visual studio menu bar click on the dropdown next to the green arrow and select "gw2"
1. Press the green arrow to start Blish HUD with the example module in debug mode: it will overlay Guild Wars 2

You can overlay a powershell or other window instead of GW2, too. But when you don't overlay GW2, API keys will not work in Blish because GW2 Mumble Link cannot be accessed by Blish. Overlaying something else than GW2 can be still usefull for debugging modules.

Additional info for debugging ("Configuring Your Project" can be ignored) is available in the [Blish docs](https://blishhud.com/docs/modules/overview/debugging).

## Notes

- `module/ref` contains assets that can be loaded using the ContentsManager
