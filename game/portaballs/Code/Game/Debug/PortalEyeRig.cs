using System;
using System.Collections.Generic;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// The headset case on a flat screen: ONE EYE of a head, going through a portal.
///
/// In a headset each eye is drawn as a view of its own, 35 mm to one side of the middle of the head, but
/// the play space is carried through a portal when the MIDDLE of the head passes the wall. Approaching at
/// an angle, one eye is through the wall a few centimetres before that moment, and the other is still
/// behind the exit wall a few centimetres after it. The desk camera normally IS the head, so none of the
/// earlier close-up tests could see what such an eye sees.
///
/// This rig keeps a head (position, facing, a direction of travel) that the game is told to treat as the
/// head (PortalView.SetTestHead: every once-a-frame decision is made for it, as for Input.VR.Head), and
/// stands the desk camera at one of its eyes. <see cref="Advance"/> moves the head and carries it through
/// a linked patch under the same core rule as VrRig (PlayerPassage.TryCross on the head's centre).
///
/// THE CHECK (<see cref="Check"/>). What the eye must see is known without any of the see-through code
/// (core EyeGuard.WhatIsSeen): a line of sight that goes through the opening shows the far side, which an
/// ordinary camera stood at the eye carried through the crossing draws with the exit wall left out; an
/// eye past the wall, looking back out, shows the room it is in with that wall left out. The rig takes
/// those reference pictures (walls are left out by tag, which is instant; nothing is switched off) and
/// counts the pixels of the eye's picture that differ from them.
///
/// Nothing here runs unless a script calls it (engine\seethrough_eye.ps1).
/// </summary>
public sealed class PortalEyeRig : Component
{
	public TestRoom Room { get; set; }
	public FlatThrower Desk { get; set; }

	/// <summary>Radius of the head for "does it fit through the patch", metres (as VrRig).</summary>
	public float HeadRadius { get; set; } = 0.10f;

	public bool Running { get; private set; }

	/// <summary>The middle of the head, metres, and which way it faces.</summary>
	public Vec3 Head { get; private set; }
	public Quat HeadRotation { get; private set; } = Quat.Identity;

	/// <summary>The direction the head moves along (unit). Carried through a crossing like everything else.</summary>
	public Vec3 Travel { get; private set; } = new Vec3( 1f, 0f, 0f );

	/// <summary>How far the eye is to the head's LEFT, metres (negative = the right eye).</summary>
	public float EyeSide { get; private set; } = 0.035f;

	public int Crossings { get; private set; }

	/// <summary>
	/// Draw the eye with the projection the engine builds for a headset eye (VRSystem.CreateProjection), whose
	/// depth terms put the near cut at about one inch whatever ZNear says, instead of the desk camera's own
	/// (near cut at ZNear, half an inch). Whether the headset's eye pass really uses that matrix is not known
	/// (rendering_findings.md); with this on, the flat screen behaves as it would if it does.
	/// </summary>
	public bool VrNearCut { get; set; }

	/// <summary>The camera's far distance in inches, as the VR matrix uses it.</summary>
	private const float FarInches = 10000f;

	/// <summary>VRSystem.CreateProjection, written out (row-vector layout, then transposed as the engine does), for a symmetric lens.</summary>
	public static Mat4 VrStyleProjection( float tanX, float tanY, float far )
	{
		float l = -tanX, r = tanX, d = -tanY, u = tanY;
		Mat4 m = new Mat4(
			2f / ( r - l ), 0f, 0f, 0f,
			0f, 2f / ( u - d ), 0f, 0f,
			( r + l ) / ( r - l ), ( u + d ) / ( u - d ), -far / ( far - 1f ), -far / ( far - 1f ),
			0f, 0f, -1f, 0f );
		return Mat4.Transpose( m );
	}

