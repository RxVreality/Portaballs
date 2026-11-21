using System;

namespace Portaballs.Core
{
    /// <summary>Answer from <see cref="PlayerPassage"/>: what one frame's head movement came to.</summary>
    public struct PlayerStep
    {
        /// <summary>Where the head ends up, world metres: held back by walls, or carried through the portal.</summary>
        public Vec3 Head;

        /// <summary>True when the head went through a linked patch this frame.</summary>
        public bool Crossed;

        /// <summary>When crossed: true = went in through A (orange).</summary>
        public bool FromA;

        /// <summary>
        /// When crossed: apply this to the whole player (the desk camera's pose, or the VR play-space anchor)
        /// and to the player's velocity. Between two walls it only turns about world up. With a floor or a
        /// ceiling at either end it is the full rigid crossing and can tip the player over: the walker turns
        /// the play space upright again afterwards (see <see cref="PortalPair.PlayerAToB"/>).
        /// </summary>
        public RigidTransform Crossing;

        /// <summary>When crossed: how far the exit was slid along the exit wall to stay inside the exit patch. 0 = plain rigid crossing.</summary>
        public float ExitMovedBy;

        /// <summary>When crossed: where on the entry wall the head went in, wall metres, and the same point in the world.</summary>
        public Vec2 EntryUV;
        public Vec3 EntryPoint;

        /// <summary>True when a wall held the head back this frame.</summary>
        public bool Blocked;
    }

    /// <summary>
    /// The player and the walls: solid everywhere except inside a linked patch, where the head goes through.
    ///
    /// Only the head counts (in VR the rest of the body is wherever the real person is). The head is treated
    /// as a ball of <c>headRadius</c>: it goes through when its centre crosses the wall plane on linked paint
    /// and a circle of that radius around the crossing point is all paint. Anywhere else the wall keeps the
    /// head <c>clearance</c> metres away.
    /// </summary>
    public static class PlayerPassage
    {
        /// <summary>
        /// One frame of head movement from <paramref name="previous"/> toward <paramref name="wanted"/>:
        /// first the portal (did it go through?), and if not, the walls.
        /// </summary>
        /// <param name="hasPair">False when no portal is linked: every wall is solid everywhere.</param>
        /// <param name="maskA">Paint on the wall of pair.A.</param>
        /// <param name="maskB">Paint on the wall of pair.B.</param>
        /// <param name="walls">The room's walls.</param>
        /// <param name="headRadius">Radius of the head for the "does it fit" test, metres.</param>
        /// <param name="clearance">How far solid wall keeps the head's centre away, metres.</param>
        /// <param name="maxPushOut">
        /// When the head is already nearer a solid wall than <paramref name="clearance"/> (it was in a portal
        /// mouth and stepped sideways), it is moved out at most this far per call, so it slides out instead
        /// of popping. Use speed times frame time.
        /// </param>
        public static PlayerStep Step(bool hasPair, in PortalPair pair, IPaintMask maskA, IPaintMask maskB,
            WallFrame[] walls, int wallCount, Vec3 previous, Vec3 wanted, float headRadius, float clearance,
            float maxPushOut, PortalSettings settings)
        {
            if (hasPair && TryCross(pair, maskA, maskB, previous, wanted, headRadius, settings, out PlayerStep crossed))
            {
                return crossed;
            }

            var result = new PlayerStep { Crossing = RigidTransform.Identity };
            result.Head = ResolveWalls(hasPair, pair, maskA, maskB, walls, wallCount, previous, wanted, headRadius,
                clearance, maxPushOut, settings, out result.Blocked);
            return result;
        }

