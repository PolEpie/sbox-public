using NativeEngine;

namespace Sandbox;

/// <summary>
/// Base class for light scene objects for use with a <see cref="SceneWorld"/>.
/// </summary>
[Expose]
public partial class SceneLight : SceneObject
{
	internal CSceneLightObject lightNative;

	/// <summary>
	/// Changes when the light moves or its range changes, invalidating cached spatial queries.
	/// </summary>
	internal int BoundsVersion { get; private set; }

	/// <summary>
	/// Updates the scene-owned modulation candidates and the managed renderer's light mirror.
	/// </summary>
	internal void SetModulatorRange( uint offset, uint count )
	{
		ref var d = ref Change();
		d.LightModulatorOffset = offset;
		d.LightModulatorCount = count;
		if ( WritesLight ) lightNative.SetLightModulatorRange( offset, count );
		NotifyChanged( Rendering.SceneObjectChange.Settings );
	}

	internal SceneLight() { }
	internal SceneLight( HandleCreationData _ ) { }

	/// <summary>
	/// Color and brightness of the light
	/// </summary>
	public Color LightColor
	{
		get { return Description.Color; }
		set { Change().Color = value; Recalculate(); if ( WritesLight ) lightNative.SetColor( value ); NotifyChanged( Rendering.SceneObjectChange.Settings ); }
	}

	/// <summary>
	/// Radius of the light in units
	/// </summary>
	public float Radius
	{
		get { return Description.Range; }
		set
		{
			if ( Radius == value ) return;
			Change().Range = value; Recalculate();
			if ( HasLight ) lightNative.SetRadius( value );
			NotifyChanged( Rendering.SceneObjectChange.Settings );
			BoundsVersion++;
		}
	}

	/// <summary>
	/// The light attenuation constant term
	/// </summary>
	public float ConstantAttenuation
	{
		get { return Description.Attenuation0; }
		set { Change().Attenuation0 = value; Recalculate(); if ( WritesLight ) lightNative.SetConstantAttn( value ); NotifyChanged( Rendering.SceneObjectChange.Settings ); }
	}

	/// <summary>
	/// The light attenuation linear term
	/// </summary>
	public float LinearAttenuation
	{
		get { return Description.Attenuation1; }
		set { Change().Attenuation1 = value; Recalculate(); if ( WritesLight ) lightNative.SetLinearAttn( value ); NotifyChanged( Rendering.SceneObjectChange.Settings ); }
	}

	/// <summary>
	/// The light attenuation quadratic term
	/// </summary>
	public float QuadraticAttenuation
	{
		// Note: to make these numbers sane I'm doing some calculation here
		get { return Description.Attenuation2 * 10000.0f; }
		set { Change().Attenuation2 = value / 10000.0f; Recalculate(); if ( WritesLight ) lightNative.SetQuadraticAttn( value / 10000.0f ); NotifyChanged( Rendering.SceneObjectChange.Settings ); }
	}

	/// <summary>
	/// Makes this a baked light addressed by probe/lightmap index textures (0-254).
	/// Pass -1 to restore dynamic lighting. Index 255 is reserved for an empty texture channel.
	/// </summary>
	public void SetBakedLightIndex( int index, bool mixedShadows = false )
	{
		if ( index is < -1 or > 254 )
			throw new ArgumentOutOfRangeException( nameof( index ) );
		ref var d = ref Change();
		d.BakeLightIndex = index;
		d.BakeLightIndexScale = 1.0f;
		d.UsesIndexedBakedLighting = index >= 0 ? 1 : 0;
		d.Flags &= ~(FlagMixedShadows | FlagBaked);
		if ( index >= 0 ) d.Flags |= FlagBaked | (mixedShadows ? FlagMixedShadows : 0u);

		if ( WritesLight )
		{
			lightNative.SetBakeLightIndex( index );
			lightNative.SetBakeLightIndexScale( 1.0f );
			lightNative.SetUsesIndexedBakedLighting( index >= 0 );
			lightNative.SetLightFlags( d.Flags );
		}

		NotifyChanged( Rendering.SceneObjectChange.Settings );
	}