	private void SetProjection( int width, int height )
	{
		CameraComponent camera = Scene.Camera;
		if ( !VrNearCut ) { camera.CustomProjectionMatrix = null; return; }
		float tanX = MathF.Tan( camera.FieldOfView * ( MathF.PI / 360f ) );
		camera.CustomProjectionMatrix = VrStyleProjection( tanX, tanX * height / width, FarInches );
	}

	/// <summary>The eye the camera stands at.</summary>
	public Vec3 Eye => Head + Vec3.Transform( new Vec3( 0f, EyeSide, 0f ), HeadRotation );

	private const string WallTagPrefix = "pb_eyerig_wall_";
	private bool _tagged;

	/// <summary>The room's rig (made the first time it is asked for).</summary>
	public static PortalEyeRig Get( TestRoom room, FlatThrower desk )
	{
		PortalEyeRig rig = room.GameObject.GetComponent<PortalEyeRig>();
		if ( !rig.IsValid() ) rig = room.GameObject.AddComponent<PortalEyeRig>();
		rig.Room = room;
		rig.Desk = desk;
		rig.TagWalls();
		return rig;
	}

	// Each wall and its paint panel get a tag of their own, so a reference picture can leave one wall out
	// with a camera tag (instant) instead of switching objects off (a frame late).
	private void TagWalls()
	{
		if ( _tagged || !Room.IsValid() ) return;
		_tagged = true;
		for ( int i = 0; i < Room.Walls.Count; i++ )
		{
			PaintWall wall = Room.Walls[i];
			if ( !wall.IsValid() ) continue;
			wall.GameObject.Tags.Add( WallTagPrefix + i );
			if ( wall.Panel != null && wall.Panel.Object.IsValid() ) wall.Panel.Object.Tags.Add( WallTagPrefix + i );
		}
	}

	private int IndexOf( PaintWall wall )
	{
		for ( int i = 0; i < Room.Walls.Count; i++ )
		{
			if ( Room.Walls[i] == wall ) return i;
		}
		return -1;
	}

	/// <summary>
	/// Start: the head here, facing <paramref name="forward"/> with <paramref name="up"/> up, moving along
	/// <paramref name="travel"/>; the camera at the eye <paramref name="eyeSide"/> metres to its left.
	/// </summary>
	public string Begin( Vec3 head, Vec3 forward, Vec3 up, float eyeSide, Vec3 travel )
	{
		Head = head;
		HeadRotation = SboxConvert.ToCore( Rotation.LookAt( SboxConvert.ToEngine( Vec3.Normalize( forward ) ), SboxConvert.ToEngine( Vec3.Normalize( up ) ) ) );
		Travel = Vec3.Normalize( travel );
		EyeSide = eyeSide;
		Running = true;
		_wobbleSpeed = 0f;
		Apply();
		return State();
	}

	/// <summary>
	/// Start in front of an end: the head <paramref name="distance"/> metres out from the middle of the opening
	/// (moved <paramref name="aside"/> metres along the wall first), travelling into the wall at
	/// <paramref name="approachDegrees"/> from straight on, the head turned a further
	/// <paramref name="headTurnDegrees"/> from its direction of travel (both about the wall's own "up"; positive =
	/// toward the wall's right as you face it) and tilted <paramref name="rollDegrees"/>.
	/// </summary>
	public string BeginAt( PaintColor end, float distance, float approachDegrees, float headTurnDegrees, float rollDegrees, float eyeSide, float aside = 0f )
	{
		PortalLink portals = Room.Portals;
		portals.Sync();
		if ( !portals.HasPair ) return "no linked pair";
		PortalEnd e = portals.EndOf( end );
		WallFrame wall = e.Wall;
		float a = approachDegrees * ( MathF.PI / 180f );
		float f = ( approachDegrees + headTurnDegrees ) * ( MathF.PI / 180f );
		Vec3 travel = -wall.Normal * MathF.Cos( a ) + wall.U * MathF.Sin( a );
		Vec3 forward = -wall.Normal * MathF.Cos( f ) + wall.U * MathF.Sin( f );
		Vec3 up = wall.V;
		if ( MathF.Abs( rollDegrees ) > 0.001f )
		{
			Quat roll = Quat.CreateFromAxisAngle( Vec3.Normalize( forward ), rollDegrees * ( MathF.PI / 180f ) );
			up = Vec3.Transform( up, roll );
		}
		// Start so that the head's CENTRE meets the wall at the middle of the opening (plus 'aside').
		Vec3 target = wall.ToWorld( e.Opening.CenterUV ) + wall.U * aside;
		Vec3 head = target - travel * ( distance / MathF.Max( 0.2f, MathF.Cos( a ) ) );
		return Begin( head, forward, up, eyeSide, travel );
	}

