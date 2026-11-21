using System;

namespace Portaballs.Core
{
    /// <summary>
    /// The four edges of a camera's view as tangents of their angles, in view space (+X right, +Y up):
    /// Left and Down are negative for an ordinary camera, Right and Up positive. A flat-screen camera is
    /// symmetric (Left = -Right); a VR eye is not, which is why the see-through code never assumes it.
    /// </summary>
    public struct LensTangents
    {
        public float Left;
        public float Right;
        public float Down;
        public float Up;

        public LensTangents(float left, float right, float down, float up)
        {
            Left = left;
            Right = right;
            Down = down;
            Up = up;
        }

        /// <summary>A symmetric camera from its HORIZONTAL field of view in degrees (the engine's way) and width / height.</summary>
        public static LensTangents Symmetric(float horizontalFovDegrees, float aspect)
        {
            float tanX = MathF.Tan(horizontalFovDegrees * (MathF.PI / 360f));
            float tanY = tanX / MathF.Max(1f / 1024f, aspect);
            return new LensTangents(-tanX, tanX, -tanY, tanY);
        }

        /// <summary>Width over height of the picture these edges make.</summary>
        public float Aspect
        {
            get { return (Right - Left) / MathF.Max(1e-9f, Up - Down); }
        }

        /// <summary>The largest sideways and the largest up/down tangent, for the head-clip guard.</summary>
        public float MaxHorizontal
        {
            get { return MathF.Max(MathF.Abs(Left), MathF.Abs(Right)); }
        }

        public float MaxVertical
        {
            get { return MathF.Max(MathF.Abs(Down), MathF.Abs(Up)); }
        }

        /// <summary>The four angles in degrees, in the order left, right, down, up (left and down negative).</summary>
        public void ToDegrees(out float left, out float right, out float down, out float up)
        {
            const float k = 180f / MathF.PI;
            left = MathF.Atan(Left) * k;
            right = MathF.Atan(Right) * k;
            down = MathF.Atan(Down) * k;
            up = MathF.Atan(Up) * k;
        }

        /// <summary>True when all four numbers are usable and the view has some width and height.</summary>
        public bool IsValid
        {
            get
            {
                return !float.IsNaN(Left + Right + Down + Up) && !float.IsInfinity(Left + Right + Down + Up)
                    && Right - Left > 1e-4f && Up - Down > 1e-4f;
            }
        }
    }

    /// <summary>What one eye needs to draw the far side of a portal.</summary>
    public struct PortalEyeView
    {
        /// <summary>Where the virtual camera stands, metres.</summary>
        public Vec3 Position;

        /// <summary>Which way it faces.</summary>
        public Quat Rotation;

        /// <summary>
        /// Its projection in the core's layout (row vectors, see <see cref="CameraConventions"/>) with the near
        /// plane moved onto the exit wall. Near, far and the plane are in the caller's length unit.
        /// Transpose it for an engine that uses column vectors.
        /// </summary>
        public Mat4 Projection;

        /// <summary>How far the virtual camera is behind the clip plane on the exit wall, metres (always positive).</summary>
        public float BehindExit;
    }

    /// <summary>How much of the screen something on a wall covers, in the view's own -1..1 coordinates.</summary>
    public struct ScreenCover
    {
        /// <summary>False when nothing of it is on screen (or the eye is behind the wall).</summary>
        public bool Visible;

        /// <summary>The part of it that is on screen: -1 is the left / bottom edge of the view, +1 the right / top.</summary>
        public float MinX, MinY, MaxX, MaxY;

        /// <summary>Width and height of that part as a share of the screen, 0..1.</summary>
        public float WidthFraction
        {
            get { return Visible ? (MaxX - MinX) * 0.5f : 0f; }
        }

        public float HeightFraction
        {
            get { return Visible ? (MaxY - MinY) * 0.5f : 0f; }
        }
    }

    /// <summary>
    /// Engine-free maths for drawing the view through a portal: one eye's virtual camera and clipped
    /// projection, the eye's own projection from its lens angles, whether a patch is on screen at all, and
    /// reading the lens angles back out of a view's side planes. Layout questions are all answered by
    /// <see cref="CameraConventions"/>; this file only uses it.
    /// </summary>
    public static class PortalViewMath
    {
        /// <summary>
        /// An eye's projection from its lens tangents, in the core's layout (row vectors, depth 0 near to
        /// 1 far). Its transpose is what the engine's own Matrix.CreateProjection makes.
        /// </summary>
        public static Mat4 Projection(LensTangents lens, float near, float far)
        {
            return Mat4.CreatePerspectiveOffCenter(lens.Left * near, lens.Right * near, lens.Down * near, lens.Up * near, near, far);
        }

