using System;

namespace Portaballs.Core
{
    // How far a splat can reach, and whether a small splat would land only on bare wall. Added 2026-11-19 for the
    // droplets that bounce off a burst (the tester's request, see SplatFeel): a droplet may only paint where it cannot touch
    // any other paint, so it can never cut into, join or reshape a portal patch.
    public sealed partial class PaintSurface
    {
        /// <summary>
        /// The farthest any texel of a splat with these numbers can be painted from its impact point uv, in metres,
        /// whatever its seed: body, lobes, spikes, beads, satellite droplets and the soft edge, after stretching and
        /// the throw shift. It is a safe upper bound worked out from the same formulas the stamp uses (a real splat
        /// usually stays well inside it). The drips a big splat later runs are NOT included: they start at radius
        /// Settings.DripMinSplatRadius.
        /// </summary>
        public float SplatReach(float radius, Vec2 wallVelocity, float normalSpeed = -1f)
        {
            if (!(radius > 0f)) return 0f;
            float R = radius;
            float vt = wallVelocity.Length();
            float ns = normalSpeed >= 0f ? normalSpeed : _s.DefaultNormalSpeed;
            float speed = MathF.Sqrt(vt * vt + ns * ns);
            float spatter = MathUtil.Saturate(speed / MathF.Max(0.1f, _s.SpeedForFullSpatter));
            float glance = vt > 0.3f ? MathUtil.Saturate(vt / (vt + ns + 1e-3f)) : 0f;
            float stretch = MathF.Min(MathF.Max(1f, _s.MaxStretch), 1f + _s.StretchPerSpeed * vt);
            float across = MathF.Pow(stretch, -0.35f);
            float shift = _s.ThrowShift * R * (stretch - 1f);
            float minPart = 0.9f * _cellMin / across;
            float rag = MathF.Abs(_s.Raggedness);

            // In the splat's own unstretched frame, measured from its centre (see BeginJob).
            float body = 0.8f * R * (1f + 1.6f * rag);
            float lobe = 0.85f * R + 0.45f * R * (1f + 1.6f * 0.8f * rag);
            float spikeReach = R * MathF.Max(0f, _s.SpikeLength) * 1.3f * (1f + 0.5f * glance) * (0.85f + 0.3f * spatter);
            float spikeTip = MathF.Max(minPart * 0.8f, 0.03f * R);
            float spikeRoot = 0.65f * R + MathF.Max(minPart * 1.4f, 0.13f * R);
            float bead = MathF.Max(minPart, 0.06f * R);
            float spike = MathF.Max(spikeRoot, spikeReach + spikeTip + 2f * bead + 0.09f * R);
            float sat = R * MathF.Max(1.3f, _s.SatelliteDistance) * (1f + 0.5f * glance) + MathF.Max(minPart, 0.085f * R);
            float local = MathF.Max(MathF.Max(body, lobe), MathF.Max(spike, sat));

            // Melting parts together can push an outline out by a quarter of the widest blend (the lobes', 0.22 R).
            // Coverage is above zero only where the outline distance, divided by its slope (held to 0.3..3), is under
            // the soft edge plus the edge noise, and never beyond the conversion band (see ConvertStep).
            float noise = MathF.Abs(_s.EdgeNoise) * R * 1.25f;
            float band = MathF.Min(1.5f * (3f * _halfEdge + noise), 3f * (_halfEdge + noise));
            local += 0.06f * R + band;

            // A point of the splat's frame lands at most `stretch` times as far out on the wall, around a centre
            // moved `shift` along the throw. One texel more, because texels are judged by their centres.
            return shift + stretch * local + _cellMin;
        }

        /// <summary>
        /// True when a splat with these numbers would land on bare wall only: no paint of either colour, no running
        /// drip, and no splat still waiting to land on this wall within its reach (<see cref="SplatReach"/>) plus
        /// <paramref name="margin"/> metres. Cheap (a small square of texels). For droplets: one that would touch any
        /// paint soaks in instead, so it can never change a portal patch.
        /// </summary>
        public bool IsClearForSplat(Vec2 uv, float radius, Vec2 wallVelocity, float normalSpeed, float margin)
        {
            if (!(radius > 0f)) return false;
            float reach = SplatReach(radius, wallVelocity, normalSpeed) + MathF.Max(0f, margin);

            // Splats still being prepared for this wall: their paint is not on the grid yet.
            for (int k = 0; k < _queueCount; k++)
            {
                ref SplatRequest q = ref _queue[(_queueHead + k) % MaxQueuedSplats];
                float qr = SplatReach(q.Radius, q.WallVelocity, q.NormalSpeed);
                if (Vec2.Distance(q.UV, uv) < reach + qr) return false;
            }

            // Running heads: their trail can lag a little behind them, and they keep moving.
            for (int k = 0; k < _headCount; k++)
            {
                ref DripHead h = ref _heads[k];
                float dx = h.X - uv.X, dy = h.Y - uv.Y;
                float lim = reach + HeadRadius(ref h) + MathF.Max(margin, 2f * _cellMin);
                if (dx * dx + dy * dy < lim * lim) return false;
            }

            // Paint on the wall.
            if (!TexelRange(uv.X - reach, uv.Y - reach, uv.X + reach, uv.Y + reach, out int x0, out int y0, out int x1, out int y1))
                return true; // entirely off the wall: nothing there to touch
            float[] fo = _field[0], fb = _field[1];
            float reach2 = reach * reach;
            for (int y = y0; y <= y1; y++)
            {
                float dv = (y + 0.5f) * TexelSizeV - uv.Y;
                float dv2 = dv * dv;
                if (dv2 > reach2) continue;
                int row = y * Width;
                for (int x = x0; x <= x1; x++)
                {
                    float du = (x + 0.5f) * TexelSizeU - uv.X;
                    if (du * du + dv2 > reach2) continue;
                    int i = row + x;
                    if (fo[i] > 0f || fb[i] > 0f) return false;
                }
            }
            return true;
        }
    }
}
