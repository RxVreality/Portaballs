using System.Collections.Generic;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// Shows a thing at BOTH ends of the portal while it is in an opening, so it never drops out of sight:
/// a ball passing through, and a hand (with the ball it holds) pushed through. One per room (TestRoom adds it).
///
/// BALLS. A ball is carried across in the one physics step in which its centre passes the wall. Before that
/// step its front half is already inside the entry wall (hidden); after it, its back half is still inside
/// the exit wall (hidden). Without a twin, the half that should be poking out of the other end is simply
/// missing for those few frames, in the room and in the picture seen through the portal.
///
/// HANDS. A hand has no physics: pushed at an open patch it just goes into the wall. Every hand carries a
/// <see cref="PortalTwinSource"/>; while its centre is near an end's wall and inside that end's opening,
/// or through it (core PortalReach), each thing it shows gets a twin at the crossing of its pose. Looking
/// into the patch you reach into, the far-side picture draws the twin: the hand carries on into the far
/// room. Looking at the other patch, the twin sticks out of it.
///
/// A twin is a second copy of the model drawn where the core's crossing puts it. It has no physics and no
/// say in anything: it is a picture only.
///
/// WHAT HIDES WHAT (nothing is cut by this code):
///   - the part of the REAL hand that is past the entry wall is behind that wall's paint panel, which is
///     solid and shows the far-side picture: hidden in the room. In a far-side picture it is behind the
///     wall the picture is cut at, or behind a room wall: hidden there too;
///   - the part of the TWIN that has not come out yet is behind the exit wall's plane: hidden in the room
///     by that wall and its panel, and cut off in the far-side picture (which starts 5 mm in front of the
///     exit wall). So the twin grows out of the exit patch exactly as fast as the hand sinks into the entry;
///   - 8 mm of the hand at the surface is drawn by neither (3 mm of panel stand-off on the entry wall, 5 mm
///     of cut in front of the exit wall, PortalView.ClipOffsetMetres). Looking along the arm it cannot be
///     seen. From well to the side and close up it shows as a see-through sliver where the hand meets the
///     surface, 8 mm times the tangent of the viewing angle wide (engine\smoke\hands\seam_*.png). Nothing in
///     this file can close it; a smaller cut on the picture's side would;
///   - the two halves are lit by where they are (the real half by the entry's surroundings, the twin by the
///     exit's), so the shade of the hand can step at the surface. That is what a portal does.
///
/// WHICH CROSSING. The pair's rigid crossing, never slid along the exit wall. <see cref="UsePlayerCrossing"/>
/// picks the full crossing (balls travel by it; the default) or the player crossing (PortalView draws the
/// far side with it). For two standing walls they are the same turn-and-move. Wherever they differ, the
/// far-side picture and this switch must agree, or a hand looks broken where it meets the surface.
///
/// COST. With no linked pair: nothing. With a pair and no hand near it: two plane distances per hand.
/// </summary>
public sealed class PortalBallTwins : Component, Component.ISceneStage
{
	public TestRoom Room { get; set; }

	/// <summary>Twins on show right now (balls in flight).</summary>
	public int ActiveTwins { get; private set; }

	/// <summary>Twins shown since the room was built (each ball that goes through counts once per frame it is twinned).</summary>
	public int TwinFrames { get; private set; }

	/// <summary>Draw hands (and the balls they hold) at both ends. Off = as before 2026-11-19: a hand pushed into a patch just vanishes into the wall.</summary>
	[Property] public bool HandTwins { get; set; } = true;

	/// <summary>
	/// False (default) = hand twins use the pair's full crossing, the one balls travel by. True = the upright
	/// player crossing. Must match what the far-side picture is drawn with wherever the two differ (see class comment).
	/// </summary>
	public static bool UsePlayerCrossing { get; set; }

	/// <summary>
	/// A dial for a headset session, not a fix. Moves every hand twin this far out of the exit wall, metres.
	/// 0 (default) = exactly where the crossing puts it. The 8 mm of the hand that neither view draws (see the
	/// class comment) stays 8 mm whatever this is; what changes is which slice of the hand is left out. At 0 the
	/// far part is in exactly the right place and 8 mm of the hand is skipped. At 0.008 nothing is skipped and
	/// the far part sits 8 mm deeper than it should. In the two close-up pairs taken on a flat screen
	/// (engine\smoke\hands\seam_*.png) 0.008 looked a little better with a ball in the hand and no different without.
	/// </summary>
	[Property] public float SeamShiftMetres { get; set; } = 0f;

