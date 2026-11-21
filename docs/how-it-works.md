# How it works

The numbers behind Portaballs, for anyone who wants to change them. Everything here is measured or set in
this repository; where a value lives in code, the file is named. Units are metres and seconds, +X forward,
+Y left, +Z up (`core/src/Common/Units.cs`).

## The room

8 x 6 x 3.5 m. Six paint surfaces: four walls, the floor and the ceiling (`TestRoom.cs`). Each has a frame
whose normal points into the room, so "down" along a surface is worked out from that frame. Paint runs down a
wall and doesn't run on a floor or a ceiling.

## Throwing

- The tracking helper reads both controllers, the head and the eyes from SteamVR about 500 times a second
  and streams them to the game over a WebSocket on `127.0.0.1:8443`. Without it the game uses the engine's
  once-a-frame poses.
- A throw is the hand's velocity at the moment the grip lets go, times 1.7 (`ThrowHand.SpeedScale`).
- Aim help pulls the throw toward a point you were looking at. Best first:
  - a steady look: eyes within 3 degrees for 70 ms, ending in the last 300 ms before the release. Takes 65%
    of the error away.
  - a glance: eyes within 4.5 degrees for 40 ms inside the last 120 ms. 45%.
  - the head's direction. 45%.
  The pull fades out for throws more than 15 to 30 degrees (eyes) or 10 to 25 degrees (head) off the point,
  so a deliberate throw elsewhere goes where you threw it. Code: `core/src/Throwing/AimAssist.cs`,
  `GazeFixationFilter.cs`.
- Each throw writes `THROW`, `GAZE`, `AIM` and `TIMING` lines to the engine's log
  (`C:\Portaballs\sbox\game\logs\sbox.log`), on your PC only.

## Paint

All the feel numbers are in `core/src/Paint/SplatFeel.cs`.

- Each surface has its own paint texture, stepped every frame.
- A splat throws 3 to 6 droplets that fly freely and dab where they land. They never go through a portal
  and never land on a portal patch.
- Drips start fast, keep running for about 6 seconds and creep to a stop at 7. Drips run faster over paint
  that's still wet, and two that join run faster again.
- Paint that reaches the foot of a wall pours onto the floor and spreads into a puddle until it runs out.
  A splat on the ceiling lets a few drops fall.
- Run-off, puddles and drops are loose paint: they never make or move a portal.

## Portals

- One patch per colour. A new splat that touches its colour's patch joins it, one that lands elsewhere
  replaces it.
- The crossing is a rigid mapping from one patch to the other, centred on the paint, like Portal's. Balls
  keep their speed and come out in the mapped direction.
- The opening is where both patches have paint at the matching point, so two patches at different heights
  each keep a band of plain paint around the opening. The picture shows 4 cm of paint as a rim.
- The player is an upright line from the head down to the floor, 0.2 m in radius. A linked floor patch with
  room for that is a hole: you sink in, and when your head passes the floor you come out of the other end.
- Coming up out of a floor you're thrown at least fast enough for your feet to rise 0.4 m clear (about
  6.3 m/s for a 1.6 m tall head). Falls are capped at 6 m/s. Landing with sideways speed slides to a stop at
  15 m/s².
- After a crossing that tips you over, the play space turns back upright about your head over 0.25 s.
- All of it is `core/src/Portals/PlaySpaceWalker.cs` and its neighbours. The flat-screen camera runs the same
  code as the headset, so the scripted tests cover both.

## Seeing through

- Each eye gets its own picture of the far side, drawn while that eye's view is drawn (engine change P2).
  The far side's projection is cut at the exit wall with an oblique near plane.
- When your eye gets closer to the opening than the engine's near plane, a "guard sheet" just in front of
  the eye takes over drawing the far side, and writes its own depth, so you don't see a flash of wall.
- A portal seen through a portal uses that eye's picture from the previous frame (engine change P4). Facing
  portals give an endless tunnel at the cost of one texture read per pixel.
- A picture is only drawn over the part of the view its opening covers, and not at all for a view that
  can't see the opening.
- The paint is self-lit ("neon") and skips the room's lighting. On my PC it was 0.09 to 0.37 ms cheaper than
  ordinary lit paint wherever paint was on screen, and the two patch lights added 0.015 to 0.16 ms.

## Tests

`core/tests` has the maths tests (paint, portals, throwing, the helper's message format). They run without
the engine or a headset, on every push. `game/portaballs/Code/Game/Debug/PortalProof.cs` is a set of 101
scripted checks that run in the engine on a flat screen: balls and the camera going through every kind of
portal pair, including floor to floor and a ceiling over a floor.
