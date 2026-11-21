# Engine changes

Portaballs changes four things in s&box's C# source. The installer copies the changed files from `files/`
over a checkout of [sbox-public](https://github.com/Facepunch/sbox-public) at commit `a8a63b3a`, then
rebuilds the engine's C# with Facepunch's own build tool. The `.patch` files show each change on its own.
No native code is touched.

The game turns each change on with a project symbol in `game/portaballs/portaballs.sbproj`
(`DefineConstants`). Take a symbol out and the game compiles against an engine without that change, and
falls back to something simpler.

| Change | File(s) | Symbol | Without it |
|---|---|---|---|
| P1: `CommandList.DrawPortal` | `CommandList.Render.cs` | `PORTABALLS_P1` | portal view mode B falls back to A |
| P2: per-eye portal pictures (`PortalViews`) | new `PortalViews.cs`, one call in `StandardPipeline.cs` and `SceneCamera.cs` | `PORTABALLS_P2` | both eyes get the same picture through a portal, which looks cross-eyed in a headset |
| P3: VR eyes placed either side of the head | `SceneCamera.cs`, `VRSystem.Rendering.cs` | always on, console variable `vr_eyes_centred` | the engine draws the world about 3.5 cm to one side of your head |
| P4: portal pictures kept from frame to frame | `PortalViews.cs` | `PORTABALLS_P4` | a portal seen through a portal is dark, no half-rate option |

## P1: DrawPortal

One new method at the end of the render command list (178 lines added, none removed). It draws the far side
of a portal for whichever view is being drawn, from that view's own eye, with the projection's near plane
moved onto the exit wall. Used for comparison now; P2 replaced it in a headset, because the engine runs that
command list only in the left eye's pass.

## P2: PortalViews

A new static class, `Sandbox.Rendering.PortalViews`. While each top-level view is set up (in a headset, each
eye), it sets aside a picture per open portal end and publishes it in the view's attributes, the same way the
engine's planar reflections do. In the view's after-depth-prepass stage it carries that view's eye through the
portal, cuts the projection at the exit wall and draws the far side into the picture. So each eye gets its own
picture through the portal.

## P3: eye placement

`SceneCamera.RenderStereo` overwrote the head position with the left eye's before placing the right eye from
it, so the eyes ended up a whole eye distance apart but both on one side of the head (measured: one eye 0 mm
and the other 70.5 mm from the head). Now each eye is half the eye distance from the head.
`vr_eyes_centred 0` switches back.

## P4: kept pictures

Each view keeps its portal pictures from the frame before, with the camera they were taken with. That gives:

- a portal seen through a portal, drawn from last frame's picture of it (the endless tunnel when two portals
  face each other)
- an optional half rate: draw a portal picture every other frame and re-project the kept one in between
- drawing a picture only over the part of the view its opening covers

## Doing it by hand

```powershell
git clone https://github.com/Facepunch/sbox-public.git C:\Portaballs\sbox
git -C C:\Portaballs\sbox checkout a8a63b3ad2ad5dcc0712eac863810b70d94a26a0
cd C:\Portaballs\sbox
.\Setup.bat
# copy engine-patches\files\* over the same paths in C:\Portaballs\sbox, then:
dotnet run --project engine\Tools\SboxBuild\SboxBuild.csproj -- build --skip-native
```

To take the changes out again: `git checkout --force a8a63b3a` in the engine folder, delete
`engine\Sandbox.Engine\Systems\Render\PortalViews.cs`, remove the three symbols from the `.sbproj`, and run the
same build command.

See [LICENSE-sbox.md](LICENSE-sbox.md) for the license of these files.