	/// <summary>Stop: the game goes back to treating the camera as the head.</summary>
	public string End()
	{
		Running = false;
		_wobbleSpeed = 0f;
		if ( Scene.Camera.IsValid() ) Scene.Camera.CustomProjectionMatrix = null;
		if ( Room.IsValid() && Room.View.IsValid() ) Room.View.ClearTestHead();
		return "eye rig off";
	}

	/// <summary>Tell the game where the head is and stand the camera at the eye.</summary>
	public void Apply()
	{
		if ( !Running || !Room.IsValid() || !Desk.IsValid() ) return;
		Room.View.SetTestHead( Head, HeadRotation );
		StandCamera( Eye, HeadRotation );
	}

	/// <summary>
	/// Stand the camera at a place with a full rotation, roll included. The desk camera's SetView keeps it level
	/// (pitch and yaw only, since the floor-portal work), which turned every rolled or tipped view (the fall case, a
	/// floor-to-wall crossing) into a picture from the wrong orientation; the camera is turned after it.
	/// </summary>
	private void StandCamera( Vec3 position, Quat rotation )
	{
		Desk.SetView( position, SboxConvert.ToEngine( rotation ).Angles() );
		CameraComponent camera = Scene.Camera;
		if ( !camera.IsValid() ) return;
		camera.WorldPosition = SboxConvert.ToInches( position );
		camera.WorldRotation = SboxConvert.ToEngine( rotation );
	}

	/// <summary>
	/// Move the head this far along its direction of travel. When its centre passes through a linked patch it is
	/// carried across (position, facing and direction of travel), exactly as VrRig carries the play space.
	/// Returns true when it was carried.
	/// </summary>
	public bool Advance( float metres )
	{
		if ( !Running ) return false;
		Vec3 previous = Head;
		Vec3 next = Head + Travel * metres;
		bool crossed = false;
		PortalLink portals = Room.Portals;
		portals.Sync();
		if ( portals.HasPair )
		{
			PortalPair pair = portals.Pair;
			if ( PlayerPassage.TryCross( pair, portals.MaskOf( PaintColor.Orange ), portals.MaskOf( PaintColor.Blue ), previous, next, HeadRadius, portals.Settings, out PlayerStep step ) )
			{
				crossed = true;
				Crossings++;
				PortalLink.CrossingRecord record = new PortalLink.CrossingRecord();
				record.IsPlayer = true;
				record.FromOrange = step.FromA;
				record.EntryUV = step.EntryUV;
				record.PositionBefore = next;
				record.PositionAfter = step.Head;
				record.PositionPredicted = pair.PlayerCrossing( step.FromA ).TransformPoint( next );
				record.VelocityBefore = Travel;
				record.VelocityAfter = step.Crossing.TransformDirection( Travel );
				record.VelocityPredicted = record.VelocityAfter;
				record.ExitMovedBy = step.ExitMovedBy;
				portals.Record( record );
				next = step.Head;
				HeadRotation = step.Crossing.TransformRotation( HeadRotation );
				Travel = Vec3.Normalize( step.Crossing.TransformDirection( Travel ) );
			}
		}
		Head = next;
		Apply();
		return crossed;
	}

