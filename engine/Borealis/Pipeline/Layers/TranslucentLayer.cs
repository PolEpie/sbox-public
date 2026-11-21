namespace Borealis;

/// <summary>
/// Back-to-front alpha-tinted geometry (<c>Translucent Forward</c>), on the main thread when it reads the frame buffer.
/// The main world enables fading; the 3D skybox does not. Materials control blending and depth writes.
/// </summary>
internal sealed class TranslucentLayer : MeshLayer
{
	static readonly StringToken ForwardMode = new( "Forward" );
	static readonly StringToken EnableAlphaTint = new( "EnableAlphaTint" );

	/// <summary>
	/// Enable <c>D_OPAQUE_FADE</c>, matching native's main translucent layer.
	/// </summary>
	public bool Fade { get; }

	public TranslucentLayer( string name, bool fade ) : base( name )
	{
		ShaderMode = ForwardMode;
		Runs = MeshRuns.Translucent;
		LayerType = MeshLayerType.Translucent;
		Fade = fade;
		TakesToolsPass = true;
	}

	// Frame buffer readers share one copy of the frame, made at the layer's first reader: one context records them all
	public override bool IsSharedIn( RenderFrame frame ) => !frame.ReadsFrameBuffer( Runs & MeshRenderFeature.FrameBufferCopyingLists );

	/// <summary>
	/// Bind the frame with alpha tint and optional fading.
	/// </summary>
	public override void Begin( RenderFrame frame, GpuContext rc, ViewPass view, bool start )
	{
		frame.BindFrame( rc );
		if ( Fade ) rc.SetPassCombo( MeshRenderFeature.OpaqueFade, 1 );
		rc.SetPassAttribute( EnableAlphaTint, 1 );
	}
}