        /// <summary>
        /// Everything one eye needs to draw what is behind a portal end.
        /// </summary>
        /// <param name="pair">The linked pair.</param>
        /// <param name="throughA">True when looking into A (the picture then shows B's room).</param>
        /// <param name="eyePosition">The real eye, metres.</param>
        /// <param name="eyeRotation">The real eye's facing.</param>
        /// <param name="lens">The real eye's lens angles.</param>
        /// <param name="near">Near distance in the caller's length unit.</param>
        /// <param name="far">Far distance in the caller's length unit.</param>
        /// <param name="unitsPerMetre">How many of the caller's length units make a metre (39.37 for inches).</param>
        /// <param name="clipOffset">How far in front of the exit wall the cut is made, metres.</param>
        /// <returns>
        /// False when the cut cannot be made: the carried eye is not behind the exit wall's cut plane. That
        /// only happens for an eye that is itself behind the entry wall. Draw nothing through the portal then.
        /// </returns>
        public static bool Build(in PortalPair pair, bool throughA, Vec3 eyePosition, Quat eyeRotation, LensTangents lens,
            float near, float far, float unitsPerMetre, float clipOffset, out PortalEyeView view)
        {
            view = default;
            RigidTransform crossing = pair.PlayerCrossing(throughA);
            return Build(crossing, throughA ? pair.B : pair.A, eyePosition, eyeRotation, lens, near, far, unitsPerMetre, clipOffset, out view);
        }

        /// <summary>
        /// The same with the crossing and the clip wall given separately (the "window test" uses the
        /// identity crossing and a plane of its own).
        /// </summary>
        public static bool Build(in RigidTransform crossing, in PortalEnd exit, Vec3 eyePosition, Quat eyeRotation, LensTangents lens,
            float near, float far, float unitsPerMetre, float clipOffset, out PortalEyeView view)
        {
            return Build(crossing, PortalCamera.ClipPlaneWorld(exit, clipOffset), eyePosition, eyeRotation, lens, near, far, unitsPerMetre, out view);
        }

        /// <summary>
        /// The general form: carry the eye through <paramref name="crossing"/> and keep only what is on the
        /// positive side of <paramref name="keepPlaneMetres"/> (a world plane in metres).
        /// </summary>
        public static bool Build(in RigidTransform crossing, Plane3 keepPlaneMetres, Vec3 eyePosition, Quat eyeRotation, LensTangents lens,
            float near, float far, float unitsPerMetre, out PortalEyeView view)
        {
            view = default;
            if (!lens.IsValid || !(near > 0f) || !(far > near)) return false;

            PortalCamera.VirtualEye(crossing, eyePosition, eyeRotation, out Vec3 position, out Quat rotation);
            Vec4 plane = CameraConventions.WorldPlaneToView(keepPlaneMetres, position, rotation);
            if (!(plane.W < 0f)) return false;   // the virtual eye is not behind the cut

            // The plane's distance term is a length: put it in the same unit as near and far.
            var planeUnits = new Vec4(plane.X, plane.Y, plane.Z, plane.W * unitsPerMetre);
            Mat4 projection = Projection(lens, near, far);
            if (!CameraConventions.ObliqueNearPlane(projection, planeUnits, out Mat4 oblique)) return false;

            view.Position = position;
            view.Rotation = rotation;
            view.Projection = oblique;
            view.BehindExit = -plane.W;
            return true;
        }

        /// <summary>
        /// The oblique near plane written out for a COLUMN-vector projection (the engine's layout: the
        /// transpose of the core's, perspective -1 in M43, depth in the third ROW). It is the closed form of
        /// <see cref="CameraConventions.ObliqueNearPlane"/> for a perspective matrix and gives the same
        /// numbers as transposing in, calling that, and transposing back. The engine patch (P1) carries a
        /// copy of exactly these lines, because the engine cannot call game code.
        /// </summary>
        /// <param name="m">Column-layout perspective projection: uses M11, M22, M13, M23, M33, M34 and replaces M31..M34.</param>
        /// <param name="viewPlane">The keep plane in view space, distance in the projection's length unit; W must be negative.</param>
        public static bool ObliqueNearPlaneColumnLayout(Mat4 m, Vec4 viewPlane, out Mat4 result)
        {
            result = m;
            if (!(viewPlane.W < 0f)) return false;
            var q = new Vec4(
                (Sign(viewPlane.X) + m.M13) / m.M11,
                (Sign(viewPlane.Y) + m.M23) / m.M22,
                -1f,
                (1f + m.M33) / m.M34);
            float d = Vec4.Dot(viewPlane, q);
            if (MathF.Abs(d) < 1e-12f) return false;
            float a = 1f / d;
            if (!(a > 0f)) return false;
            result.M31 = a * viewPlane.X;
            result.M32 = a * viewPlane.Y;
            result.M33 = a * viewPlane.Z;
            result.M34 = a * viewPlane.W;
            return true;
        }

