# World Tuner for Pathologic 2

> **Status: work in progress, not released yet.** There is no release package. Features are still being added and
> tested in the game; settings, names and the config file may change, and some parts may not work as described.
> If you want to try it anyway, build it from source (see below) and keep a backup of your saves.

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin with an in-game window (**F6**) to tune the town's look while
you play:

- **Free camera** (**F9**), which the game does not have: fly anywhere with the world stopped or running, houses can
  load around the camera, hide the interface and the player's hands, field of view, zoom, roll, depth of field with auto
  focus. NPCs keep their poses, dithered hair stays smooth in a still frame.
- **Weather**: switch to any of the game's weathers (Clear, CloudyPartial, RainSun, Cloudy, RainLight, RainHeavy, Menu),
  lock the weather against the game's scripts, keep the streets wet (or dry).
- **Sky**: clouds, day and night light, sun, moon and stars, colours of light, sky, fog and clouds, rain and wind; the
  town's latitude, longitude, time zone and the sky's date (white nights, polar night, noon at 12:00).
- **Life & lights**: where street crowds appear and disappear and how many, how far lamps cast shadows and keep their
  light, the player's lantern (range, brightness, colour, shadows), birds per flock, falling leaves.
- **Fog**: density, start distance, height fog level and sky haze, either one setting for every weather or a separate
  setting for each weather. Volumetric light (light shafts) can be kept as in the game.
- **Draw distance**: how far objects, trees and people are drawn, how far real houses and districts are loaded,
  LOD bias, shadow distance, grass, terrain detail.
- **Time of day**: set the hour or stop the clock.
- **Physics rate**: Unity's fixed timestep (30 Hz in the game), 5–1000 Hz.
- **Smooth run stop**: the game drops from running to walking speed within one physics step (Shift released, stamina
  run out), which jerks the view; the mod can slow down over a set time instead (off by default).
- **Landing camera push**: the game kicks the field of view on every landing after a jump, which reads as a jerk of the
  view. The mod removes it by default; the slider brings it back in part or fully (the landing sound stays).
- **Image**: exposure, contrast, saturation, white balance, strength of the player-state screen effects,
  bloom, vignette, chromatic aberration, film grain, auto exposure.
- **Memory & loading**: frame-time and hitch monitor, memory numbers, the game's scheduled memory cleanup, garbage
  collector mode, background loading priority, GPU upload, texture streaming budget.

