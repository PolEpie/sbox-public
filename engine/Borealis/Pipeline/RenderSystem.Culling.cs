namespace Borealis;

/// <summary>
/// Shared main/shadow-view culling and object-to-feature lookup.
/// </summary>
public sealed partial class RenderSystem
{
	readonly List<int> candidates = new();

	/// <summary>
	/// Collect visible objects for the main view.
	/// </summary>
	internal void Collect( RenderWorld world, RenderView view, ref RenderStats stats )
	{
		frame.World = world;
		frame.View = view;
		// Not in a view rendered while another render of the world is under way (a child view's): its frame reads them
		if ( world.Rendering <= 1 ) world.ResolveAnimation();
		mainPass.Reset( features.Count );
		mainPass.View = view;
		mainPass.Root = view;
		mainPass.World = world;

		// The cull reasons view sees every object in the frustum, so it can tell the small ones from the off-screen ones and
		// draw them anyway
		mainPass.CullReasons = null;
		var reasons = (frame.DebugViews & DebugViews.CullReasons) != 0 && !isSkybox;
		if ( reasons )
		{
			if ( cullReasons.Length < world.Objects.Length ) cullReasons = new byte[world.Objects.Length];
			Array.Clear( cullReasons );
			mainPass.CullReasons = cullReasons;
		}

		// What the view's resident list draws (the GPU culls it) is left out: the frame's transforms begin here, as the list is
		// worked out from them
		var occlusion = RenderHost.Current.OcclusionCulling && DepthPrepass && !isSkybox && !view.Capture;
		collectedResident = CollectSkipsResident && !reasons && frame.DebugViews == DebugViews.None && !occlusion;
		if ( collectedResident )
		{
			transforms.World = world;
			transforms.Clear();
			mainPass.ResidentHolds = aggregates.CollectHolds( world, mainPass, transforms );
			mainPass.TreeKept = aggregates.CollectTreeKept( world, mainPass );
		}

		var inFrustum = FindCandidates( world, mainPass, candidates, out var inTree, out var rejected, sizeCull: !reasons );
		stats.ObjectsSizeCulled += rejected;
		if ( reasons ) stats.ObjectsFrustumCulled += world.Count - candidates.Count;

		var typeFeatures = PrepareCullFeatures( world );
		if ( ParallelRecording && candidates.Count >= MinParallelCullCandidates && JobDispatcher.MaxDegreeOfParallelism > 1 )
			CullInChunks( world, mainPass, candidates, inTree, inFrustum, typeFeatures, ref stats );
		else
			Cull( world, mainPass, candidates, inTree, inFrustum, typeFeatures, ref stats );
	}

	/// <summary>
	/// Leave out of the main view's collect what its resident list draws (<see cref="ViewPass.ResidentHolds"/>). Never with
	/// debug views or occlusion culling. Off for the tests' comparisons, and with borealislab's <c>-collect-resident</c>.
	/// </summary>
	internal static bool CollectSkipsResident { get; set; } = true;

	/// <summary>Whether this frame's collect left resident objects out, having begun the frame's transforms.</summary>
	bool collectedResident;

	/// <summary>
	/// Candidates a main view has before culling splits them across threads. 2,500 broke even; 5,000 took 20-30% off
	/// collecting, the serial tree query being the rest.
	/// </summary>
	internal static int MinParallelCullCandidates { get; set; } = 4096;

	// The main view's cull reasons, by world index, while the cull reasons view is on
	byte[] cullReasons = [];

	// Candidates in each chunk of a split cull; enough chunks that threads finishing early can take another
	const int CullChunkCandidates = 1024;

	// Each chunk's visible objects by feature, and its counts, merged in candidate order after
	List<int>[][] chunkVisible = [];
	RenderStats[] chunkStats = [];
	(RenderWorld World, ViewPass Pass, List<int> Candidates, int InTree, bool InFrustum, int[] TypeFeatures) chunkCull;