        /// <summary>
        /// Reads a view's lens tangents back out of its four side planes, given as plane NORMALS in the
        /// camera's own axes (X forward, Y left, Z up). It does not matter whether the normals point into
        /// the view or out of it. Each side plane passes through the eye, so its normal alone fixes its angle.
        /// </summary>
        public static bool TangentsFromSidePlanes(Vec3 right, Vec3 left, Vec3 top, Vec3 bottom, out LensTangents lens)
        {
            lens = default;
            if (MathF.Abs(right.Y) < 1e-6f || MathF.Abs(left.Y) < 1e-6f || MathF.Abs(top.Z) < 1e-6f || MathF.Abs(bottom.Z) < 1e-6f) return false;
            lens.Right = right.X / right.Y;
            lens.Left = left.X / left.Y;
            lens.Up = -top.X / top.Z;
            lens.Down = -bottom.X / bottom.Z;
            if (lens.Left > lens.Right)
            {
                float t = lens.Left; lens.Left = lens.Right; lens.Right = t;
            }
            if (lens.Down > lens.Up)
            {
                float t = lens.Down; lens.Down = lens.Up; lens.Up = t;
            }
            return lens.IsValid;
        }

        /// <summary>
        /// How much of the screen a rectangle on a wall covers for an eye (the rectangle is a patch's bounding
        /// box, in wall metres). Used to skip the portal's extra view when its patch is off screen or tiny.
        /// The rectangle is cut at the camera's near distance first, so a patch the eye is almost touching
        /// is handled properly.
        /// </summary>
        /// <param name="outOfWall">How far in front of the wall the rectangle sits, metres.</param>
        public static ScreenCover CoverOf(in WallFrame wall, Vec2 boundsMin, Vec2 boundsMax, float outOfWall, Vec3 eyePosition,
            Quat eyeRotation, LensTangents lens, float nearMetres)
        {
            ScreenCover cover = default;
            if (!lens.IsValid) return cover;
            if (!(wall.SignedDistance(eyePosition) > outOfWall)) return cover;   // behind it, or in it

            // Corners in view space, in order round the rectangle.
            Vec3 a = CameraConventions.WorldToView(wall.ToWorld(new Vec2(boundsMin.X, boundsMin.Y), outOfWall), eyePosition, eyeRotation);
            Vec3 b = CameraConventions.WorldToView(wall.ToWorld(new Vec2(boundsMax.X, boundsMin.Y), outOfWall), eyePosition, eyeRotation);
            Vec3 c = CameraConventions.WorldToView(wall.ToWorld(new Vec2(boundsMax.X, boundsMax.Y), outOfWall), eyePosition, eyeRotation);
            Vec3 d = CameraConventions.WorldToView(wall.ToWorld(new Vec2(boundsMin.X, boundsMax.Y), outOfWall), eyePosition, eyeRotation);

            float near = MathF.Max(1e-5f, nearMetres);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            int points = 0;
            ClipEdge(a, b, near, lens, ref minX, ref minY, ref maxX, ref maxY, ref points);
            ClipEdge(b, c, near, lens, ref minX, ref minY, ref maxX, ref maxY, ref points);
            ClipEdge(c, d, near, lens, ref minX, ref minY, ref maxX, ref maxY, ref points);
            ClipEdge(d, a, near, lens, ref minX, ref minY, ref maxX, ref maxY, ref points);
            if (points == 0) return cover;

            minX = MathF.Max(minX, -1f);
            minY = MathF.Max(minY, -1f);
            maxX = MathF.Min(maxX, 1f);
            maxY = MathF.Min(maxY, 1f);
            if (!(maxX > minX) || !(maxY > minY)) return cover;

            cover.Visible = true;
            cover.MinX = minX;
            cover.MinY = minY;
            cover.MaxX = maxX;
            cover.MaxY = maxY;
            return cover;
        }

        // One edge of the rectangle: keep the part in front of the near distance and add its ends to the box.
        private static void ClipEdge(Vec3 p, Vec3 q, float near, LensTangents lens, ref float minX, ref float minY,
            ref float maxX, ref float maxY, ref int points)
        {
            // View space looks down -Z: "in front by at least near" is -z >= near.
            float dp = -p.Z - near;
            float dq = -q.Z - near;
            if (dp < 0f && dq < 0f) return;
            if (dp < 0f)
            {
                p = p + (q - p) * (dp / (dp - dq));
            }
            else if (dq < 0f)
            {
                q = q + (p - q) * (dq / (dq - dp));
            }
            Add(p, lens, ref minX, ref minY, ref maxX, ref maxY);
            Add(q, lens, ref minX, ref minY, ref maxX, ref maxY);
            points += 2;
        }