	/// <summary>Hand twins on show right now (a hand block and its held ball count as two).</summary>
	public int ActiveHandTwins { get; private set; }

	/// <summary>Hands that were at a portal in the latest frame.</summary>
	public int HandsAtPortal { get; private set; }

	private readonly List<GameObject> _pool = new List<GameObject>();
	private readonly List<ModelRenderer> _poolRenderers = new List<ModelRenderer>();
	private int _crossingsSeen = -1;

	protected override void OnPreRender()
	{
		int used = 0;
		PortalLink portals = Room.IsValid() ? Room.Portals : null;
		if ( portals.IsValid() && portals.HasPair )
		{
			PortalPair pair = portals.Pair;
			foreach ( Portaball ball in Scene.GetAllComponents<Portaball>() )
			{
				// Only a ball that is really on its way through (it has left the physics engine for the passage).
				if ( !ball.IsValid() || ball.Spent || !ball.InPortal ) continue;
				// Drawn position (the engine smooths between physics steps), not the physics position.
				Vec3 p = SboxConvert.ToMetres( ball.WorldPosition );
				float r = Portaball.RadiusMetres;

				// Which end is it cutting? The one whose wall plane passes through the ball, over that end's patch.
				for ( int c = 0; c < 2; c++ )
				{
					bool fromA = c == 0;
					PortalEnd end = fromA ? pair.A : pair.B;
					float d = end.Wall.SignedDistance( p );
					if ( d > r || d < -r ) continue;
					if ( !NearOpening( end, p ) ) continue;

					RigidTransform crossing = pair.Crossing( fromA );
					GameObject twin = Take( used++, ball.Paint );
					twin.WorldPosition = SboxConvert.ToInches( crossing.TransformPoint( p ) );
					twin.WorldRotation = SboxConvert.ToEngine( crossing.TransformRotation( SboxConvert.ToCore( ball.WorldRotation ) ) );
					break;
				}
			}
		}
		for ( int i = used; i < _pool.Count; i++ )
		{
			if ( _pool[i].IsValid() && _pool[i].Enabled ) _pool[i].Enabled = false;
		}
		ActiveTwins = used;
		TwinFrames += used;
	}

	// Over this end's patch (both ends can be on one wall: this tells them apart).
	private static bool NearOpening( in PortalEnd end, Vec3 p )
	{
		Vec2 uv = end.Wall.ToUV( p );
		const float pad = 0.05f;
		return uv.X >= end.Opening.BoundsMin.X - pad && uv.X <= end.Opening.BoundsMax.X + pad
			&& uv.Y >= end.Opening.BoundsMin.Y - pad && uv.Y <= end.Opening.BoundsMax.Y + pad;
	}

	private GameObject Take( int index, PaintColor paint )
	{
		while ( _pool.Count <= index )
		{
			GameObject go = new GameObject( GameObject, false, "Portaball twin (picture only)" );
			go.LocalScale = SboxConvert.SphereScaleFor( Portaball.RadiusMetres );
			ModelRenderer renderer = go.AddComponent<ModelRenderer>();
			renderer.Model = Model.Sphere;
			_pool.Add( go );
			_poolRenderers.Add( renderer );
		}
		GameObject twin = _pool[index];
		PaintLook.Glow( _poolRenderers[index], paint );
		if ( !twin.Enabled ) twin.Enabled = true;
		return twin;
	}

	// ------------------------------------------------------------------ hands

	void Component.ISceneStage.Start()
	{
	}

	/// <summary>
	/// The very end of the frame's update, after every component's OnPreRender. The engine moves a VR hand
	/// to its final place for the frame in ITS OnPreRender (VRTrackedObject), and components are not woken in
	/// a promised order, so doing this in our own OnPreRender could place a twin from a pose one step old:
	/// a hand that jumps with the play space (the head going through a portal) would leave its twin behind
	/// for a frame.
	/// </summary>
	void Component.ISceneStage.End()
	{
		PlaceHandTwins();
	}

