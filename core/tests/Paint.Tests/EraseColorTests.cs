using System;
using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// PaintSurface.EraseColor: take one colour off the whole wall (added for "one portal patch per colour": when a
    /// colour's patch moves, its old paint is wiped). It must do exactly what EraseDisc with a wall-sized disc does,
    /// leave the other colour alone, keep the paint budget balanced, and only report the texels it really changed.
    /// </summary>
    public class EraseColorTests
    {
        // Orange and blue splats that overlap a little, run long enough that both have drips going.
        private static PaintSurface Painted()
        {
            var s = new PaintSurface(LookSheets.Wall());
            s.Splat(PaintColor.Orange, new Vec2(1.6f, 2.2f), 0.42f, new Vec2(1f, -0.5f), 5u, 10f);
            s.Splat(PaintColor.Blue, new Vec2(2.3f, 2.0f), 0.40f, new Vec2(-2f, 0f), 6u, 10f);
            for (int i = 0; i < 60; i++) s.Step(1f / 90f);
            return s;
        }

        private static int DripsOf(PaintSurface s, PaintColor color)
        {
            int n = 0;
            for (int k = 0; k < s.ActiveDripCount; k++)
            {
                s.GetDrip(k, out _, out _, out PaintColor c, out _);
                if (c == color) n++;
            }
            return n;
        }

        [Fact]
        public void RemovesOnlyThatColourEverywhere()
        {
            var s = Painted();
            Assert.True(DripsOf(s, PaintColor.Orange) > 0, "the set-up should have orange drips running");
            Assert.True(DripsOf(s, PaintColor.Blue) > 0, "the set-up should have blue drips running");

            var blueBefore = new float[s.Width * s.Height];
            for (int y = 0; y < s.Height; y++)
                for (int x = 0; x < s.Width; x++)
                    blueBefore[x + y * s.Width] = s.GetField(PaintColor.Blue, x, y);
            int blueDripsBefore = DripsOf(s, PaintColor.Blue);

            s.EraseColor(PaintColor.Orange);

            for (int y = 0; y < s.Height; y++)
            {
                for (int x = 0; x < s.Width; x++)
                {
                    Assert.Equal(0f, s.GetField(PaintColor.Orange, x, y));
                    Assert.Equal(blueBefore[x + y * s.Width], s.GetField(PaintColor.Blue, x, y));
                }
            }
            Assert.Equal(0, DripsOf(s, PaintColor.Orange));
            Assert.Equal(blueDripsBefore, DripsOf(s, PaintColor.Blue));
            Assert.False(s.IsPainted(PaintColor.Orange, new Vec2(1.6f, 2.2f)));
            Assert.True(s.IsPainted(PaintColor.Blue, new Vec2(2.3f, 2.0f)));

            // No paint made or lost: what was removed is booked as erased.
            PaintBudget b = s.ComputeBudget();
            Assert.True(b.Erased > 0.0);
            Assert.True(Math.Abs(b.Unaccounted) < b.Deposited * 1e-4, $"unaccounted {b.Unaccounted} of {b.Deposited}");
        }

        [Fact]
        public void MatchesEraseDiscWithAWallSizedDisc()
        {
            var a = Painted();
            var b = Painted();
            WallFrame wall = a.Wall;

            a.EraseColor(PaintColor.Orange);
            b.EraseDisc(new Vec2(wall.Width * 0.5f, wall.Height * 0.5f), wall.Width + wall.Height, PaintColor.Orange);

            for (int y = 0; y < a.Height; y++)
            {
                for (int x = 0; x < a.Width; x++)
                {
                    Assert.Equal(b.GetField(PaintColor.Orange, x, y), a.GetField(PaintColor.Orange, x, y));
                    Assert.Equal(b.GetField(PaintColor.Blue, x, y), a.GetField(PaintColor.Blue, x, y));
                    Assert.Equal(b.GetThickness(x, y), a.GetThickness(x, y), 7);
                    Assert.Equal(b.GetWetness(x, y), a.GetWetness(x, y));
                }
            }
            Assert.Equal(b.ActiveDripCount, a.ActiveDripCount);
            Assert.Equal(b.ComputeBudget().Erased, a.ComputeBudget().Erased, 9);

            // And they stay the same afterwards: the blue paint carries on dripping identically.
            for (int i = 0; i < 200; i++) { a.Step(1f / 90f); b.Step(1f / 90f); }
            var ba = new byte[a.RgbaByteCount];
            var bb = new byte[b.RgbaByteCount];
            a.CopyAllToRgba(ba);
            b.CopyAllToRgba(bb);
            Assert.Equal(bb, ba);
        }

        [Fact]
        public void ReportsOnlyTheTexelsItChanged()
        {
            var s = new PaintSurface(LookSheets.Wall());
            s.PaintDisc(PaintColor.Orange, new Vec2(1.0f, 1.0f), 0.2f);
            s.PaintDisc(PaintColor.Blue, new Vec2(3.0f, 2.0f), 0.2f);
            s.CopyDirtyToRgba(new byte[s.RgbaByteCount]);
            s.TakeShapeChanges(PaintColor.Orange);
            s.TakeShapeChanges(PaintColor.Blue);

            s.EraseColor(PaintColor.Orange);

            // The changed rectangle hugs the orange disc (radius 0.2 m plus its soft edge), nowhere near the whole wall.
            TexelRect dirty = s.DirtyRect;
            Assert.False(dirty.IsEmpty);
            float tpm = s.Settings.TexelsPerMeter;
            Assert.True(dirty.Width <= (int)(0.6f * tpm) && dirty.Height <= (int)(0.6f * tpm), $"dirty {dirty}");
            Assert.True(dirty.Contains((int)(1.0f * tpm), (int)(1.0f * tpm)));
            Assert.False(dirty.Contains((int)(3.0f * tpm), (int)(2.0f * tpm)));

            Assert.False(s.TakeShapeChanges(PaintColor.Orange).IsEmpty);
            Assert.True(s.TakeShapeChanges(PaintColor.Blue).IsEmpty);

            // Erasing a colour that is not there changes nothing.
            s.CopyDirtyToRgba(new byte[s.RgbaByteCount]);
            s.EraseColor(PaintColor.Orange);
            Assert.True(s.DirtyRect.IsEmpty);
            Assert.True(s.TakeShapeChanges(PaintColor.Orange).IsEmpty);
        }
    }
}