	/// <summary>
	/// Get or set the resolution of the shadow map. If this is zero the engine will decide what it should use.
	/// </summary>
	public int ShadowTextureResolution
	{
		get { return Description.ShadowWidth; }
		set { ref var d = ref Change(); d.ShadowWidth = value; d.ShadowHeight = value; if ( WritesLight ) lightNative.SetShadowTextureResolution( value ); NotifyChanged( Rendering.SceneObjectChange.Settings ); }
	}

	/// <summary>
	/// Enable or disable shadow rendering
	/// </summary>
	public bool ShadowsEnabled
	{
		get { return Description.CastShadows != 0; }
		set { Change().CastShadows = value ? 1 : 0; if ( WritesLight ) lightNative.SetShadows( value ); NotifyChanged( Rendering.SceneObjectChange.Settings ); }
	}

	private Texture _lightCookie;

	/// <summary>
	/// Access the LightCookie - which is a texture that gets drawn over the light
	/// </summary>
	public Texture LightCookie
	{
		get => _lightCookie ??= HasLight ? Texture.FromNative( lightNative.GetLightCookie() ) : null;
		set
		{
			_lightCookie = value;
			if ( WritesLight ) lightNative.SetLightCookie( value == null ? default : value.native ); NotifyChanged( Rendering.SceneObjectChange.Settings );
		}
	}

	/// <summary>
	/// Should this light contribute diffuse lighting?
	/// </summary>
	public bool RenderDiffuse
	{
		get => _renderDiffuse;
		set
		{
			_renderDiffuse = value;
			Change().RenderDiffuse = value ? 1 : 0;
			if ( WritesLight ) lightNative.SetRenderDiffuse( value ); NotifyChanged( Rendering.SceneObjectChange.Settings );
		}
	}
	private bool _renderDiffuse = true;

	/// <summary>
	/// Should this light contribute specular highlights?
	/// </summary>
	public bool RenderSpecular
	{
		get => _renderSpecular;
		set
		{
			_renderSpecular = value;
			Change().RenderSpecular = value ? 1 : 0;
			if ( WritesLight ) lightNative.SetRenderSpecular( value ); NotifyChanged( Rendering.SceneObjectChange.Settings );
		}
	}
	private bool _renderSpecular = true;

	/// <summary>
	/// Should this light contribute transmissive lighting (light passing through surfaces)?
	/// </summary>
	public bool RenderTransmissive
	{
		get => _renderTransmissive;
		set
		{
			_renderTransmissive = value;
			Change().RenderTransmissive = value ? 1 : 0;
			if ( WritesLight ) lightNative.SetRenderTransmissive( value ); NotifyChanged( Rendering.SceneObjectChange.Settings );
		}
	}
	private bool _renderTransmissive = true;

	public enum FogLightingMode
	{
		None,
		Baked,
		Dynamic,
		DynamicNoShadows
	}

	public enum LightShape
	{
		Sphere,
		Capsule,
		Rectangle
	}

	public LightShape Shape
	{
		get => (LightShape)Description.Shape;
		set { Change().Shape = (int)value; Recalculate(); if ( HasLight ) lightNative.SetLightShape( (LightSourceShape_t)value ); NotifyChanged( Rendering.SceneObjectChange.Settings ); }
	}

	public Vector2 ShapeSize
	{
		set
		{
			ref var d = ref Change();
			d.LightSourceDim0 = value.x;
			d.LightSourceDim1 = value.y;
			Recalculate();
			if ( HasLight )
			{
				lightNative.SetLightSourceDim0( value.x );
				lightNative.SetLightSourceDim1( value.y );
			}

			NotifyChanged( Rendering.SceneObjectChange.Settings );
		}
	}

	public FogLightingMode FogLighting
	{
		get => (FogLightingMode)Description.FogLightingMode;
		set { Change().FogLightingMode = (int)value; if ( WritesLight ) lightNative.SetFogLightingMode( (int)value ); NotifyChanged( Rendering.SceneObjectChange.Settings ); }
	}

	public float FogStrength
	{
		get => Description.FogContributionStrength;
		set { Change().FogContributionStrength = value; if ( WritesLight ) lightNative.SetFogContributionStength( value ); NotifyChanged( Rendering.SceneObjectChange.Settings ); }
	}

	/// <summary>
	/// Stupid fucking shit
	/// </summary>
	internal Vector3 WorldDirection => Description.Direction;