	/// <summary>Work out every hand's twins now. Called by the engine once a frame; a test that moves a hand and takes a picture in one call may call it too.</summary>
	public void PlaceHandTwins()
	{
		PortalLink portals = Room.IsValid() ? Room.Portals : null;
		bool linked = HandTwins && portals.IsValid() && portals.HasPair;
		int hands = 0, twins = 0;

		PortalPair pair = default;
		IPaintMask maskA = null, maskB = null;
		bool playerCrossed = false, playerFromA = false;
		if ( linked )
		{
			pair = portals.Pair;
			maskA = portals.MaskOf( PaintColor.Orange );
			maskB = portals.MaskOf( PaintColor.Blue );

			// Did the player's head go through since last frame? (The play space, and every hand on it, was carried.)
			int count = portals.CrossingCount;
			if ( _crossingsSeen < 0 ) _crossingsSeen = count;
			if ( count != _crossingsSeen )
			{
				IReadOnlyList<PortalLink.CrossingRecord> list = portals.Crossings;
				int fresh = System.Math.Min( count - _crossingsSeen, list.Count );
				for ( int i = list.Count - fresh; i < list.Count; i++ )
				{
					if ( i < 0 || !list[i].IsPlayer ) continue;
					playerCrossed = true;
					playerFromA = list[i].FromOrange;
				}
				_crossingsSeen = count;
			}
		}
		else
		{
			_crossingsSeen = -1;
		}

		foreach ( PortalTwinSource source in Scene.GetAllComponents<PortalTwinSource>() )
		{
			if ( !source.IsValid() ) continue;
			if ( !linked )
			{
				if ( source.ActiveTwins > 0 ) source.HideTwins();
				source.State = default;
				source.Latch = default;
				continue;
			}

			Vec3 centre = SboxConvert.ToMetres( source.WorldPosition );
			if ( playerCrossed && source.FollowsPlayer ) PortalReach.PlayerCrossed( ref source.Latch, pair, playerFromA, centre, PortalReach.ArmReach );

			ReachState state = PortalReach.Update( ref source.Latch, pair, maskA, maskB, centre, PortalReach.OpeningRim, source.ReachMetres,
				PortalReach.ArmReach, UsePlayerCrossing, portals.Settings );
			source.State = state;
			if ( !state.AtPortal )
			{
				if ( source.ActiveTwins > 0 ) source.HideTwins();
				continue;
			}
			hands++;

			RigidTransform crossing = PortalReach.Crossing( pair, state.FromA, UsePlayerCrossing );
			Vec3 seam = ( state.FromA ? pair.B : pair.A ).Normal * SeamShiftMetres;
			int shown = 0;
			for ( int i = 0; i < source.Visuals.Count; i++ )
			{
				GameObject visual = source.Visuals[i];
				ModelRenderer from = source.VisualRenderers[i];
				GameObject twin = source.Twins[i];
				if ( !from.IsValid() && visual.IsValid() && visual.Active ) source.VisualRenderers[i] = from = visual.GetComponent<ModelRenderer>( true );
				if ( !visual.IsValid() || !visual.Active || !from.IsValid() )
				{
					if ( twin.IsValid() && twin.Enabled ) twin.Enabled = false;
					continue;
				}
				if ( !twin.IsValid() )
				{
					twin = new GameObject( GameObject, false, visual.Name + " twin (picture only)" );
					source.Twins[i] = twin;
					source.TwinRenderers[i] = twin.AddComponent<ModelRenderer>();
				}
				CopyLook( source.TwinRenderers[i], from, source.BallPaints[i] );

				// The drawn pose of the real thing, carried across. A rigid crossing never changes size.
				Transform real = visual.WorldTransform;
				twin.WorldPosition = SboxConvert.ToInches( crossing.TransformPoint( SboxConvert.ToMetres( real.Position ) ) + seam );
				twin.WorldRotation = SboxConvert.ToEngine( crossing.TransformRotation( SboxConvert.ToCore( real.Rotation ) ) );
				twin.WorldScale = real.Scale;
				if ( !twin.Enabled ) twin.Enabled = true;
				shown++;
			}
			source.ActiveTwins = shown;
			twins += shown;
		}

		HandsAtPortal = hands;
		ActiveHandTwins = twins;
	}

	/// <summary>
	/// Make a twin look like the thing it copies: same model, tint and material. A value set on the real
	/// renderer itself (not on its material) cannot be read back, so the one such value in the game is sent
	/// again here: the glow colour of a paint ball's own material ("GlowColor"). Harmless when no such material is in use.
	/// </summary>
	private static void CopyLook( ModelRenderer to, ModelRenderer from, PaintColor? ballPaint )
	{
		if ( to.Model != from.Model ) to.Model = from.Model;
		if ( to.Tint != from.Tint ) to.Tint = from.Tint;
		if ( to.MaterialOverride != from.MaterialOverride ) to.MaterialOverride = from.MaterialOverride;
		if ( ballPaint.HasValue && from.MaterialOverride != null ) to.Attributes.Set( "GlowColor", PaintPanel.LinearColour( ballPaint.Value ) );
	}
}
