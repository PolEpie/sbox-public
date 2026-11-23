using Borealis;
using Borealis.Gpu;
using BorealisTests.Headless;
using System.Linq;
using static BorealisTests.TestScenes;

namespace BorealisTests;

/// <summary>
/// The translucent layer's frame copy: made at the layer's first frame buffer reader, as native's
/// <c>CRenderBatchList::DrawCurrentPrimitives</c> makes it, however many record workers the layer is split across.
/// </summary>
[TestClass]
[DoNotParallelize]
public class FrameBufferCopyTests
{
	static HeadlessDevice Device => HeadlessRenderHost.Headless;

	static RenderMesh Glass( HeadlessMaterial material ) => new( new HeadlessMesh( "glass" ), UnitBox, [[new RenderMesh.Draw( 0, 0, 0, 36, 24, 0, material, Translucent: true )]] );

	[TestMethod]
	public void SplitTranslucentLayerCopiesTheFrameOnce()
	{
		var world = new RenderWorld();
		using var material = new HeadlessMaterial( "glass", "complex" );
		RenderMesh[] glass = [Glass( material ), Glass( material )];
		for ( int i = 0; i < 200; i++ )
			world.Add( new MeshObject( glass[i % 2], new Transform( new Vector3( 20 + i, (i % 9) - 4, 0 ) ) ) { WantsFrameBufferCopy = true } );

		using var target = ViewTarget.Create( new Vector2Int( 640, 480 ) );
		using var system = new RenderSystem { ParallelRecording = true };

		using var frame = Device.Capture();
		RenderHost.Current.NextFrame();
		system.Render( world, LookingDownX(), target );

		var translucentLists = frame.Commands.Count( c => c.Op == GpuOp.Bind && c.Scope.Contains( "Translucent Forward" ) );
		if ( System.Environment.ProcessorCount > 2 )
			Assert.IsTrue( translucentLists > 1, $"the layer splits across record workers ({translucentLists} parts)" );

		Assert.AreEqual( 1, frame.Commands.Count( c => c.Op == GpuOp.CopyFrameBuffer ), "one copy, at the layer's first reader" );
	}
}