	// ------------------------------------------------------------------ moving in real time (the wobble)

	private float _wobbleSpeed;
	private float _wobbleSpan;
	private float _wobbleGone;

	/// <summary>
	/// Walk to and fro in real time, one step every real frame: <paramref name="span"/> metres forward at
	/// <paramref name="speed"/> m/s, then the same back, for ever (0 speed = stop). For pictures of the REAL
	/// window while the head is moving through the opening (engine\seethrough_eye.ps1 -wobble).
	/// </summary>
	public void Wobble( float speed, float span )
	{
		_wobbleSpeed = speed;
		_wobbleSpan = MathF.Max( 0.01f, span );
		_wobbleGone = 0f;
	}

	protected override void OnUpdate()
	{
		if ( !Running || _wobbleSpeed <= 0f ) return;
		float step = _wobbleSpeed * MathF.Min( RealTime.Delta, 0.05f );
		if ( _wobbleGone + step > _wobbleSpan )
		{
			// Turn round: walk backwards the way it came (the head keeps facing as it was).
			Travel = -Travel;
			_wobbleGone = 0f;
		}
		_wobbleGone += step;
		Advance( step );
	}

	// ------------------------------------------------------------------ the seam at the surface

	private GameObject _bar, _barTwin;

	/// <summary>
	/// A thing pushed through an opening, seen from the side: a yellow bar 5 x 5 cm pushed straight into the middle
	/// of <paramref name="end"/>, 30 cm each side of the wall, with a twin at the far end placed by the pair's rigid
	/// crossing (as the game's hand and ball twins are). The desk camera looks at where the bar meets the wall from
	/// <paramref name="distance"/> metres, swung <paramref name="sideDegrees"/> round. Then the bar's middle line is
	/// followed across the surface on the picture, a millimetre at a time: every sample should be the bar (its near
	/// half in the room, its far half seen through the opening). Returns how many millimetres were not, and where.
	/// </summary>
	public string SeamCheck( PaintColor end, float sideDegrees, float distance, int width, int height )
	{
		PortalLink portals = Room.Portals;
		portals.Sync();
		if ( !portals.HasPair ) return "no linked pair";
		PortalPair pair = portals.Pair;
		bool fromA = end == PaintColor.Orange;
		PortalEnd e = portals.EndOf( end );
		WallFrame wall = e.Wall;
		Vec3 centre = wall.ToWorld( e.Opening.CenterUV );
		RigidTransform crossing = pair.Crossing( fromA );

		RemoveBar();
		Quat along = SboxConvert.ToCore( Rotation.LookAt( SboxConvert.ToEngine( wall.Normal ), SboxConvert.ToEngine( wall.V ) ) );
		_bar = MakeBar( "Seam bar", centre, along );
		_barTwin = MakeBar( "Seam bar twin", crossing.TransformPoint( centre ), crossing.TransformRotation( along ) );

		// The camera: in front of the meeting point, swung round the wall's up direction.
		float side = sideDegrees * ( MathF.PI / 180f );
		Vec3 eye = centre + ( wall.Normal * MathF.Cos( side ) + wall.U * MathF.Sin( side ) ) * distance + wall.V * 0.03f;
		Desk.LookAt( eye, centre );
		if ( Running ) End();
		Scene.Camera.CustomProjectionMatrix = null;

		PortalView view = Room.View;
		view.AspectOverride = width / (float)height;
		view.Rebuild();
		Bitmap shot = new Bitmap( width, height );
		Scene.Camera.RenderToBitmap( shot, false );
		view.AspectOverride = 0f;
		LastSeamShot = shot;   // the script saves it (game code may not write files)

		// Follow the bar's middle line (a little toward the camera, on the face it sees) from 4 cm in front of the
		// wall to 4 cm behind it.
		SboxConvert.ToCorePose( Scene.Camera.WorldTransform, out Vec3 camPos, out Quat camRot );
		LensTangents lens = LensTangents.Symmetric( Scene.Camera.FieldOfView, width / (float)height );
		Vec3 toCamera = Vec3.Normalize( camPos - centre );
		Vec3 faceOffset = ( toCamera - wall.Normal * Vec3.Dot( toCamera, wall.Normal ) );
		if ( faceOffset.LengthSquared() > 1e-6f ) faceOffset = Vec3.Normalize( faceOffset ) * 0.015f;
		int samples = 0, missing = 0, longest = 0, run = 0;
		float firstMissing = float.NaN, lastMissing = float.NaN;
		for ( int mm = 40; mm >= -40; mm-- )
		{
			Vec3 p = centre + wall.Normal * ( mm * 0.001f ) + faceOffset;
			Vec3 v = CameraConventions.WorldToView( p, camPos, camRot );
			if ( !( v.Z < -0.01f ) ) continue;
			float px = ( v.X / -v.Z - lens.Left ) / ( lens.Right - lens.Left ) * width;
			float py = ( lens.Up - v.Y / -v.Z ) / ( lens.Up - lens.Down ) * height;
			int x = (int)px, y = (int)py;
			if ( x < 0 || y < 0 || x >= width || y >= height ) continue;
			Color c = shot.GetPixel( x, y );
			bool yellow = c.r > 0.25f && c.g > 0.2f && c.b < 0.6f * MathF.Min( c.r, c.g );
			samples++;
			if ( !yellow )
			{
				missing++;
				run++;
				if ( float.IsNaN( firstMissing ) ) firstMissing = mm;
				lastMissing = mm;
				if ( run > longest ) longest = run;
			}
			else run = 0;
		}
		return $"seam at {end}, {sideDegrees:0} deg to the side from {distance:0.00} m, cut {view.ClipFor( (int)end ) * 1000f:0.0} mm from the exit wall: {missing} of {samples} one-millimetre samples along the bar are NOT the bar (longest gap {longest} mm{( missing > 0 ? $", from {firstMissing:0} to {lastMissing:0} mm out of the wall" : "" )})";
	}

