using System.Collections.Generic;
using Sandbox;
using Portaballs.Core;

namespace Portaballs;

/// <summary>
/// The droplets that fly off when a paint ball bursts on a wall. The tester, 2026-11-19: "some splats coming off, bouncing
/// off the wall, most of it sticking". Every number (how many, how fast, how big, how long) is in core SplatFeel.
///
/// One per scene, on its own object "Paint droplets", made while the room loads (<see cref="Ensure"/>, called by
/// PaintWall.Setup). A bursting ball calls <see cref="Burst"/>. The flying is core PaintSpray: plain free flight
/// under gravity against the room's walls, floor and ceiling. Droplets are not physics bodies, have no collider, and
/// never go through a portal. Each frame:
///   - a droplet that reaches a wall leaves a small splat there through PaintWall.SplatDroplet, which only paints
///     bare wall, so a droplet can never start, move, join or cut a portal patch (otherwise it soaks in). A droplet
///     splat costs about 0.2 ms, so at most SplatFeel.DropletSplatsPerFrame are laid a frame; the rest wait a frame;
///   - "wall" means every paint surface the room has registered (PortalPatches.AddWall): once the floor or the
///     ceiling is one of them, a droplet lands on it like on any wall. Where there is no paint surface, one that
///     reaches the floor or the ceiling just disappears;
///   - none lives longer than SplatFeel.DropletLifeSeconds, and at most SplatFeel.MaxDroplets fly at once.
/// Two more things use the same droplets (the tester, 2026-11-19 evening): a drop that falls off a ceiling splat
/// (<see cref="Drop"/>, sent by PaintWall), and a ball that bursts in a wet puddle on a floor throws a few more
/// droplets (SplatFeel.WetSplashExtraDroplets).
/// Each droplet is drawn as a small ball of its colour, stretched a little along its path. The objects are made once
/// and reused, so a burst creates nothing.
/// </summary>
public sealed class PaintDroplets : Component
{
	/// <summary>Off = balls burst with no droplets, as before 2026-11-19.</summary>
	public static bool On { get; set; } = true;

	/// <summary>Write one line per burst with how many droplets it threw, and one per droplet that reaches a wall.</summary>
	public static bool LogDroplets { get; set; } = false;

	/// <summary>The core flight of every droplet in the air.</summary>
	public PaintSpray Spray { get; private set; }

	/// <summary>Since the scene started: droplets thrown, that left a splat on a wall, that soaked in on a wall.</summary>
	public int Thrown { get; private set; }
	public int Painted { get; private set; }
	public int SoakedIn { get; private set; }

	/// <summary>Milliseconds the droplets took in the latest frame (flight, drawing and their splats).</summary>
	public double LastFrameMs { get; private set; }

	private GameObject[] _objects;
	private ModelRenderer[] _renderers;
	private bool[] _shown;
	private int[] _shownColour;
	private PaintWall[] _walls = new PaintWall[0];
	private int _roomWallCount = -1;

	private static PaintDroplets _last;

	/// <summary>The scene's droplets, made (switched on, with its drawing objects) if there are none yet.</summary>
	public static PaintDroplets Ensure( Scene scene )
	{
		if ( scene == null ) return null;
		if ( _last.IsValid() && _last.Scene == scene ) return _last;
		foreach ( PaintDroplets d in scene.GetAllComponents<PaintDroplets>() )
		{
			_last = d;
			d.Build();
			return d;
		}
		GameObject go = new GameObject( scene, false, "Paint droplets" );
		PaintDroplets made = go.AddComponent<PaintDroplets>();
		made.Build();
		go.Enabled = true;
		_last = made;
		return made;
	}

	/// <summary>
	/// A ball burst on a wall: throw its droplets (none when <see cref="On"/> is off or the ball was slow).
	/// All in core units: metres and m/s.
	/// </summary>
	public static void Burst( Scene scene, PaintWall wall, PaintColor color, Vec3 hitPoint, Vec3 ballVelocity, float splatRadius, uint seed )
	{
		if ( !On || !wall.IsValid() ) return;
		PaintDroplets d = Ensure( scene );
		if ( d.IsValid() ) d.Throw( wall, color, hitPoint, ballVelocity, splatRadius, seed );
	}