        private static void Add(Vec3 v, LensTangents lens, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            float depth = MathF.Max(1e-6f, -v.Z);
            float tx = v.X / depth;
            float ty = v.Y / depth;
            float x = (2f * tx - (lens.Right + lens.Left)) / (lens.Right - lens.Left);
            float y = (2f * ty - (lens.Up + lens.Down)) / (lens.Up - lens.Down);
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        /// <summary>
        /// Where each point of one wall comes out on another wall, as a flat map between the two walls' own
        /// 0..1 coordinates (0,0 = lower-left corner, 1,1 = upper-right): a point at (u, v) on the entry wall
        /// is carried by the crossing to  origin + u * alongU + v * alongV  on the exit wall. Exact, because
        /// both walls are flat and the crossing is rigid. The paint shader uses it to ask "is there paint
        /// where this point of the patch comes out?", so an opening is only shown where the player could
        /// really go through. Results outside 0..1 are off the exit wall.
        /// </summary>
        public static void FarWallMap(in WallFrame entry, in WallFrame exit, in RigidTransform crossing,
            out Vec2 origin, out Vec2 alongU, out Vec2 alongV)
        {
            origin = FarPlace(entry, exit, crossing, 0f, 0f);
            alongU = FarPlace(entry, exit, crossing, 1f, 0f) - origin;
            alongV = FarPlace(entry, exit, crossing, 0f, 1f) - origin;
        }

        /// <summary>One point of the entry wall (0..1 across and up) carried across, as 0..1 across and up the exit wall.</summary>
        public static Vec2 FarPlace(in WallFrame entry, in WallFrame exit, in RigidTransform crossing, float u, float v)
        {
            Vec3 world = entry.ToWorld(new Vec2(u * entry.Width, v * entry.Height));
            Vec2 far = exit.ToUV(crossing.TransformPoint(world));
            return new Vec2(far.X / exit.Width, far.Y / exit.Height);
        }

        /// <summary>
        /// How close (metres, measured from the wall) an eye may come to an open patch before part of its
        /// screen pokes through the paint panel that draws the opening. Inside this distance the game draws
        /// the opening on a sheet fixed to the camera instead (see the game's PortalView). It is the
        /// head-clip guard's distance plus the panel's own stand-off from the wall.
        /// </summary>
        public static float GuardDistance(float nearMetres, LensTangents lens, float margin, float panelOffset)
        {
            return HeadClipGuard.NearCornerDistance(nearMetres, lens.MaxHorizontal, lens.MaxVertical) + margin + panelOffset;
        }

        private static float Sign(float x)
        {
            return x > 0f ? 1f : (x < 0f ? -1f : 0f);
        }
    }

    /// <summary>
    /// A rectangle of a view in the view's own -1..1 coordinates (X to the right, Y UP): the part of the
    /// screen an opening covers, and so the only part of the far side's picture that has to be drawn.
    /// </summary>
    public struct ScreenRect
    {
        public float MinX, MinY, MaxX, MaxY;

        public ScreenRect(float minX, float minY, float maxX, float maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        /// <summary>The whole view.</summary>
        public static ScreenRect Full
        {
            get { return new ScreenRect(-1f, -1f, 1f, 1f); }
        }

        /// <summary>True when it has no width or no height.</summary>
        public bool IsEmpty
        {
            get { return !(MaxX > MinX) || !(MaxY > MinY); }
        }

        /// <summary>How much of the view it covers, 0..1.</summary>
        public float Share
        {
            get { return IsEmpty ? 0f : (MaxX - MinX) * (MaxY - MinY) * 0.25f; }
        }

        /// <summary>Grown by <paramref name="pad"/> on every side (in the same -1..1 units), then cut to the view.</summary>
        public ScreenRect Padded(float pad)
        {
            return new ScreenRect(MathF.Max(-1f, MinX - pad), MathF.Max(-1f, MinY - pad), MathF.Min(1f, MaxX + pad), MathF.Min(1f, MaxY + pad));
        }

        public bool Contains(ScreenRect other)
        {
            return other.MinX >= MinX && other.MaxX <= MaxX && other.MinY >= MinY && other.MaxY <= MaxY;
        }
    }

    /// <summary>How often the far side of a portal is drawn afresh. See <see cref="PortalSchedule"/>.</summary>
    public enum PortalRateMode
    {
        /// <summary>Every picture is drawn every frame.</summary>
        EveryFrame = 0,