	/// <summary>The picture the latest <see cref="SeamCheck"/> looked at.</summary>
	public Bitmap LastSeamShot { get; private set; }

	public void RemoveBar()
	{
		if ( _bar.IsValid() ) _bar.Destroy();
		if ( _barTwin.IsValid() ) _barTwin.Destroy();
		_bar = _barTwin = null;
	}

	private GameObject MakeBar( string name, Vec3 centre, Quat rotation )
	{
		GameObject go = new GameObject( Room.GameObject, false, name );
		go.WorldPosition = SboxConvert.ToInches( centre );
		go.WorldRotation = SboxConvert.ToEngine( rotation );
		go.LocalScale = SboxConvert.BoxScaleFor( new Vec3( 0.60f, 0.05f, 0.05f ) );
		ModelRenderer r = go.AddComponent<ModelRenderer>();
		r.Model = Model.Cube;
		r.Tint = new Color( 1f, 0.85f, 0.05f );
		go.Enabled = true;
		return go;
	}

	// ------------------------------------------------------------------ alarm colours

	private readonly List<ModelRenderer> _tinted = new List<ModelRenderer>();
	private readonly List<Color> _tintWas = new List<Color>();
	private Color _backgroundWas;
	private bool _alarm;

	/// <summary>
	/// Paint the two walls that hold the portal's ends bright magenta and the void outside the room bright green,
	/// so that in a picture of the real window any wall or void showing where the far side should be is
	/// unmistakable (and countable). Off puts the colours back.
	/// </summary>
	public string AlarmColours( bool on )
	{
		CameraComponent camera = Scene.Camera;
		if ( on == _alarm ) return on ? "alarm colours already on" : "alarm colours already off";
		_alarm = on;
		if ( on )
		{
			PortalLink portals = Room.Portals;
			_backgroundWas = camera.BackgroundColor;
			camera.BackgroundColor = new Color( 0f, 1f, 0f );
			for ( int c = 0; c < 2; c++ )
			{
				PaintWall wall = portals.WallOf( (PaintColor)c );
				if ( !wall.IsValid() ) continue;
				ModelRenderer r = wall.GameObject.GetComponent<ModelRenderer>();
				if ( !r.IsValid() || _tinted.Contains( r ) ) continue;
				_tinted.Add( r );
				_tintWas.Add( r.Tint );
				r.Tint = new Color( 1f, 0f, 1f );
			}
			return $"alarm colours on: {_tinted.Count} wall(s) magenta, void green";
		}
		for ( int i = 0; i < _tinted.Count; i++ )
		{
			if ( _tinted[i].IsValid() ) _tinted[i].Tint = _tintWas[i];
		}
		_tinted.Clear();
		_tintWas.Clear();
		camera.BackgroundColor = _backgroundWas;
		return "alarm colours off";
	}

