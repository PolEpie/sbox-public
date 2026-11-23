using Sandbox.Rendering;

namespace Sandbox;

/// <summary>
/// A decal. Use the Component.
/// </summary>
/// <remarks>
/// Managed only: it has no native object, and its settings live here.
/// </remarks>
internal sealed class DecalSceneObject : SceneObject
{
	Texture colorTexture, normalTexture, rmoTexture, heightTexture, emissionTexture;
	Vector4 color;
	uint sortOrder, exclusionBitMask, sequenceIndex;
	float attenuationAngle, colorMix, emissionEnergy, parallaxStrength, coverageAmount, coverageRange;
	int samplerIndex;

	public DecalBlendMode BlendMode
	{
		get;
		set
		{
			if ( field == value ) return;
			field = value;
			Attributes.Set( "DecalBlendMode", (int)value );
			NotifyChanged( Rendering.SceneObjectChange.Settings );
		}
	}

	internal DecalSceneObject() { }

	public Texture ColorTexture
	{
		get => colorTexture;
		set { colorTexture = value; SettingsChanged(); }
	}

	public Texture NormalTexture
	{
		get => normalTexture;
		set { normalTexture = value; SettingsChanged(); }
	}

	public Texture RMOTexture
	{
		get => rmoTexture;
		set { rmoTexture = value; SettingsChanged(); }
	}

	public Texture HeightTexture
	{
		get => heightTexture;
		set { heightTexture = value; SettingsChanged(); }
	}

	public Color Color
	{
		get => color;
		set { color = value; SettingsChanged(); }
	}

	public uint SortOrder
	{
		get => sortOrder;
		set { sortOrder = value; SettingsChanged(); }
	}

	public uint ExclusionBitMask
	{
		get => exclusionBitMask;
		set { exclusionBitMask = value; SettingsChanged(); }
	}

	public Texture EmissionTexture
	{
		get => emissionTexture;
		set { emissionTexture = value; SettingsChanged(); }
	}

	public float AttenuationAngle
	{
		get => attenuationAngle;
		set { attenuationAngle = value; SettingsChanged(); }
	}

	public float ColorMix
	{
		get => colorMix;
		set { colorMix = value; SettingsChanged(); }
	}

	public float EmissionEnergy
	{
		get => emissionEnergy;
		set { emissionEnergy = value; SettingsChanged(); }
	}

	public uint SequenceIndex
	{
		get => sequenceIndex;
		set { sequenceIndex = value; SettingsChanged(); }
	}

	public float ParallaxStrength
	{
		get => parallaxStrength;
		set { parallaxStrength = value; SettingsChanged(); }
	}

	public int SamplerIndex
	{
		get => samplerIndex;
		set { samplerIndex = value; SettingsChanged(); }
	}

	public float CoverageAmount
	{
		get => coverageAmount;
		set { coverageAmount = value; SettingsChanged(); }
	}

	public float CoverageRange
	{
		get => coverageRange;
		set { coverageRange = value; SettingsChanged(); }
	}

	public DecalSceneObject( SceneWorld world )
	{
		Assert.IsValid( world );

		MakeDecal( world );
	}

	/// <summary>
	/// What native's decal object started as (opaque, visible, not batchable, no bounds until it's placed), with its defaults for
	/// the settings.
	/// </summary>
	void MakeDecal( SceneWorld world )
	{
		color = new Vector4( 1, 1, 1, 1 );
		attenuationAngle = 1.0f;
		emissionEnergy = 1.0f;
		colorMix = 1.0f;
		parallaxStrength = 0.25f;
		coverageAmount = 1.0f;
		coverageRange = 0.07f;

		MakeManaged( world, null, Rendering.SceneObjectFlags.IsOpaque, batchable: false );
		KeepFixedBounds( false );
	}

	/// <summary>A unit box around its transform.</summary>
	internal override void OnTransformChanged( in Transform tx )
	{
		base.OnTransformChanged( tx );
		KeepBounds( TransformBounds( tx, new BBox( new Vector3( -0.5f ), new Vector3( 0.5f ) ) ) );
	}

	void SettingsChanged() => NotifyChanged( Rendering.SceneObjectChange.Settings );

	internal override bool OwnsRenderObject => true;

	internal override Borealis.RenderObject MakeRenderObject( Rendering.ISceneWorldRender render ) => new Borealis.DecalObject( Transform );

	/// <summary>
	/// Its box and everything it projects, as <c>CLightBinnerStandard</c> reads a <c>CDecalSceneObject</c>.
	/// </summary>
	internal override void WriteRender( Borealis.RenderObject obj, Rendering.SceneObjectChange change, Rendering.ISceneWorldRender render )
	{
		base.WriteRender( obj, change, render );
		var decal = (Borealis.DecalObject)obj;

		if ( (change & Rendering.SceneObjectChange.Transform) != 0 )
			decal.Transform = Transform == default ? Transform.Zero : Transform;

		if ( (change & Rendering.SceneObjectChange.Visibility) != 0 )
			decal.Visible = RenderingEnabled;

		if ( (change & Rendering.SceneObjectChange.Settings) != 0 )
		{
			decal.Color = render.Texture( colorTexture );
			decal.Normal = render.Texture( normalTexture );
			decal.RoughnessMetalnessOcclusion = render.Texture( rmoTexture );
			decal.Height = render.Texture( heightTexture );
			decal.Emission = render.Texture( emissionTexture );
			decal.Tint = Color;
			decal.SortOrder = sortOrder;
			decal.ExclusionBitMask = exclusionBitMask;
			decal.AttenuationAngle = attenuationAngle;
			decal.EmissionEnergy = emissionEnergy;
			decal.SequenceIndex = sequenceIndex;
			decal.ColorMix = colorMix;
			decal.BlendMode = (Borealis.DecalBlendMode)BlendMode;
			decal.ParallaxStrength = parallaxStrength;
			decal.SamplerIndex = samplerIndex;
			decal.CoverageAmount = coverageAmount;
			decal.CoverageRange = coverageRange;
		}
	}
}