        /// <summary>
        /// Each picture is drawn every OTHER frame and shown re-projected in between, so each frame draws
        /// half of them: with one portal in view the two eyes take turns, with two in view the two ends do.
        /// </summary>
        HalfRate = 1,

        /// <summary>Never more than one picture a frame: with both ends in view in a headset each is drawn every fourth frame.</summary>
        OnePerFrame = 2,
    }

    /// <summary>
    /// Which far-side pictures are drawn afresh in a frame, and which are kept from an earlier frame and
    /// shown re-projected. The engine patch (P4) carries a copy of exactly these lines.
    /// What matters for holding the headset's frame rate is the cost of the WORST frame, so the turns are
    /// arranged to make every frame draw the same number of pictures.
    /// </summary>
    public static class PortalSchedule
    {
        /// <param name="tick">Goes up by one each frame.</param>
        /// <param name="eye">0 on a flat screen; 0 and 1 for the two eyes of a headset.</param>
        /// <param name="end">0 = orange, 1 = blue.</param>
        /// <param name="activeEnds">How many ends are wanted this frame (1 or 2).</param>
        /// <param name="eyes">1 on a flat screen, 2 in a headset.</param>
        public static bool IsFresh(PortalRateMode mode, int tick, int eye, int end, int activeEnds, int eyes)
        {
            if (tick < 0) tick = -tick;
            if (eye < 0) eye = 0;
            if (eyes < 1) eyes = 1;
            if (eye >= eyes) eye = eyes - 1;
            bool both = activeEnds >= 2;
            switch (mode)
            {
                case PortalRateMode.HalfRate:
                    // Two ends: the ends take turns, both eyes together (a moving thing is then seen at the
                    // same moment by both eyes). One end: the eyes take turns.
                    return ((tick + (both ? end : eye)) & 1) == 0;
                case PortalRateMode.OnePerFrame:
                {
                    int count = eyes * (both ? 2 : 1);
                    int index = both ? eye * 2 + end : eye;
                    return tick % count == index;
                }
                default:
                    return true;
            }
        }

        /// <summary>How many pictures a frame draws afresh under a mode, at most.</summary>
        public static int WorstFrame(PortalRateMode mode, int activeEnds, int eyes)
        {
            int ends = activeEnds >= 2 ? 2 : 1;
            int worst = 0;
            for (int tick = 0; tick < 8; tick++)
            {
                int n = 0;
                for (int eye = 0; eye < eyes; eye++)
                {
                    for (int end = 0; end < ends; end++)
                    {
                        if (IsFresh(mode, tick, eye, end, activeEnds, eyes)) n++;
                    }
                }
                if (n > worst) worst = n;
            }
            return worst;
        }
    }

    /// <summary>
    /// The close-up rule for ONE eye (in a headset each eye is a view of its own, up to 35 mm from the
    /// middle of the head, and can be through a wall when the head is not, or behind it when the head has
    /// been carried on).
    ///
    /// The wall's paint panel draws an opening for an eye that is comfortably in front of it. For an eye
    /// nearer than the guard distance, or already past the wall inside the opening, the panel leaves the
    /// opening out and a sheet fixed in front of the camera draws it, working out per pixel what that eye
    /// sees. Both decide from the position of the eye being drawn, so the two can never disagree.
    /// </summary>
    public static class EyeGuard
    {
        /// <summary>
        /// How near (metres in front of the wall) an eye may come before the sheet takes over from the panel:
        /// the distance at which the far corners of the camera's near cut reach the panel, plus the panel's
        /// own stand-off, a margin, and how far the head moves in one frame.
        /// </summary>
        public static float Distance(float nearMetres, LensTangents lens, float margin, float panelOffset, float speed, float frameSeconds)
        {
            float travel = MathF.Max(0f, speed) * MathF.Max(0f, frameSeconds);
            if (travel > 0.30f) travel = 0.30f;
            return HeadClipGuard.NearCornerDistance(nearMetres, lens.MaxHorizontal, lens.MaxVertical) + margin + panelOffset + travel;
        }

        /// <summary>True when this eye's openings are the sheet's to draw (the panel then leaves them out).</summary>
        /// <param name="eyeOutOfWall">The eye's distance in front of the wall, metres; negative = past it.</param>
        public static bool SheetDraws(float eyeOutOfWall, float guardDistance)
        {
            return eyeOutOfWall < guardDistance;
        }

        /// <summary>What one line of sight shows.</summary>
        public enum Sight
        {
            /// <summary>The room this eye is in (or is leaving), drawn as if the wall were not there when the eye is past it.</summary>
            ThisSide = 0,

            /// <summary>What lies beyond the portal, as this eye carried through the crossing would see it.</summary>
            FarSide = 1,
        }