	// ------------------------------------------------------------------ what the rig is doing

	/// <summary>The nearest open end to the eye (by distance to its wall, the eye being over or near its patch), or -1.</summary>
	private int NearestEnd( PortalLink portals, Vec3 eye, out float eyeOut )
	{
		int best = -1;
		float bestScore = float.MaxValue;
		eyeOut = 0f;
		for ( int c = 0; c < 2; c++ )
		{
			PortalEnd end = portals.EndOf( (PaintColor)c );
			float d = end.Wall.SignedDistance( eye );
			if ( d > 1.5f || d < -0.3f ) continue;
			Vec2 uv = end.Wall.ToUV( eye );
			float aside = Vec2.Distance( uv, end.Opening.CenterUV );
			float score = MathF.Abs( d ) + aside;
			if ( score < bestScore ) { bestScore = score; best = c; eyeOut = d; }
		}
		return best;
	}

	/// <summary>One line: where the head and the eye are against the nearest end's wall.</summary>
	public string State()
	{
		if ( !Running ) return "eye rig off";
		PortalLink portals = Room.Portals;
		if ( !portals.HasPair ) return "no linked pair";
		Vec3 eye = Eye;
		int c = NearestEnd( portals, eye, out float eyeOut );
		if ( c < 0 ) return $"head ({Head.X:0.000}, {Head.Y:0.000}, {Head.Z:0.000}) m, not near an end; crossings {Crossings}";
		PortalEnd end = portals.EndOf( (PaintColor)c );
		float headOut = end.Wall.SignedDistance( Head );
		PortalView view = Room.View;
		return $"at {(PaintColor)c} on {portals.WallOf( (PaintColor)c ).WallName}: head {headOut * 1000f:0.0} mm, eye {eyeOut * 1000f:0.0} mm in front of the wall (minus = past it); crossings {Crossings}; guard {view.GuardDistanceNow * 1000f:0} mm, sheet {( view.ArmedWall.IsValid() ? "armed on " + view.ArmedWall.WallName : "off" )}";
	}

	// ------------------------------------------------------------------ the check

	/// <summary>What <see cref="Check"/> found for one picture.</summary>
	public struct CheckResult
	{
		/// <summary>Pixels that had to show the far side / the room the eye is in, and were compared.</summary>
		public int FarChecked, NearChecked;
		/// <summary>Of those, the ones that differ from the reference by more than the tolerance.</summary>
		public int FarBad, NearBad;
		/// <summary>The largest difference in any channel, 0..255, and the mean over the compared pixels.</summary>
		public int Worst;
		public float Mean;
		/// <summary>The eye and the head against the nearest end's wall, millimetres in front (minus = past it).</summary>
		public float EyeOutMm, HeadOutMm;
		public int End;
		public string Note;

		public int Bad => FarBad + NearBad;
		public int Checked => FarChecked + NearChecked;