	/// <summary>
	/// <see cref="Cull"/> split into chunks of candidates across the job dispatcher's threads, each into its own lists,
	/// then joined in candidate order - the same visible lists and counts as culling them in one go.
	/// </summary>
	void CullInChunks( RenderWorld world, ViewPass pass, List<int> candidates, int inTree, bool inFrustum, int[] typeFeatures, ref RenderStats stats )
	{
		var chunks = (candidates.Count + CullChunkCandidates - 1) / CullChunkCandidates;
		if ( chunkVisible.Length < chunks )
		{
			var old = chunkVisible.Length;
			Array.Resize( ref chunkVisible, chunks );
			Array.Resize( ref chunkStats, chunks );
			for ( int c = old; c < chunks; c++ ) chunkVisible[c] = [];
		}

		for ( int c = 0; c < chunks; c++ )
		{
			if ( chunkVisible[c].Length < features.Count )
			{
				var old = chunkVisible[c].Length;
				Array.Resize( ref chunkVisible[c], features.Count );
				for ( int f = old; f < features.Count; f++ ) chunkVisible[c][f] = new List<int>();
			}

			for ( int f = 0; f < features.Count; f++ ) chunkVisible[c][f].Clear();
			chunkStats[c] = default;
		}

		chunkCull = (world, pass, candidates, inTree, inFrustum, typeFeatures);
		JobDispatcher.For( chunks, this, static ( system, c ) =>
		{
			var (world, pass, candidates, inTree, inFrustum, typeFeatures) = system.chunkCull;
			var start = c * CullChunkCandidates;
			var end = Math.Min( start + CullChunkCandidates, candidates.Count );
			system.CullRange( world, pass, candidates, start, end, inTree, inFrustum, typeFeatures, system.chunkVisible[c], ref system.chunkStats[c] );
		}, itemsPerBatch: 1 );
		chunkCull = default;

		for ( int f = 0; f < features.Count; f++ )
		{
			var visible = pass.Visible( features[f] );
			for ( int c = 0; c < chunks; c++ ) visible.AddRange( chunkVisible[c][f] );
		}

		for ( int c = 0; c < chunks; c++ )
		{
			stats.ObjectsSizeCulled += chunkStats[c].ObjectsSizeCulled;
			stats.ObjectsVisible += chunkStats[c].ObjectsVisible;
			stats.ObjectsTagCulled += chunkStats[c].ObjectsTagCulled;
			stats.ObjectsBoxCulled += chunkStats[c].ObjectsBoxCulled;
			stats.ObjectsFrustumCulled += chunkStats[c].ObjectsFrustumCulled;
		}
	}

	/// <summary>
	/// Query the frustum or light sphere, then append non-tree objects. Entries encode (world index &lt;&lt; 1) | contained.
	/// Returns true when containment means frustum containment. Only the first <paramref name="inTree"/> entries are size-culled;
	/// <paramref name="rejected"/> counts objects in size-rejected subtrees, including possible off-screen objects. Without
	/// <paramref name="sizeCull"/>, no subtree is rejected, and <see cref="Cull"/> size culls each object.
	/// </summary>
	internal static bool FindCandidates( RenderWorld world, ViewPass pass, List<int> candidates, out int inTree, out int rejected, bool sizeCull = true )
	{
		candidates.Clear();
		if ( pass.StaticFilter == StaticFilter.Nothing )
		{
			(inTree, rejected) = (0, 0);
			return true;
		}

		var size = sizeCull && !pass.KeepsSmallCasters ? SizeCull.ForPass( pass ) : default;

		// Avoid traversing static objects for dynamic-only shadows.
		var tree = pass.StaticFilter is StaticFilter.NoStatic or StaticFilter.SettledNoStatic or StaticFilter.Unsettled ? world.DynamicTree : world.Tree;

		using var zone = Zones.TreeQuery.Start();
		bool inFrustum;
		if ( pass.LightSphere is { } sphere )
		{
			var query = new SpatialTree.SphereQuery { Center = sphere.Center, Radius = sphere.Radius, Size = size };
			tree.QueryClassified( ref query, candidates, out rejected, pass.TreeKept );
			inFrustum = false;
		}
		else
		{
			var query = new SpatialTree.FrustumQuery { Frustum = pass.View.Frustum, Size = size };
			tree.QueryClassified( ref query, candidates, out rejected, pass.TreeKept );
			inFrustum = true;
		}

		// Non-tree casters are kept separately so each shadow face can skip lights, probes and decals.
		inTree = candidates.Count;
		candidates.AddRange( world.UnculledCandidates( pass.IsShadow && pass.CullReasons is null ) );

		return inFrustum;
	}