	/// <summary>
	/// One drop lets go in mid-air: a drop falling off a ceiling splat (core PaintRunOff.Fell). It falls and lands
	/// like any droplet: a small dab on bare surface only, never on or near a portal patch.
	/// </summary>
	/// <param name="position">Where its centre starts, metres (just clear of the surface it left).</param>
	/// <param name="splatRadius">Radius of the dab it leaves, metres (kept within the droplets' own limits).</param>
	public static void Drop( Scene scene, PaintColor color, Vec3 position, float splatRadius, uint seed )
	{
		if ( !On ) return;
		PaintDroplets d = Ensure( scene );
		if ( !d.IsValid() ) return;
		d.Build();
		d.FindRoom();
		d.Spray.Drop( position, new Vec3( 0f, 0f, 0f ), SplatFeel.CeilingDropDrawRadius, splatRadius, color, seed );
		d.Thrown++;
		d.Dropped++;
		if ( LogDroplets ) Log.Info( $"[PaintDroplets] {color} drop let go at ({position.X:0.00}, {position.Y:0.00}, {position.Z:0.00}) m ({d.Spray.AliveCount} in the air)." );
	}

	/// <summary>Drops let go by <see cref="Drop"/> since the scene started, and extra droplets thrown by balls landing in wet paint.</summary>
	public int Dropped { get; private set; }
	public int SplashExtras { get; private set; }

	// Made once: the core spray, the drawing objects, and the first-use pause of the maths spent now.
	private void Build()
	{
		if ( Spray != null ) return;
		long start = System.Diagnostics.Stopwatch.GetTimestamp();
		PaintSpray.WarmUp();
		Spray = new PaintSpray();
		int n = Spray.Capacity;
		_objects = new GameObject[n];
		_renderers = new ModelRenderer[n];
		_shown = new bool[n];
		_shownColour = new int[n];
		for ( int i = 0; i < n; i++ )
		{
			GameObject go = new GameObject( GameObject, false, "Droplet" );
			ModelRenderer r = go.AddComponent<ModelRenderer>();
			r.Model = Model.Sphere;
			PaintLook.Glow( r, PaintColor.Orange );
			r.RenderType = ModelRenderer.ShadowRenderType.Off;
			_objects[i] = go;
			_renderers[i] = r;
			_shownColour[i] = 0;
		}
		double ms = ( System.Diagnostics.Stopwatch.GetTimestamp() - start ) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
		Log.Info( $"[PaintDroplets] Ready: up to {n} droplets in the air, {SplatFeel.DropletsMin} to {SplatFeel.DropletsMax} per burst; setting up took {ms:0.0} ms." );
	}

	private void Throw( PaintWall wall, PaintColor color, Vec3 hitPoint, Vec3 ballVelocity, float splatRadius, uint seed )
	{
		Build();
		FindRoom();
		// A ball landing in a puddle that is still wet splashes: more droplets, the wetter the more. Only on a floor:
		// a burst on a wall stays exactly as the tester approved it.
		int extra = 0;
		if ( SplatFeel.WetSplashExtraDroplets > 0 && wall.Surface != null && wall.Surface.IsFloorLike )
		{
			Vec2 uv = wall.Frame.ToUV( hitPoint );
			PaintSurface s = wall.Surface;
			int x = (int)( uv.X / s.TexelSizeU ), y = (int)( uv.Y / s.TexelSizeV );
			if ( x >= 0 && y >= 0 && x < s.Width && y < s.Height )
				extra = (int)System.MathF.Round( SplatFeel.WetSplashExtraDroplets * s.GetWetness( x, y ) );
		}
		int n = Spray.Burst( wall.Frame, hitPoint, ballVelocity, splatRadius, color, seed, extra );
		Thrown += n;
		if ( n > 0 ) SplashExtras += extra;
		if ( LogDroplets && n > 0 ) Log.Info( $"[PaintDroplets] #{seed} {color}: {n} droplets off {wall.WallName} ({Spray.AliveCount} in the air)." );
	}