		public override string ToString()
		{
			return $"end {( End < 0 ? "none" : ( (PaintColor)End ).ToString() )} head {HeadOutMm:0.0} mm eye {EyeOutMm:0.0} mm | checked {Checked} (far side {FarChecked}, this side {NearChecked}) | BAD {Bad} (far {FarBad}, this side {NearBad}) | worst {Worst} mean {Mean:0.00}{( string.IsNullOrEmpty( Note ) ? "" : " | " + Note )}";
		}
	}

	/// <summary>The reference pictures the latest <see cref="Check"/> used, and its map of what was compared (for saving).</summary>
	public Bitmap LastFar { get; private set; }
	public Bitmap LastNear { get; private set; }
	public Bitmap LastMap { get; private set; }

	/// <summary>The eye's picture right now (the portal view brought up to date for it first).</summary>
	public Bitmap Shoot( int width, int height )
	{
		Apply();
		SetProjection( width, height );
		PortalView view = Room.View;
		view.AspectOverride = width / (float)height;
		view.Rebuild();
		Bitmap b = new Bitmap( width, height );
		Scene.Camera.RenderToBitmap( b, false );
		view.AspectOverride = 0f;
		return b;
	}

	private Bitmap Reference( Vec3 position, Quat rotation, int width, int height, int leaveOutWall )
	{
		CameraComponent camera = Scene.Camera;
		string tag = leaveOutWall >= 0 ? WallTagPrefix + leaveOutWall : null;
		StandCamera( position, rotation );
		if ( tag != null ) camera.RenderExcludeTags.Add( tag );
		camera.RenderExcludeTags.Add( PortalView.GuardTag );
		Bitmap b = new Bitmap( width, height );
		camera.RenderToBitmap( b, false );
		if ( tag != null ) camera.RenderExcludeTags.Remove( tag );
		camera.RenderExcludeTags.Remove( PortalView.GuardTag );
		return b;
	}