	/// <summary>
	/// Filter candidates by frustum, root-view size and tags, then feature support.
	/// Shadow views also apply caster and static filters. With <see cref="ViewPass.CullReasons"/>, meshes culled for their
	/// size or tags are kept with their reason, and not counted visible.
	/// </summary>
	internal void Cull( RenderWorld world, ViewPass pass, List<int> candidates, int inTree, bool inFrustum, int[] typeFeatures, ref RenderStats stats )
		=> CullRange( world, pass, candidates, 0, candidates.Count, inTree, inFrustum, typeFeatures, pass.VisibleByFeature, ref stats );

	/// <summary>
	/// <see cref="Cull"/> candidates [<paramref name="start"/>, <paramref name="end"/>) into <paramref name="visible"/>, by
	/// feature index.
	/// </summary>
	void CullRange( RenderWorld world, ViewPass pass, List<int> candidates, int start, int end, int inTree, bool inFrustum, ReadOnlySpan<int> typeFeatures, List<int>[] visible, ref RenderStats stats )
	{
		var frustum = pass.View.Frustum;
		var root = pass.Root;
		var sizeCull = pass.KeepsSmallCasters ? default : SizeCull.ForPass( pass );
		var shadow = pass.IsShadow;
		var filter = pass.StaticFilter;
		var objects = world.Objects;
		var centers = world.BoundsCenter;
		var extents = world.BoundsExtents;
		var subWorlds = world.SubWorlds;
		var reasons = pass.CullReasons;
		var members = pass.ResidentMembers;
		var holds = pass.ResidentHolds;
		var boxes = System.Runtime.InteropServices.CollectionsMarshal.AsSpan( world.CullingBoxes );

		var types = world.ObjectTypeIndices;
		var entries = System.Runtime.InteropServices.CollectionsMarshal.AsSpan( candidates );
		for ( int k = start; k < end; k++ )
		{
			var entry = entries[k];
			var i = entry >> 1;

			// Drawn from the main view's resident list
			if ( holds is not null && i < holds.Length && holds[i] ) continue;

			// Drawn from the view's resident list (its query leaves most of these out already)
			if ( members is not null && i < members.Length && members[i] != 0 ) continue;

			if ( !(inFrustum && (entry & 1) != 0) && !frustum.Intersects( centers[i], extents[i] ) )
			{
				if ( reasons is not null ) stats.ObjectsFrustumCulled++;
				continue;
			}

			// Size-cull tree entries before accessing objects.
			var reason = CullReason.None;
			if ( k < inTree && sizeCull.Culls( centers[i], extents[i] ) )
			{
				stats.ObjectsSizeCulled++;
				if ( reasons is null ) continue;
				reason = CullReason.Size;
			}

			// Another world's objects that this view doesn't draw aren't culled: they aren't in its view at all
			if ( !root.ShowsSubWorld( subWorlds[i] ) ) continue;
			var obj = objects[i];
			if ( reason == CullReason.None && !root.ShowsShown( obj ) )
			{
				stats.ObjectsTagCulled++;
				if ( reasons is null ) continue;
				reason = CullReason.Tags;
			}

			// Hidden by the world's culling boxes, in every view
			if ( boxes.Length > 0 && CullingBox.CullsObject( obj ) && CullingBox.Culls( boxes, centers[i], extents[i] ) )
			{
				stats.ObjectsBoxCulled++;
				continue;
			}

			var feature = typeFeatures[types[i]];
			if ( feature < 0 ) continue;
			var f = features[feature];

			// Only meshes are drawn anyway: a culled light or probe would change what the visible ones look like
			if ( reason != CullReason.None )
			{
				if ( obj is not MeshObject ) continue;
				reasons[i] = (byte)reason;
				visible[feature].Add( i );
				continue;
			}

			if ( shadow )
			{
				if ( !f.DrawsShadows || !f.CastsShadow( obj ) ) continue;
				// A static caster that animates vertices is drawn live: never in a cache, the sun's settled far cascade included
				if ( filter == StaticFilter.NoStatic && obj.CachesAsStatic ) continue;
				if ( filter >= StaticFilter.Settled && !TakesCaster( pass, filter, obj, world.MovedStamps[i] ) ) continue;
			}

			visible[feature].Add( i );
			stats.ObjectsVisible++;
		}
	}