**Presets** on the Fog, Distance, Sky, Life & Lights, Memory & Loading tabs and for the image (post-processing): a
drop-down with built-in presets and your own, saved as shareable text files in
`BepInEx\config\pathologic2-world-tuner-<tab>-presets\`. Any preset except *Game* can be deleted (built-in ones are
hidden and come back with *Restore built-in*).

Everything is changed in memory at runtime. The mod does not modify game files, and saves stay clean.

## Requirements

- **Pathologic 2** for Windows. Written for game versions 2.1.31563 and 2.1.31648 (Unity 2022.3).
- **BepInEx 5, 64-bit (x64)**, tested with 5.4.23. BepInEx 6 is not supported.

## Installation

There is no release yet, so the mod is built from source:

1. Install BepInEx 5 x64 into the game folder (next to `Pathologic.exe`) and start the game once.
2. Build and install it (see *Building from source*). The plugin ends up in
   `BepInEx\plugins\pathologic2-world-tuner\Pathologic2WorldTuner.dll`.
3. Start the game. `BepInEx\LogOutput.log` should contain a line like
   `Pathologic 2 World Tuner 1.0.0: MenuKey=F6, Fog.Mode=Off`.

To uninstall, delete the `BepInEx\plugins\pathologic2-world-tuner` folder.

## Usage

Press **F6** to open or close the window (Esc also closes it). While it is open the game gets no mouse or keyboard input.
Every slider has a **Game** button that returns the game's value. Settings are saved to
`BepInEx\config\pathologic2.worldtuner.cfg` and applied on every start; weather and time of day are one-off actions.

### Weather

Works like the game's console command `change_weather`: the picked weather is blended in on the base weather layer
over the chosen time. The game's scripts keep changing the weather on their own schedule, so the next scripted change
replaces it. The window also shows the four weather layers (base, planned events, district, cutscene) and their weights:
when an upper layer is active, it covers the base layer.

Below the layers:

- **Weather lock** (not saved): *Base layer* stops the game's scripts from changing the everyday weather; *All layers*
  also blocks storms and district or cutscene weather and their weights. The weather you pick in the window still works,
  and loading a save still restores its weather.
- **Wet streets**: *Fixed* holds the ground wetness and the puddles at the chosen levels, with or without rain.

### Sky

Multipliers on top of every weather (the game keeps blending the weathers as usual): cloud coverage, opacity,
brightness, size, sharpness, light through clouds, colour and glow; for day and for night separately the sun- or
moonlight, ambient light, shadow strength, sky reflections, sky brightness and contrast (day also Rayleigh, Mie and the
glow around the sun); sun, moon and star size and brightness; warmth and saturation of the light, sky, ambient light, fog
and clouds; rain strength and wind (cloud drift). Moonlight x6–x10 gives clearly lighter nights.

**Where and when**: the game puts the town at 52° N, 85° E with clocks at UTC+8 and starts the sky on 15 August 1915.
The tab changes the latitude, longitude, time zone and shifts the sky's date, and shows today's dawn and dusk.
The tab's presets include *Noon at 12:00* (time zone = longitude / 15), *White nights*, *Polar day*, *Polar night* and
*Equator*. Only the sky moves: street lamps, crowds and quests follow the clock, not the sun.

### Life & lights

- **Street crowds**: passers-by appear in a ring 50–75 m around the player and are removed beyond 100 m, at most 500 at
  once. With a longer draw distance they pop up in plain view; moving the ring farther hides that, but points inside the
  inner radius stay empty until you walk away. More distance means more NPCs at once (CPU). Works in a running game only.
- **Street lamps**: lamps drop their shadows beyond 15-25 m and dim with the distance to the camera; both distances can be
  multiplied. Point-light shadows are expensive: x2 and above may cost a lot of GPU time in lit streets.
- **Lantern** (two point lights): range, brightness, colour warmth, shadows (Game / Off / Hard / Soft).
- **Birds**: number of birds per flock (birds are added or removed one at a time). Each bird costs some CPU.
- **Falling leaves**: rate and radius around the player.

### Fog

Every weather has its own fog (density, start distance, height) and sky haze. Modes:

- **Off**: as in the game.
- **Global**: one set of multipliers and offsets applied to every weather.
- **Per weather**: a separate set for each weather. Buttons copy a setting to all weathers or take it from Global.

| Setting | Meaning |
|---|---|
| Density | Multiplier of the fog density. 0 = no distance fog. |
| Start distance | Meters added to where the fog starts: no fog closer than this, the far view stays hazy. |
| Height fog level | Level up to which the height fog lies (60 m in the game). |
| Sky haze | Multiplier of the weather's "fogginess" (hazy horizon). |
| Keep volumetric light | The game scales light shafts with the fog density. On: they stay as in the game whatever the density. |
| Fog shader | Multipliers of the camera's fog shader (distance and height parts) and switches to turn either part off. |

The mod edits the weather templates in memory, so the change shows at once and also applies to weathers the game
switches to later. Saves store only which weather is active, not the fog values.

### Draw distance

What limits the view in Pathologic 2 is not the LOD setting:

- most objects (trees, fences, house facades, people) are cut at **150 m** by per-layer camera distances;
- real houses are separate scenes, loaded at **30–95 m** (special buildings up to 200 m); farther away you see
  low-poly district proxies;
- grass is drawn to 30–50 m, the sharp ground texture to 20 m.

| Group | Sliders |
|---|---|
| Rendering | Objects/trees/people, infection decals, interiors, key buildings and proxies, terrain, camera far clip |
| World streaming | Houses, landmark buildings, districts (multipliers of the load/unload distances), load distance while indoors |
| Detail | LOD bias, shadow distance, grass, sharp ground texture distance, terrain mesh error, reflection distance |

Presets **Game / Far / Very far** set several sliders at once. Streaming sliders apply when released: larger values
load more houses at once, which costs CPU time and memory and causes hitches while walking. Start with Houses x2–3.

### Time of day

Sets the solar time: sky, sun, street lamps and the day/night street crowds. Quests run on the separate game time,
which is not changed. **Stop the clock** freezes the solar time. While a save is written, the mod puts back the game's
solar time and clock speed, so saves are not affected. Sleeping or a script may set the time again on their own.

### Physics rate

The game steps physics 30 times per second (`Time.fixedDeltaTime` = 0.0333 s). The player's movement, blocking, the combat
camera kicks, the animators of walking NPCs and ragdolls all advance in these steps. The slider covers 10–240 Hz, buttons
and the **Exact** field accept 5–1000 Hz. Game code works in seconds, so speeds and timings stay the same; the CPU cost
per second grows with the rate, and game speed multiplies the number of steps per frame. While the Time tab is open, the
window shows the measured steps per frame and the time the physics part of the frame takes. With **Game** selected the
mod does not touch the value at all. Ragdolls and combat were tuned for 30 Hz.

### Free camera

Press **F9** (or the button on the Free Camera & Image tab) during normal play. WASD moves, Q/E go up and down, the mouse looks,
Shift and Ctrl change the speed, the wheel sets the speed, +/- zoom, Z/C roll, R levels the camera, H shows or hides the
interface, Esc leaves. Close the mod's window to fly. By default the world stops (game speed 0), the interface and the player's
hands are hidden; all three are switches. The camera uses the game's own slot for the developers' fly camera, so the
player stands still and cannot interact. A cutscene, a dialog or loading a save switches it off. Houses load around the
player; with *Load houses around the camera* they load where the camera is (flying fast means hitches and more memory). NPCs who look at the player keep doing so (the game would drop that pose).
Stopping the world also stops shader time; *Keep dithering moving while stopped* (on by default) shifts it by a few
milliseconds every frame, so dithered hair keeps being smoothed by TAA/DLSS. Use Steam's F12 for screenshots.

Depth of field: Off, only in the free camera, or always; focus distance (or auto focus on the centre of the screen),
aperture, focal length, blur size.

### Image

Applied after the game's own effects, every frame, to the game camera's post-processing (Post Processing Stack v1).
**Screen effects** scales how much the player's state (tiredness, infection, pain) shifts colour and chromatic
aberration: 0 keeps the picture neutral. Effects that are off in the game (for example grain) cannot be scaled. With
every slider at Game the mod does not touch post-processing at all. Presets: *Vivid*, *Bleak*, *Plague*, *Old photo*,
*Noir*. TAA sharpness is in the DLSS mod.

### Memory & loading

- **Hitch monitor**: frame times, and every frame longer than a threshold with what happened in it (garbage
  collection, the game's memory cleanup, scenes loaded or unloaded). Runs while the tab is open, or always if switched
  on; can write hitches to `LogOutput.log` with the local time (`Hitch at 21:14:03.512: 90 ms (game memory cleanup)`),
  to match them with a frame-time graph or the game's `Player.log`.
- **The game's memory cleanup**: the game keeps the garbage collector off. When a game starts, when you enter a house
  or change location, and every 1000 s of game time it pauses the game with a small loading icon, unloads unused
  assets and collects all garbage. The timer can be changed or switched off; "Clean up now" runs it at once.
- **Garbage collector**: Game (off between cleanups) or Incremental (Unity's collector always on in small slices,
  an experiment), and its time slice.
- **Loading**: what limits how fast houses appear is how often each house checks its distance to the player (once a
  second in the game; can be set down to 0.05 s, the checks are cheap) and that scenes load strictly one at a time
  (parallel loading is an experiment: the developers turned it off). Background loading priority (the game uses Low)
  and GPU upload time and buffer are there too, but hardly matter here: a house scene loads in 0.02-0.03 s.
- **Texture streaming**: the budget for streamed textures and how far textures may be lowered to fit it, with the
  memory the textures want now.

### Config

`BepInEx\config\pathologic2.worldtuner.cfg`: sections `General` (window key, input blocking), `Weather`, `Fog`
(`Mode`, `KeepVolumetricLight`), `Fog.Global`, `Fog.<weather>`, `Fog.Advanced`, `Distance.Rendering`,
`Distance.Streaming`, `Distance.Detail`, `Player`, `Physics` (`Rate`), `Photo`, `Image`, `Memory`, `Loading`, `Monitor`,
`Sky.Clouds`, `Sky.Day`, `Sky.Night`, `Sky.Bodies`, `Sky.Colors`, `Sky.Elements`, `Sky.Place`, `Sky.Wetness`, `Life`, `Lantern`. In the file, `-1` means "as in the game" for absolute values and `1` for
multipliers. Edit the file with the game closed (or use the window).

## How it works

- Weather: `WeatherController` blends four layers of `WeatherSnapshot` templates; the mod calls
  `BlendTo` on the base layer like `change_weather` does.
- Sky: a postfix on `WeatherSnapshotUtility.CopyFrom` (the game copies the blended weather into `TOD_Sky`, `RainManager`,
  `LeafManager` and the wind only when the weather changes) scales the copied values; installed only while a value is not
  at Game. A change re-runs the game's copy, which restores the game's values first. The date is
  `GameSettingsData.OffsetTime` (used only by the sky). Weather lock: prefixes on the script component's
  `SetWeatherSample*` / `SetWeatherLayerWeight*`. Wetness: `RainManager.terrainWetness/puddleWetness` every frame (Fixed only).
  The game's smooth updaters (weather, environment, house loaders) accumulate scaled time and stop at game speed 0; while the
  world is stopped the mod runs the weather and environment updates itself twice a second, so sky and time changes still show.
- Life: `ExternalOptimizationSettings.CrowdSpawnMin/MaxDistance`, `CrowdDestroyDistance`, `MaxOutdoorCrowdEntityCount`;
  `LightCameraShadowDistance.shadowDistance` and the `rangeCurve` of `LightCameraDistance` (stretched along the distance
  axis; lamps enabled later get it in an `OnEnable` prefix); the `Light` components under `PivotPlayer.FlashlightLight`
  (and `LightFlicker.baseIntensity`); `FlockController._childAmount`.
- Fog: the `Fog` and `Day/Night.Fogginess` fields of the snapshot templates are rewritten from the stored originals; finished
  blends hold copies, which are updated too, then the weather is re-applied (`FogController.CopyFrom`). A prefix on
  `VolumetricLightDensity.ApplyDensity` feeds the game's density to volumetric light when it is kept.
- Draw distance: `Camera.layerCullDistances` and `farClipPlane` of the game camera; fields `loadDistance`,
  `unloadDistance`, `loadIndoorDistance` of the `LoadByDistance*` components (rescanned after scene loads);
  `QualitySettings.lodBias/shadowDistance` (re-applied after the game's settings are applied); `_DrawDistance` of the grass
  materials; `Terrain.basemapDistance/heightmapPixelError`; `PlanarReflectionCapture.RelativeWorldCullingDistance`.
- Time: `TimeService.SolarTime` and `SolarTimeFactor`; `TimeService.Save` is wrapped to write the game's values.
- Physics rate: `Time.fixedDeltaTime`, written once when the setting changes (the game never sets it).
- Free camera: `CameraService.Kind = Fly`; a prefix replaces `FlyCameraController.Update` while it is on (installed on
  entering, removed on leaving). The game does not control the player in Fly mode by itself; the mod
  leaves the game's input alone except Esc, which leaves the free camera (the pause menu would put Fly back on closing).
  Held keys are read from Windows (`GetAsyncKeyState`). The game drops NPC look-at IK whenever the camera is not the
  first-person one (NPCs looking at the player snap out of their pose); a postfix on `IKController.OnPreLookAtIKUpdate`
  keeps it while flying.
- Image: a postfix on `PostProcessingOverrideController.LateUpdate` puts the mod's own model instances with adjusted
  settings into the rebuilt `_Override` profile; installed only while a setting differs from the game's.
- Memory: `ExternalOptimizationSettings.MemoryStrategyTimeEventPeriod` and the timer of `MemoryStrategyService`;
  `GarbageCollector.GCMode` and `incrementalTimeSliceNanoseconds`; `Application.backgroundLoadingPriority`;
  `QualitySettings.asyncUpload*` and `streamingMipmaps*` (re-applied after the game applies its graphics settings).

## Building from source

Requirements: the .NET SDK (any recent version; the project targets .NET Framework 4.7.2 through reference
assemblies) and the game with BepInEx 5 installed. The game folder provides the referenced assemblies.

```powershell
# builds dist\Pathologic2WorldTuner.dll and the archive dist\Pathologic2WorldTuner-<version>.zip
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 [-GameDir "D:\Games\Pathologic"]

# copies the DLL into <game>\BepInEx\plugins\pathologic2-world-tuner\ (-Uninstall removes it)
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 [-GameDir "D:\Games\Pathologic"] [-Uninstall]
```

The default game path is `C:\Program Files (x86)\Steam\steamapps\common\Pathologic`.

## About this mod

This mod is vibe-coded. The investigation (decompiling the game code, reading the game assets) and the code were done
by an AI assistant ([Claude Code](https://claude.com/claude-code)) under the direction of
[edwvee](https://github.com/edwvee). Bug reports and pull requests are welcome.

## License

[MIT](LICENSE). Pathologic 2 is © Ice-Pick Lodge. This project is not affiliated with Ice-Pick Lodge or tinyBuild.
