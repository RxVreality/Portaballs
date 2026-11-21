namespace Portaballs;

/// <summary>
/// Proves the engine is running our own code. Each time a scene starts playing it writes one line
/// to the console that begins PORTABALLS_SMOKE, which the smoke test script looks for.
/// It does nothing else and can stay in the project.
/// </summary>
public sealed class SmokeTestSystem : GameObjectSystem
{
	public SmokeTestSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.SceneLoaded, 0, OnSceneLoaded, "PortaballsSmokeTest" );
	}

	void OnSceneLoaded()
	{
		// The editor also builds a scene for editing; only speak up for a scene that is being played.
		if ( Scene.IsEditor ) return;

		Log.Info( $"PORTABALLS_SMOKE our code is running. scene={Scene.Name} vr={Game.IsRunningInVR}" );
	}
}