	/// <summary>
	/// Whether a cache (<see cref="StaticFilter.Settled"/>, <see cref="StaticFilter.SettledNoStatic"/>) or what's drawn on top
	/// of it (<see cref="StaticFilter.Unsettled"/>) takes a caster, which <see cref="DrawnLive"/> or <see cref="TakesSettled"/>
	/// decides, but for one that animates only some of its LODs in a view that <see cref="ViewPass.SplitsLods"/>: both take it,
	/// for the LODs each draws (<see cref="ViewPass.DrawsLod"/>).
	/// </summary>
	static bool TakesCaster( ViewPass pass, StaticFilter filter, RenderObject obj, ulong moved )
	{
		if ( !DrawnLive( pass, obj ) ) return TakesSettled( pass, filter, obj.IsStatic, moved );
		return filter == StaticFilter.Unsettled || (pass.SplitsLods && obj.AnimatesVertices && !obj.AnimatesAllLods);
	}

	/// <summary>
	/// A caster no cache holds, drawn live on top of one: a static caster that animates vertices, and in a light's cache, a
	/// custom object that isn't static, whose draws change without anything moving. The sun's far cascade, far from the
	/// camera, keeps custom objects once they've settled. Its cache keeps the LODs of a static caster that don't animate
	/// (<see cref="ViewPass.SplitsLods"/>).
	/// </summary>
	static bool DrawnLive( ViewPass pass, RenderObject obj ) => obj.AnimatesVertices || (pass.LightSphere is not null && !obj.IsStatic && obj is CustomObject);

	/// <summary>
	/// Whether a cache (<see cref="StaticFilter.Settled"/>, <see cref="StaticFilter.SettledNoStatic"/>) or what's drawn on top
	/// of it (<see cref="StaticFilter.Unsettled"/>) takes a caster that last moved at <paramref name="moved"/>. The one on top
	/// counts those it takes that have settled since its cache was drawn.
	/// </summary>
	static bool TakesSettled( ViewPass pass, StaticFilter filter, bool isStatic, ulong moved )
	{
		if ( filter == StaticFilter.Settled && isStatic ) return true;
		if ( isStatic ) return false;
		var cached = Borealis.Shadows.SettledCasters.SettledBy( moved, pass.SettledStamp, pass.RestlessStamp );
		if ( filter != StaticFilter.Unsettled ) return cached;
		if ( cached ) return false;

		if ( pass.NewlySettled is { } count && Borealis.Shadows.SettledCasters.Settled( moved ) ) count.Value++;
		return true;
	}

	sealed class CullFeatures
	{
		public int FeatureCount = -1;
		public int TypeCount;
		public int[] ByType = [];
	}

	readonly System.Runtime.CompilerServices.ConditionalWeakTable<RenderWorld, CullFeatures> cullFeatures = new();

	/// <summary>Resolve world type indices against this renderer's feature order before giving the map to cull jobs.</summary>
	internal int[] PrepareCullFeatures( RenderWorld world )
	{
		var map = cullFeatures.GetValue( world, static _ => new CullFeatures() );
		var types = world.ObjectTypes;
		if ( map.FeatureCount != features.Count )
		{
			map.FeatureCount = features.Count;
			map.TypeCount = 0;
		}

		if ( map.ByType.Length < types.Length ) Array.Resize( ref map.ByType, Math.Max( types.Length, map.ByType.Length * 2 ) );
		for ( int t = map.TypeCount; t < types.Length; t++ )
		{
			var feature = 0;
			while ( feature < features.Count && !features[feature].Accepts( types[t] ) ) feature++;
			map.ByType[t] = feature < features.Count ? feature : -1;
		}
		map.TypeCount = types.Length;
		return map.ByType;
	}
}
