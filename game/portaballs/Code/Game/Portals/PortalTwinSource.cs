using System.Collections.Generic;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// Marks a HAND (or anything else carried about with no physics) whose pictures must show at both ends
/// of a portal while it is in an opening. It sits on the hand's own object; that object's position is
/// the hand's centre. <see cref="PortalBallTwins"/> finds every one of these each frame and does the work;
/// this component only holds the list of things to draw twice and what was worked out last frame.
///
/// To use: <c>PortalTwinSource.Register( hand, block )</c> once for each thing the hand shows (the hand
/// block, the ball it holds). A thing that is switched off (the held ball while not holding) gets no twin.
/// </summary>
public sealed class PortalTwinSource : Component
{
	/// <summary>
	/// How far in front of an opening's wall the hand's centre may be and still need a twin, metres: the
	/// reach of the biggest thing it shows, plus a little so the twin is in place before any of it shows.
	/// </summary>
	[Property] public float ReachMetres { get; set; } = 0.12f;

	/// <summary>
	/// True for a hand that is carried along when the player's head goes through a portal (the VR hands).
	/// A hand that trails behind the head is then reaching back through the exit; see PortalReach.PlayerCrossed.
	/// </summary>
	public bool FollowsPlayer { get; set; }

	/// <summary>Where the hand was against the linked pair when the latest frame was drawn.</summary>
	public ReachState State { get; internal set; }

	/// <summary>Kept between frames by PortalBallTwins: a hand that went in through an opening stays through until it comes back.</summary>
	public ReachLatch Latch;

	/// <summary>How many of this hand's twins are on show right now.</summary>
	public int ActiveTwins { get; internal set; }

	internal readonly List<GameObject> Visuals = new List<GameObject>();
	internal readonly List<ModelRenderer> VisualRenderers = new List<ModelRenderer>();
	internal readonly List<PaintColor?> BallPaints = new List<PaintColor?>();
	internal readonly List<GameObject> Twins = new List<GameObject>();
	internal readonly List<ModelRenderer> TwinRenderers = new List<ModelRenderer>();

	/// <summary>
	/// Say that <paramref name="visual"/> (an object with a ModelRenderer, on or under <paramref name="hand"/>)
	/// is part of what this hand shows. Makes the hand's PortalTwinSource the first time. Safe to call twice.
	/// The ModelRenderer may be added to the visual after this call: it is looked for when first needed.
	/// </summary>
	/// <param name="ballPaint">For a paint ball: its colour, so the twin can be given the same glow (see PortalBallTwins.CopyLook).</param>
	public static PortalTwinSource Register( GameObject hand, GameObject visual, PaintColor? ballPaint = null )
	{
		if ( !hand.IsValid() || !visual.IsValid() ) return null;
		PortalTwinSource source = hand.GetComponent<PortalTwinSource>( true );
		if ( !source.IsValid() ) source = hand.AddComponent<PortalTwinSource>();
		if ( source.Visuals.Contains( visual ) ) return source;
		source.Visuals.Add( visual );
		source.VisualRenderers.Add( visual.GetComponent<ModelRenderer>( true ) );
		source.BallPaints.Add( ballPaint );
		source.Twins.Add( null );
		source.TwinRenderers.Add( null );
		return source;
	}

	/// <summary>The twin of one of this hand's things, if it is on show (for tests and readouts).</summary>
	public GameObject TwinOf( GameObject visual )
	{
		int i = Visuals.IndexOf( visual );
		return i >= 0 && Twins[i].IsValid() && Twins[i].Enabled ? Twins[i] : null;
	}

	/// <summary>Take every twin of this hand off show.</summary>
	internal void HideTwins()
	{
		for ( int i = 0; i < Twins.Count; i++ )
		{
			if ( Twins[i].IsValid() && Twins[i].Enabled ) Twins[i].Enabled = false;
		}
		ActiveTwins = 0;
	}

	protected override void OnDisabled()
	{
		HideTwins();
		State = default;
		Latch = default;
	}

	protected override void OnDestroy()
	{
		for ( int i = 0; i < Twins.Count; i++ )
		{
			if ( Twins[i].IsValid() ) Twins[i].Destroy();
		}
	}
}