        /// <summary>
        /// What one line of sight of an eye at an opening must show. Used by the tests and the picture checks.
        /// </summary>
        /// <param name="eyeOutOfWall">The eye's distance in front of the wall, metres; negative = past it.</param>
        /// <param name="rayIntoWall">How fast the line of sight closes on the wall: -dot(ray, wall normal). Positive = looking at (or deeper into) the wall.</param>
        /// <param name="hitsOpening">For an eye in front: does the line of sight meet the wall inside the opening? For an eye past the wall: is the eye itself inside the opening?</param>
        public static Sight WhatIsSeen(float eyeOutOfWall, float rayIntoWall, bool hitsOpening)
        {
            if (eyeOutOfWall > 0f)
            {
                if (rayIntoWall > 0f && hitsOpening) return Sight.FarSide;
                return Sight.ThisSide;
            }
            // Past the wall, standing in the opening: onward is the far side, back is the room being left.
            if (!hitsOpening) return Sight.ThisSide;
            return rayIntoWall > 0f ? Sight.FarSide : Sight.ThisSide;
        }
    }

    /// <summary>
    /// Engine-free maths for a far-side picture that is KEPT from one frame to the next: drawing only the
    /// part of it an opening covers, and looking a kept picture up from somewhere else than where it was
    /// taken (the head has moved since: "re-projection"; or the picture is wanted for an opening seen
    /// through an opening: the endless tunnel between two portals).
    ///
    /// THE LOOK-UP. A line of sight that enters an opening is carried through the crossing, then followed
    /// to where it leaves the room (the room's box stands in for the true depth of what is seen: exact for
    /// the walls, floor and ceiling, a little off for things standing in the room). That point is then
    /// found in the kept picture with the picture's own camera. Nothing here cares where the kept picture
    /// was taken from, so the same look-up serves a head that moved, an eye that was carried through, and
    /// each further level of a portal seen through a portal.
    ///
    /// Layouts as in <see cref="CameraConventions"/>: row vectors; the engine patch and the shader use the
    /// transposes.
    /// </summary>
    public static class PortalPictureMath
    {
        /// <summary>World to clip space for a camera: a world point p becomes Vec4.Transform((p, 1), result).</summary>
        public static Mat4 ViewProjection(Vec3 position, Quat rotation, Mat4 projection)
        {
            return CameraConventions.ViewMatrix(position, rotation) * projection;
        }

        /// <summary>
        /// One row of the world-to-clip matrix the way a shader wants it: clip[row] = dot(result, (p, 1)).
        /// (Column <paramref name="row"/> of the row-vector matrix.)
        /// </summary>
        public static Vec4 ShaderRow(Mat4 viewProjection, int row)
        {
            switch (row)
            {
                case 0: return new Vec4(viewProjection.M11, viewProjection.M21, viewProjection.M31, viewProjection.M41);
                case 1: return new Vec4(viewProjection.M12, viewProjection.M22, viewProjection.M32, viewProjection.M42);
                case 2: return new Vec4(viewProjection.M13, viewProjection.M23, viewProjection.M33, viewProjection.M43);
                default: return new Vec4(viewProjection.M14, viewProjection.M24, viewProjection.M34, viewProjection.M44);
            }
        }

        /// <summary>
        /// A projection that draws only <paramref name="rect"/> of the view the given one draws, stretched
        /// over the whole picture. Depth is untouched, so a cut-off (oblique) projection stays cut off at the
        /// same plane. Row-vector layout.
        /// </summary>
        public static Mat4 Crop(Mat4 projection, ScreenRect rect)
        {
            float sx = 2f / (rect.MaxX - rect.MinX);
            float ox = -(rect.MaxX + rect.MinX) / (rect.MaxX - rect.MinX);
            float sy = 2f / (rect.MaxY - rect.MinY);
            float oy = -(rect.MaxY + rect.MinY) / (rect.MaxY - rect.MinY);
            Mat4 m = projection;
            // x' = sx * x + ox * w, y' = sy * y + oy * w: columns 1 and 2 take a share of column 4.
            m.M11 = sx * projection.M11 + ox * projection.M14;
            m.M21 = sx * projection.M21 + ox * projection.M24;
            m.M31 = sx * projection.M31 + ox * projection.M34;
            m.M41 = sx * projection.M41 + ox * projection.M44;
            m.M12 = sy * projection.M12 + oy * projection.M14;
            m.M22 = sy * projection.M22 + oy * projection.M24;
            m.M32 = sy * projection.M32 + oy * projection.M34;
            m.M42 = sy * projection.M42 + oy * projection.M44;
            return m;
        }