	// The walls a droplet can land on, and the floor and ceiling heights: the test room's, or every paint wall in
	// the scene when there is no room. Looked up again if the room's walls change.
	private void FindRoom()
	{
		TestRoom room = null;
		foreach ( TestRoom r in Scene.GetAllComponents<TestRoom>() )
		{
			room = r;
			break;
		}
		if ( room.IsValid() && room.WallCount > 0 )
		{
			// Every paint surface the room has registered: its walls, and its floor and ceiling once they take
			// paint. Where one of those lies, a droplet lands on it; the bare floor and ceiling heights sit a
			// little beyond, so they only catch what falls where there is no paint surface.
			PortalPatches patches = room.Patches;
			int count = patches.IsValid() ? patches.WallCount : room.WallCount;
			if ( count == _roomWallCount && AllWallsValid() ) return;
			_walls = new PaintWall[count];
			WallFrame[] frames = new WallFrame[count];
			for ( int i = 0; i < count; i++ )
			{
				_walls[i] = patches.IsValid() ? patches.WallAt( i ) : room.Walls[i];
				frames[i] = _walls[i].Frame;
			}
			// The floor and the ceiling are paint surfaces in that list now (TestRoom.Walls), so a droplet lands on
			// them like on a wall. The plain floor and ceiling heights are kept a little outside the room, as a
			// backstop only: at the real heights they would tie with the surfaces and make droplets vanish.
			float floor = SboxConvert.ToMetres( room.WorldPosition ).Z;
			Spray.SetRoom( frames, floor - BareFloorMarginMetres, floor + room.Height + BareFloorMarginMetres );
			_roomWallCount = count;
			return;
		}

		// No room: every paint wall, with the floor and ceiling at their lowest and highest edges.
		List<PaintWall> found = new List<PaintWall>();
		foreach ( PaintWall w in Scene.GetAllComponents<PaintWall>() )
		{
			if ( w.Surface != null ) found.Add( w );
		}
		if ( found.Count == _roomWallCount && AllWallsValid() ) return;
		_walls = found.ToArray();
		WallFrame[] f = new WallFrame[_walls.Length];
		float lo = float.MaxValue, hi = float.MinValue;
		for ( int i = 0; i < _walls.Length; i++ )
		{
			f[i] = _walls[i].Frame;
			lo = System.MathF.Min( lo, f[i].Origin.Z );
			hi = System.MathF.Max( hi, f[i].Origin.Z + f[i].Height );
		}
		if ( _walls.Length == 0 ) { lo = -1000f; hi = 1000f; }
		Spray.SetRoom( f, lo, hi );
		_roomWallCount = _walls.Length;
	}

	/// <summary>How far past the room's floor and ceiling a droplet may fall before it is dropped, metres (so a floor that takes paint always gets it first).</summary>
	public const float BareFloorMarginMetres = 0.02f;

	private bool AllWallsValid()
	{
		for ( int i = 0; i < _walls.Length; i++ )
		{
			if ( !_walls[i].IsValid() ) return false;
		}
		return true;
	}

	// Droplets that reached a wall and wait for their splat (SplatFeel.DropletSplatsPerFrame are laid a frame).
	private readonly DropletLanding[] _waiting = new DropletLanding[64];
	private int _waitingHead, _waitingCount;

	/// <summary>Droplets that have reached a wall and whose splat is waiting for its frame.</summary>
	public int WaitingSplats => _waitingCount;

	protected override void OnUpdate()
	{
		if ( Spray == null ) return;
		float dt = Time.Delta;
		LastFrameMs = 0.0;
		if ( !( dt > 0f ) ) return;   // the clock is stopped (a picture stop, a pause): everything holds still
		if ( Spray.AliveCount == 0 && _waitingCount == 0 )
		{
			HideAll();
			return;
		}
		long start = System.Diagnostics.Stopwatch.GetTimestamp();

		if ( Spray.AliveCount > 0 )
		{
			Spray.Step( dt, SboxConvert.ToMetres( Scene.PhysicsWorld.Gravity ) );
			for ( int i = 0; i < Spray.LandingCount; i++ )
			{
				if ( _waitingCount == _waiting.Length )
				{
					// Full (it never should be): the oldest waiting droplet just soaks in.
					_waitingHead = ( _waitingHead + 1 ) % _waiting.Length;
					_waitingCount--;
					SoakedIn++;
				}
				_waiting[( _waitingHead + _waitingCount ) % _waiting.Length] = Spray.GetLanding( i );
				_waitingCount++;
			}
		}
		LandWaiting();
		Draw();
		LastFrameMs = ( System.Diagnostics.Stopwatch.GetTimestamp() - start ) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
	}

