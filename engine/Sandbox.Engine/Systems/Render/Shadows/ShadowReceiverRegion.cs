using NativeEngine;

namespace Sandbox.Rendering;

/// <summary>
/// Where the pixels that sample one sun cascade can be: inside the camera's side planes and the cascade's selection
/// sphere, but not inside the previous cascade's, which those pixels pick first. A caster only matters to the cascade
/// if its shadow, swept along the light, can reach that region. Published on cascade views so GPU-driven renderers
/// can skip the casters that can't.
/// </summary>
internal struct ShadowReceiverRegion
{
	public Vector3 LightDirection; // The way the light travels, normalized
	public Plane Left, Right, Top, Bottom; // Camera side planes, facing in
	public Vector4 Sphere; // xyz = center, w = radius: pixels outside use a later cascade or none
	public Vector4 Excluded; // xyz = center, w = radius: pixels inside use an earlier cascade. w = 0 for the first

	static readonly StringToken LightAttribute = "ShadowReceiverLight";
	static readonly StringToken SphereAttribute = "ShadowReceiverSphere";
	static readonly StringToken ExcludedAttribute = "ShadowReceiverExcluded";
	static readonly StringToken[] PlaneAttributes = ["ShadowReceiverPlane0", "ShadowReceiverPlane1", "ShadowReceiverPlane2", "ShadowReceiverPlane3"];

	public readonly Plane GetPlane( int i ) => i switch { 0 => Left, 1 => Right, 2 => Top, _ => Bottom };

	public readonly void WriteTo( CRenderAttributes attributes )
	{
		attributes.SetVector4DValue( LightAttribute, new Vector4( LightDirection, 1.0f ) );
		attributes.SetVector4DValue( SphereAttribute, Sphere );
		attributes.SetVector4DValue( ExcludedAttribute, Excluded );

		for ( int i = 0; i < 4; i++ )
		{
			var p = GetPlane( i );
			attributes.SetVector4DValue( PlaneAttributes[i], new Vector4( p.Normal, p.Distance ) );
		}
	}

	/// <summary>
	/// The region published on a view, if it is a sun cascade.
	/// </summary>
	public static bool TryRead( CRenderAttributes attributes, out ShadowReceiverRegion region )
	{
		region = default;

		var light = attributes.GetVector4DValue( LightAttribute, default );
		if ( light.w == 0.0f )
			return false;

		region.LightDirection = new Vector3( light.x, light.y, light.z );
		region.Sphere = attributes.GetVector4DValue( SphereAttribute, default );
		region.Excluded = attributes.GetVector4DValue( ExcludedAttribute, default );
		region.Left = ReadPlane( attributes, 0 );
		region.Right = ReadPlane( attributes, 1 );
		region.Top = ReadPlane( attributes, 2 );
		region.Bottom = ReadPlane( attributes, 3 );
		return true;

		static Plane ReadPlane( CRenderAttributes attributes, int i )
		{
			var v = attributes.GetVector4DValue( PlaneAttributes[i], default );
			return new Plane { Normal = new Vector3( v.x, v.y, v.z ), Distance = v.w };
		}
	}

	/// <summary>
	/// Whether a sphere's shadow can fall on this region. Clips the sphere's path along the light to the region and
	/// checks something is left. Matches MayShadowReceivers in clutter_cull_cs.shader.
	/// </summary>
	public readonly bool MayShadow( Vector3 center, float radius )
	{
		var light = LightDirection;

		// Where the path is within the sphere grown by the caster's radius
		var toCenter = center - new Vector3( Sphere.x, Sphere.y, Sphere.z );
		float reach = Sphere.w + radius;
		float b = toCenter.Dot( light );
		float discriminant = b * b - (toCenter.Dot( toCenter ) - reach * reach);
		if ( discriminant < 0.0f )
			return false;

		float root = MathF.Sqrt( discriminant );
		float t0 = MathF.Max( -b - root, 0.0f );
		float t1 = -b + root;

		// Then in front of every side plane, grown by the radius
		for ( int i = 0; i < 4; i++ )
		{
			var p = GetPlane( i );
			float distance = p.Normal.Dot( center ) - p.Distance + radius;
			float rate = p.Normal.Dot( light );

			if ( MathF.Abs( rate ) < 1e-6f )
			{
				if ( distance < 0.0f ) return false;
				continue;
			}

			float t = -distance / rate;
			if ( rate > 0.0f ) t0 = MathF.Max( t0, t );
			else t1 = MathF.Min( t1, t );
		}

		if ( t0 > t1 )
			return false;

		// Pixels in the earlier cascade's sphere never sample this one. The sphere is convex, so the path is
		// inside it if both ends are.
		float inner = Excluded.w - radius;
		if ( inner > 0.0f )
		{
			var excludedCenter = new Vector3( Excluded.x, Excluded.y, Excluded.z );
			var a = center + light * t0 - excludedCenter;
			var e = center + light * t1 - excludedCenter;
			if ( a.Dot( a ) <= inner * inner && e.Dot( e ) <= inner * inner )
				return false;
		}

		return true;
	}
}
