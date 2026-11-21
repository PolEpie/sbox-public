using Sandbox.Navigation;

namespace NavigationTests;

/// <summary>
/// Tile builders own tens of MiB each and a load runs one per core: they must not outlive the navmesh that used them.
/// </summary>
[TestClass]
[DoNotParallelize]
public class NavMeshBuilderPool
{
	[TestMethod]
	public async Task BuildersGoWhenTheNavmeshIsDisposed()
	{
		var scene = new Scene();
		using var scope = scene.Push();
		scene.Floor( Vector3.Zero, 1400 );
		await scene.NavMesh.Generate( scene.PhysicsWorld );
		Assert.IsTrue( NavMesh.PooledBuilders > 0, "The builds left their builders for the next" );

		scene.NavMesh.Dispose();
		Assert.AreEqual( 0, NavMesh.PooledBuilders );
	}
}