        /// <summary>
        /// The same for a COLUMN-vector projection (the engine's layout): rows 1 and 2 take a share of row 4.
        /// The engine patch (P4) carries a copy of exactly these lines.
        /// </summary>
        public static Mat4 CropColumnLayout(Mat4 projection, ScreenRect rect)
        {
            float sx = 2f / (rect.MaxX - rect.MinX);
            float ox = -(rect.MaxX + rect.MinX) / (rect.MaxX - rect.MinX);
            float sy = 2f / (rect.MaxY - rect.MinY);
            float oy = -(rect.MaxY + rect.MinY) / (rect.MaxY - rect.MinY);
            Mat4 m = projection;
            m.M11 = sx * projection.M11 + ox * projection.M41;
            m.M12 = sx * projection.M12 + ox * projection.M42;
            m.M13 = sx * projection.M13 + ox * projection.M43;
            m.M14 = sx * projection.M14 + ox * projection.M44;
            m.M21 = sy * projection.M21 + oy * projection.M41;
            m.M22 = sy * projection.M22 + oy * projection.M42;
            m.M23 = sy * projection.M23 + oy * projection.M43;
            m.M24 = sy * projection.M24 + oy * projection.M44;
            return m;
        }

        /// <summary>
        /// Grow a rectangle of the view outward to whole pixels of a picture <paramref name="width"/> by
        /// <paramref name="height"/>. Returns the rectangle those pixels really cover; x, y (from the TOP),
        /// w, h are the pixels. Never smaller than 8 pixels each way.
        /// </summary>
        public static ScreenRect SnapToPixels(ScreenRect rect, int width, int height, out int x, out int y, out int w, out int h)
        {
            int x0 = (int)MathF.Floor((rect.MinX * 0.5f + 0.5f) * width);
            int x1 = (int)MathF.Ceiling((rect.MaxX * 0.5f + 0.5f) * width);
            int y0 = (int)MathF.Floor((0.5f - rect.MaxY * 0.5f) * height);
            int y1 = (int)MathF.Ceiling((0.5f - rect.MinY * 0.5f) * height);
            if (x1 - x0 < 8) { x0 -= 4; x1 = x0 + 8; }
            if (y1 - y0 < 8) { y0 -= 4; y1 = y0 + 8; }
            if (x0 < 0) { x1 = Math.Min(width, x1 - x0); x0 = 0; }
            if (y0 < 0) { y1 = Math.Min(height, y1 - y0); y0 = 0; }
            if (x1 > width) { x0 = Math.Max(0, x0 - (x1 - width)); x1 = width; }
            if (y1 > height) { y0 = Math.Max(0, y0 - (y1 - height)); y1 = height; }
            x = x0;
            y = y0;
            w = x1 - x0;
            h = y1 - y0;
            return new ScreenRect(2f * x0 / width - 1f, 1f - 2f * y1 / height, 2f * x1 / width - 1f, 1f - 2f * y0 / height);
        }

        /// <summary>
        /// The part of the view a flat four-cornered patch covers (corners in order round it, world space),
        /// cut at <paramref name="near"/> in front of the eye first. Empty when none of it is in view.
        /// </summary>
        public static ScreenRect BoundsOnScreen(Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec3 eyePosition, Quat eyeRotation, LensTangents lens, float near)
        {
            Vec3 va = CameraConventions.WorldToView(a, eyePosition, eyeRotation);
            Vec3 vb = CameraConventions.WorldToView(b, eyePosition, eyeRotation);
            Vec3 vc = CameraConventions.WorldToView(c, eyePosition, eyeRotation);
            Vec3 vd = CameraConventions.WorldToView(d, eyePosition, eyeRotation);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            int points = 0;
            Edge(va, vb, near, lens, ref minX, ref minY, ref maxX, ref maxY, ref points);
            Edge(vb, vc, near, lens, ref minX, ref minY, ref maxX, ref maxY, ref points);
            Edge(vc, vd, near, lens, ref minX, ref minY, ref maxX, ref maxY, ref points);
            Edge(vd, va, near, lens, ref minX, ref minY, ref maxX, ref maxY, ref points);
            if (points == 0) return new ScreenRect(0f, 0f, 0f, 0f);
            return new ScreenRect(MathF.Max(minX, -1f), MathF.Max(minY, -1f), MathF.Min(maxX, 1f), MathF.Min(maxY, 1f));
        }