	public float ShadowBias
	{
		get;
		set { field = value; NotifyChanged( Rendering.SceneObjectChange.Settings ); }
	}

	public float ShadowHardness
	{
		get;
		set { field = value; NotifyChanged( Rendering.SceneObjectChange.Settings ); }
	}

	/// <summary>
	/// Should this light generate screen-space contact shadows on top of its shadow maps?
	/// </summary>
	internal bool ContactShadows
	{
		get;
		set { field = value; NotifyChanged( Rendering.SceneObjectChange.Settings ); }
	}

	internal override void OnTransformChanged( in Transform tx )
	{
		base.OnTransformChanged( tx );

		// As native's SetWorldDirection then SetWorldPosition; both reach native, since its bounds come from them
		ref var d = ref Change();
		var forward = tx.Rotation.Forward;
		if ( d.Type == TypeDirectional ) forward = -forward;
		d.Direction = NormalizedSafe( forward, new Vector3( 1, 0, 0 ) );
		d.Up = NormalizedSafe( tx.Rotation.Up, new Vector3( 0, 0, 1 ) );
		d.Position = tx.Position;
		Recalculate();

		if ( HasLight )
		{
			lightNative.SetWorldDirection( tx.Rotation );
			lightNative.SetWorldPosition( tx.Position );
		}

		BoundsVersion++;
	}

	[Obsolete( "Use ScenePointLight (or stop fucking using SceneObjects at all)" )]
	public SceneLight( SceneWorld sceneWorld, Vector3 position, float radius, Color color )
	{
		Assert.IsValid( sceneWorld );

		using ( var h = IHandle.MakeNextHandle( this ) )
		{
			if ( MakesNativeTwin ) CSceneSystem.CreatePointLight( sceneWorld );
			else MakeLightWithoutTwin( sceneWorld, NewDescription( TypePoint ) );
		}

		Position = position;
		Radius = radius;
		LightColor = color;
		QuadraticAttenuation = 1.0f;
	}

	[Obsolete( "Use ScenePointLight (or stop fucking using SceneObjects at all)" )]
	public SceneLight( SceneWorld sceneWorld ) : this( sceneWorld, Vector3.Zero, 100, Color.White * 10.0f )
	{

	}

	internal override void OnNativeInit( CSceneObject ptr )
	{
		base.OnNativeInit( ptr );

		lightNative = (CSceneLightObject)ptr;

		lock ( World.InternalSceneObjects )
		{
			World.InternalSceneLights.Add( this );
		}
	}

	/// <summary>
	/// Made without a native twin (<see cref="SceneObject.MakesNativeTwin"/> off): in its world, its renderer and its world's
	/// lights as <see cref="OnNativeInit"/> puts one, with the description and flags native's <c>CSceneSystem::CreateLight</c>
	/// would have given it, and its bounds worked out here from then on (<see cref="DescriptionBounds"/>).
	/// </summary>
	private protected void MakeLightWithoutTwin( SceneWorld world, in LightDescription description,
		Rendering.SceneObjectFlags flags = Rendering.SceneObjectFlags.IsLight | Rendering.SceneObjectFlags.IsDirectLight )
	{
		desc = description;
		described = true;
		MakeWithoutTwin( world, null, flags );

		// CreateLight: a directional light's bounds infinite, the others' from the description
		if ( desc.Type == TypeDirectional ) KeepFixedBounds( true );
		else KeepBounds( DescriptionBounds( desc ) );

		lock ( World.InternalSceneObjects )
		{
			World.InternalSceneLights.Add( this );
		}
	}

	/// <summary>Whether it has its native light: a twin, or the light native code made.</summary>
	private protected bool HasLight => !lightNative.IsNull;

	/// <summary>Whether a setting also goes to the native light: while something reads it, and only if there is one.</summary>
	bool WritesLight => WritesNative && !lightNative.IsNull;

	internal override void Destroyed()
	{
		lock ( World.InternalSceneObjects )
		{
			World.InternalSceneLights.Remove( this );
		}

		base.Destroyed();
	}

	internal override void OnNativeDestroy()
	{
		lightNative = IntPtr.Zero;

		base.OnNativeDestroy();
	}
}