        /// <summary>
        /// Did the head go through a linked patch between <paramref name="previous"/> and <paramref name="current"/>?
        /// At most one crossing is reported per call.
        /// </summary>
        /// <param name="floorExitRadius">
        /// Coming up out of a patch in the FLOOR, the exit is placed with room for a circle this big (the
        /// body) instead of just the head, so the player comes out where the hole is wide enough to rise
        /// through. Leave at 0 for the head alone. Only the exit placement uses it; going in is the head's test.
        /// </param>
        public static bool TryCross(in PortalPair pair, IPaintMask maskA, IPaintMask maskB, Vec3 previous,
            Vec3 current, float headRadius, PortalSettings settings, out PlayerStep step, float floorExitRadius = 0f)
        {
            step = new PlayerStep { Head = current, Crossing = RigidTransform.Identity };
            for (int k = 0; k < 2; k++)
            {
                bool fromA = k == 0;
                PortalEnd end = fromA ? pair.A : pair.B;
                IPaintMask mask = fromA ? maskA : maskB;
                CrossingResult hit = PortalCrossing.Detect(end, mask, previous, current, settings);
                if (!hit.Teleports) continue;
                if (!PortalCrossing.Passage(end, mask, hit.UV, headRadius, settings).Fits) continue;

                PortalEnd exit = fromA ? pair.B : pair.A;
                float exitRadius = exit.Wall.IsFloor && floorExitRadius > headRadius ? floorExitRadius : headRadius;
                PortalCarry carry = BallTransit.Carry(pair, fromA, true, fromA ? maskB : maskA, hit.Point, exitRadius, settings);
                step.Crossed = true;
                step.FromA = fromA;
                step.Crossing = carry.Transform;
                step.ExitMovedBy = carry.MovedBy;
                step.EntryUV = hit.UV;
                step.EntryPoint = hit.Point;
                step.Head = carry.Transform.TransformPoint(current);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Keep the head out of the walls and the ceiling. Returns <paramref name="wanted"/> moved back into
        /// the room where a solid surface is in the way. In front of a linked patch the surface is open and
        /// does not push.
        ///
        /// FLOORS in the list are skipped: the head is never what meets a floor. The feet are, and that is
        /// <see cref="PlayerFall"/> and <see cref="FloorIsOpenUnder"/> (see <see cref="PlaySpaceWalker"/>).
        /// </summary>
        public static Vec3 ResolveWalls(bool hasPair, in PortalPair pair, IPaintMask maskA, IPaintMask maskB,
            WallFrame[] walls, int wallCount, Vec3 previous, Vec3 wanted, float headRadius, float clearance,
            float maxPushOut, PortalSettings settings, out bool blocked)
        {
            blocked = false;
            if (walls == null) return wanted;
            if (wallCount > walls.Length) wallCount = walls.Length;

            for (int i = 0; i < wallCount; i++)
            {
                WallFrame wall = walls[i];
                if (wall.IsFloor) continue;
                float d = wall.SignedDistance(wanted);
                if (d >= clearance) continue;

                Vec2 uv = wall.ToUV(wanted);
                // Only the stretch of plane the wall really covers (plus the clearance, so corners stay closed).
                if (uv.X < -clearance || uv.X > wall.Width + clearance || uv.Y < -clearance || uv.Y > wall.Height + clearance) continue;

                if (hasPair && IsOpenAt(pair, maskA, maskB, wall, uv, headRadius, settings)) continue;

                // How near the wall the head may be this frame. Normally the full clearance; if it was
                // already nearer (it stepped sideways out of a portal mouth) it is eased out, not popped.
                float need = clearance;
                float dPrev = wall.SignedDistance(previous);
                if (dPrev < clearance)
                {
                    float from = dPrev > 0f ? dPrev : 0f;
                    float eased = from + (maxPushOut > 0f ? maxPushOut : 0f);
                    if (eased < need) need = eased;
                }
                if (d >= need) continue;

                wanted += wall.Normal * (need - d);
                blocked = true;
            }
            return wanted;
        }

        /// <summary>True when this spot on this wall is inside a linked patch with room for the whole head.</summary>
        public static bool IsOpenAt(in PortalPair pair, IPaintMask maskA, IPaintMask maskB, in WallFrame wall,
            Vec2 uv, float headRadius, PortalSettings settings)
        {
            if (SameWall(pair.A.Wall, wall) && PortalCrossing.Passage(pair.A, maskA, uv, headRadius, settings).Fits) return true;
            if (SameWall(pair.B.Wall, wall) && PortalCrossing.Passage(pair.B, maskB, uv, headRadius, settings).Fits) return true;
            return false;
        }

        /// <summary>
        /// Is there NO floor under a body standing at this point? True when the point is over a linked patch
        /// that lies in the floor (at <paramref name="floorHeight"/>) and a circle of
        /// <paramref name="bodyRadius"/> around it is all that patch's paint: the hole is big enough for the
        /// body and the body is over it. Only the point's X and Y matter.
        /// </summary>
        public static bool FloorIsOpenUnder(bool hasPair, in PortalPair pair, IPaintMask maskA, IPaintMask maskB,
            Vec3 point, float floorHeight, float bodyRadius, PortalSettings settings)
        {
            if (!hasPair) return false;
            return FloorEndOpenUnder(pair.A, maskA, point, floorHeight, bodyRadius, settings)
                || FloorEndOpenUnder(pair.B, maskB, point, floorHeight, bodyRadius, settings);
        }

        private static bool FloorEndOpenUnder(in PortalEnd end, IPaintMask mask, Vec3 point, float floorHeight,
            float bodyRadius, PortalSettings settings)
        {
            if (!end.Wall.IsFloor) return false;
            if (MathF.Abs(end.Wall.Origin.Z - floorHeight) > 0.02f) return false;      // some other floor
            return PortalCrossing.Passage(end, mask, end.Wall.ToUV(point), bodyRadius, settings).Fits;
        }

        private static bool SameWall(in WallFrame a, in WallFrame b)
        {
            return a.Origin == b.Origin && a.U == b.U && a.V == b.V && a.Width == b.Width && a.Height == b.Height;
        }
    }
}