        private static void Edge(Vec3 p, Vec3 q, float near, LensTangents lens, ref float minX, ref float minY, ref float maxX, ref float maxY, ref int points)
        {
            float dp = -p.Z - near;
            float dq = -q.Z - near;
            if (dp < 0f && dq < 0f) return;
            if (dp < 0f) p = p + (q - p) * (dp / (dp - dq));
            else if (dq < 0f) q = q + (p - q) * (dq / (dq - dp));
            Point(p, lens, ref minX, ref minY, ref maxX, ref maxY);
            Point(q, lens, ref minX, ref minY, ref maxX, ref maxY);
            points += 2;
        }

        private static void Point(Vec3 v, LensTangents lens, ref float minX, ref float minY, ref float maxX, ref float maxY)
        {
            float depth = MathF.Max(1e-6f, -v.Z);
            float x = (2f * v.X / depth - (lens.Right + lens.Left)) / (lens.Right - lens.Left);
            float y = (2f * v.Y / depth - (lens.Up + lens.Down)) / (lens.Up - lens.Down);
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        /// <summary>
        /// True when no line of sight of this view closes on the wall: every corner of the view looks along
        /// the wall or away from it. Then nothing of an opening in that wall can be seen, from in front or
        /// from an eye standing in it, and its far side need not be drawn.
        /// </summary>
        /// <param name="wallNormal">Out of the wall, into the room.</param>
        public static bool LooksAwayFrom(Quat eyeRotation, LensTangents lens, Vec3 wallNormal)
        {
            Vec3 forward = Vec3.Transform(new Vec3(1f, 0f, 0f), eyeRotation);
            Vec3 left = Vec3.Transform(new Vec3(0f, 1f, 0f), eyeRotation);
            Vec3 up = Vec3.Transform(new Vec3(0f, 0f, 1f), eyeRotation);
            float f = Vec3.Dot(forward, wallNormal);
            float r = -Vec3.Dot(left, wallNormal);
            float u = Vec3.Dot(up, wallNormal);
            // The view's four corner rays: forward + tanX * right + tanY * up. The dot with the normal is
            // linear in each, so the least value is at a corner.
            float least = f + MathF.Min(lens.Left * r, lens.Right * r) + MathF.Min(lens.Down * u, lens.Up * u);
            return least >= 0f;
        }

        /// <summary>
        /// Where a line leaves a box (the room). <paramref name="origin"/> may be outside the box (behind the
        /// exit wall): the line then goes in through the wall and out the far side, and it is the far side
        /// that is returned.
        /// </summary>
        public static Vec3 RoomExit(Vec3 origin, Vec3 direction, Vec3 roomMin, Vec3 roomMax)
        {
            float t = MathF.Min(FarSlab(origin.X, direction.X, roomMin.X, roomMax.X),
                MathF.Min(FarSlab(origin.Y, direction.Y, roomMin.Y, roomMax.Y), FarSlab(origin.Z, direction.Z, roomMin.Z, roomMax.Z)));
            if (!(t > 0f)) t = 0f;
            return origin + direction * t;
        }

        private static float FarSlab(float o, float d, float min, float max)
        {
            if (MathF.Abs(d) < 1e-6f) d = d < 0f ? -1e-6f : 1e-6f;
            float t1 = (min - o) / d;
            float t2 = (max - o) / d;
            return t1 > t2 ? t1 : t2;
        }

        /// <summary>
        /// Look a kept picture up for one line of sight that enters an opening (see the class comment).
        /// </summary>
        /// <param name="crossing">The opening's crossing (the player's rigid one).</param>
        /// <param name="eye">Where the line of sight starts: the eye of the view being drawn.</param>
        /// <param name="ray">Its direction (any length).</param>
        /// <param name="keptViewProjection">World to clip of the camera the kept picture was taken with (<see cref="ViewProjection"/>).</param>
        /// <param name="uv">Where in the kept picture to read: 0..1 across, 0..1 DOWN.</param>
        /// <param name="scenePoint">The point of the room that was looked up.</param>
        /// <returns>False when that point is behind the kept picture's camera (uv is then meaningless). Whether uv is inside the picture is the caller's to check.</returns>
        public static bool Lookup(in RigidTransform crossing, Vec3 eye, Vec3 ray, Vec3 roomMin, Vec3 roomMax, Mat4 keptViewProjection, out Vec2 uv, out Vec3 scenePoint)
        {
            Vec3 origin = crossing.TransformPoint(eye);
            Vec3 direction = crossing.TransformDirection(ray);
            scenePoint = RoomExit(origin, direction, roomMin, roomMax);
            Vec4 clip = Vec4.Transform(new Vec4(scenePoint, 1f), keptViewProjection);
            if (!(clip.W > 1e-6f))
            {
                uv = new Vec2(0.5f, 0.5f);
                return false;
            }
            uv = new Vec2(0.5f + 0.5f * clip.X / clip.W, 0.5f - 0.5f * clip.Y / clip.W);
            return true;
        }
    }
}