	/// <summary>
	/// Compare the eye's picture with what it must show (see the class comment). Only lines of sight whose
	/// answer is certain are compared: through the middle of the opening (within <paramref name="share"/> of its
	/// largest circle) for an eye in front; everything, onward and back, for an eye standing in the opening.
	/// </summary>
	/// <param name="shot">The eye's picture, from <see cref="Shoot"/>.</param>
	/// <param name="tolerance">A pixel is bad when any channel differs by more than this (0..255).</param>
	public CheckResult Check( Bitmap shot, int tolerance = 12, float share = 0.55f )
	{
		CheckResult result = new CheckResult { End = -1, Note = "" };
		LastFar = null;
		LastNear = null;
		LastMap = null;
		PortalLink portals = Room.Portals;
		if ( !Running || !portals.HasPair ) { result.Note = "rig off or no pair"; return result; }

		int width = shot.Width, height = shot.Height;
		Vec3 eye = Eye;
		Quat rotation = HeadRotation;
		int c = NearestEnd( portals, eye, out float eyeOut );
		result.End = c;
		if ( c < 0 ) { result.Note = "not near an end"; return result; }

		PortalPair pair = portals.Pair;
		bool throughA = c == 0;
		PortalEnd end = portals.EndOf( (PaintColor)c );
		WallFrame wall = end.Wall;
		PaintWall entryWall = portals.WallOf( (PaintColor)c );
		PaintWall exitWall = portals.WallOf( (PaintColor)( 1 - c ) );
		result.EyeOutMm = eyeOut * 1000f;
		result.HeadOutMm = wall.SignedDistance( Head ) * 1000f;

		Vec2 centre = end.Opening.CenterUV;
		float safe = end.Opening.InscribedRadius * share;
		bool past = eyeOut <= 0f;
		bool eyeInOpening = Vec2.Distance( wall.ToUV( eye ), centre ) <= end.Opening.InscribedRadius * 0.9f;
		if ( past && !eyeInOpening ) { result.Note = "eye past the wall but not in the opening"; return result; }

		// The references: ordinary cameras, the see-through code not involved.
		RigidTransform crossing = pair.PlayerCrossing( throughA );
		PortalCamera.VirtualEye( crossing, eye, rotation, out Vec3 farEye, out Quat farRotation );
		Bitmap far = Reference( farEye, farRotation, width, height, IndexOf( exitWall ) );
		Bitmap near = past ? Reference( eye, rotation, width, height, IndexOf( entryWall ) ) : null;
		Apply();   // the camera back at the eye
		LastFar = far;
		LastNear = near;

		Color32[] a = shot.GetPixels32();
		Color32[] f = far.GetPixels32();
		Color32[] n = near != null ? near.GetPixels32() : null;
		Color32[] map = new Color32[a.Length];

		CameraComponent camera = Scene.Camera;
		float tanX = MathF.Tan( camera.FieldOfView * ( MathF.PI / 360f ) );
		float tanY = tanX * height / width;
		Vec3 forward = Vec3.Transform( new Vec3( 1f, 0f, 0f ), rotation );
		Vec3 right = -Vec3.Transform( new Vec3( 0f, 1f, 0f ), rotation );
		Vec3 up = Vec3.Transform( new Vec3( 0f, 0f, 1f ), rotation );
		Vec3 normal = wall.Normal;
		float fN = Vec3.Dot( forward, normal ), rN = Vec3.Dot( right, normal ), uN = Vec3.Dot( up, normal );

		long sum = 0;
		int worst = 0;
		for ( int y = 0; y < height; y++ )
		{
			float ty = ( 1f - 2f * ( y + 0.5f ) / height ) * tanY;
			for ( int x = 0; x < width; x++ )
			{
				float tx = ( 2f * ( x + 0.5f ) / width - 1f ) * tanX;
				int i = y * width + x;
				// How fast this line of sight closes on the wall (per unit of forward travel), normalised.
				float along = fN + tx * rN + ty * uN;
				float length = MathF.Sqrt( 1f + tx * tx + ty * ty );
				float into = -along / length;

				int want = 0;   // 0 = not compared, 1 = far side, 2 = this side
				if ( !past )
				{
					if ( into > 0.02f )
					{
						// Where the line meets the wall.
						float t = eyeOut / -along;
						Vec3 hit = eye + ( forward + right * tx + up * ty ) * t;
						if ( Vec2.Distance( wall.ToUV( hit ), centre ) <= safe ) want = 1;
					}
				}
				else
				{
					if ( into > 0.03f ) want = 1;
					else if ( into < -0.03f ) want = 2;
				}
				if ( want == 0 )
				{
					map[i] = new Color32( (byte)( a[i].r / 3 ), (byte)( a[i].g / 3 ), (byte)( a[i].b / 3 ), 255 );
					continue;
				}

				Color32 reference = want == 1 ? f[i] : n[i];
				int d = Math.Max( Math.Abs( a[i].r - reference.r ), Math.Max( Math.Abs( a[i].g - reference.g ), Math.Abs( a[i].b - reference.b ) ) );
				sum += d;
				if ( d > worst ) worst = d;
				bool bad = d > tolerance;
				if ( want == 1 ) { result.FarChecked++; if ( bad ) result.FarBad++; }
				else { result.NearChecked++; if ( bad ) result.NearBad++; }
				// The map: what was compared in its own colours, bad pixels red.
				map[i] = bad ? new Color32( 255, 0, 0, 255 ) : reference;
			}
		}
		result.Worst = worst;
		result.Mean = result.Checked > 0 ? sum / (float)result.Checked : 0f;

		Bitmap mapBitmap = new Bitmap( width, height );
		Color[] colours = new Color[map.Length];
		for ( int i = 0; i < map.Length; i++ ) colours[i] = map[i].ToColor();
		mapBitmap.SetPixels( colours );
		LastMap = mapBitmap;
		return result;
	}
}