	// Splats of droplets that reached a wall, oldest first, at most SplatFeel.DropletSplatsPerFrame of them painted
	// this frame (one that soaks in costs next to nothing and does not count).
	private void LandWaiting()
	{
		int budget = System.Math.Max( 1, SplatFeel.DropletSplatsPerFrame );
		int painted = 0;
		while ( _waitingCount > 0 && painted < budget )
		{
			DropletLanding l = _waiting[_waitingHead];
			_waitingHead = ( _waitingHead + 1 ) % _waiting.Length;
			_waitingCount--;
			if ( l.Wall < 0 || l.Wall >= _walls.Length || !_walls[l.Wall].IsValid() ) continue;
			PaintWall wall = _walls[l.Wall];
			bool ok = wall.SplatDroplet( l.Color, l.UV, l.SplatRadius, l.SplatWallVelocity, l.Seed, l.SplatNormalSpeed );
			if ( ok )
			{
				Painted++;
				painted++;
			}
			else SoakedIn++;
			if ( LogDroplets ) Log.Info( $"[PaintDroplets] {l.Color} droplet hit {wall.WallName} at u={l.UV.X:0.00} v={l.UV.Y:0.00} m, {l.NormalSpeed:0.0} m/s into it: {( ok ? $"small splat, radius {l.SplatRadius:0.000} m" : "soaked in (paint or a patch too near)" )}." );
		}
	}

	// Each slot of the core spray has its own drawing object.
	private void Draw()
	{
		float stretchSeconds = System.MathF.Max( 0f, SplatFeel.DropletStretchSeconds );
		for ( int i = 0; i < _objects.Length; i++ )
		{
			Droplet d = Spray.Get( i );
			GameObject go = _objects[i];
			if ( !d.Alive )
			{
				if ( _shown[i] )
				{
					go.Enabled = false;
					_shown[i] = false;
				}
				continue;
			}

			int colour = (int)d.Color;
			if ( _shownColour[i] != colour )
			{
				PaintLook.Glow( _renderers[i], d.Color );
				_shownColour[i] = colour;
			}

			// A ball of its radius, drawn longer along its path the faster it goes (at most 3 times as long).
			float speed = d.Velocity.Length();
			float length = 1f + System.MathF.Min( 2f, speed * stretchSeconds / System.MathF.Max( 0.001f, 2f * d.DrawRadius ) );
			Vector3 s = SboxConvert.SphereScaleFor( d.DrawRadius );
			go.WorldPosition = SboxConvert.ToInches( d.Position );
			if ( speed > 0.01f ) go.WorldRotation = Rotation.LookAt( SboxConvert.ToEngine( d.Velocity / speed ) );
			go.LocalScale = new Vector3( s.x * length, s.y, s.z );   // the object's own X is the way it is flying

			if ( !_shown[i] )
			{
				go.Enabled = true;
				go.Transform.ClearInterpolation();
				_shown[i] = true;
			}
		}
	}

	/// <summary>The walls are being wiped: drop every droplet in the air and every splat still waiting (PaintWall.ClearPaint).</summary>
	public static void ClearIn( Scene scene )
	{
		if ( !_last.IsValid() || _last.Scene != scene || _last.Spray == null ) return;
		_last.Spray.Clear();
		_last._waitingCount = 0;
		_last._waitingHead = 0;
		_last.HideAll();
	}

	private void HideAll()
	{
		if ( _objects == null ) return;
		for ( int i = 0; i < _objects.Length; i++ )
		{
			if ( !_shown[i] ) continue;
			_objects[i].Enabled = false;
			_shown[i] = false;
		}
	}

	protected override void OnDestroy()
	{
		if ( _last == this ) _last = null;
	}
}
