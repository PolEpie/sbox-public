using NativeEngine;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sandbox;

public class SceneDynamicObject : SceneObject
{
	/// <summary>The vertices, which Borealis draws.</summary>
	internal List<Vertex> ManagedVertices { get; }

	/// <summary>What the vertices are, as last begun.</summary>
	internal Graphics.PrimitiveType ManagedPrimitiveType { get; private set; }

	/// <summary>The material set last, for a backend other than Source 2's.</summary>
	internal Material ManagedMaterial { get; private set; }

	public SceneDynamicObject( SceneWorld sceneWorld )
	{
		Assert.IsValid( sceneWorld );

		ManagedVertices = new List<Vertex>();

		MakeManaged( sceneWorld, null, SceneCustomObject.ManagedFlags, batchable: false ); // as native's CDynamicSceneObject::Create made it

		// Start off with infinite bounds so it will actually get rendered
		// if the user forgets/doesn't need bounds.
		KeepFixedBounds( infinite: true );
	}

	public Material Material
	{
		set => ManagedMaterial = value;
	}

	public void Clear()
	{
		WritesNativeObject( "Reset" );
		ManagedVertices.Clear();
	}

	[MethodImpl( MethodImplOptions.AggressiveInlining )]
	public void AddVertex( in Vertex v )
	{
		WritesNativeObject( "AddVertex" );
		ManagedVertices.Add( v );
	}

	public unsafe void AddVertex( in Span<Vertex> v )
	{
		fixed ( Vertex* ptr = &MemoryMarshal.GetReference( v ) )
		{
			AddVertex( ptr, v.Length );
		}
	}

	internal unsafe void AddVertex( in Vertex* ptr, int count )
	{
		WritesNativeObject( "AddVertexRange" );
		ManagedVertices.AddRange( new ReadOnlySpan<Vertex>( ptr, count ) );
	}

	public IDisposable Write( Graphics.PrimitiveType type, int vertices, int indices )
	{
		Begin( type, vertices );
		return null;
	}

	public void Init( Graphics.PrimitiveType type ) => Begin( type, 0 );

	// Native's Begin drops the vertices and sets the primitive type
	void Begin( Graphics.PrimitiveType type, int vertices )
	{
		ThrowIfUndefined( type );
		WritesNativeObject( "Begin" );
		ManagedVertices.Clear();
		ManagedPrimitiveType = type;
	}

	internal override bool OwnsRenderObject => true;

	internal override Borealis.RenderObject MakeRenderObject( Rendering.ISceneWorldRender render ) => new Rendering.BridgeDynamicObject( this );

	internal override void WriteRender( Borealis.RenderObject obj, Rendering.SceneObjectChange change, Rendering.ISceneWorldRender render )
	{
		base.WriteRender( obj, change, render );
		if ( obj is not Borealis.CustomObject custom ) return;

		// Its material is known before it draws (gizmos, the debug overlay): whether it needs a stencil, so the view's depth has
		// one before the frame records. Other custom objects pick their materials as they draw
		if ( RenderBackend.Default is BorealisGpuRenderBackend gpu ) custom.NeedsStencil = gpu.NeedsStencil( ManagedMaterial );
		WriteCustom( custom, change );
	}

	internal static void ThrowIfUndefined( Graphics.PrimitiveType type )
	{
		if ( !Enum.IsDefined( type ) )
			throw new ArgumentOutOfRangeException( nameof( type ), $"Unknown primitive type {(int)type}" );
	}
}

