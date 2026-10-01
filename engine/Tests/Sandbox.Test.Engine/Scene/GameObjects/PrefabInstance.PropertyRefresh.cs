namespace SceneTests.GameObjects;

/// <summary>
/// Refreshing a patch for objects whose own properties changed re-diffs just those objects. It must always
/// end up with exactly the patch a full refresh of the whole instance gives.
/// </summary>
public partial class PrefabInstanceTest
{
	static string Describe( Json.Patch patch )
	{
		var overrides = patch.PropertyOverrides
			.Select( x => $"{x.Target.Type}:{x.Target.IdValue}.{x.Property}={x.Value?.ToJsonString()}" )
			.Order();

		var structure = new Json.Patch { AddedObjects = patch.AddedObjects, RemovedObjects = patch.RemovedObjects, MovedObjects = patch.MovedObjects };
		return string.Join( "\n", overrides ) + "\n" + Json.SerializeAsObject( structure ).ToJsonString();
	}

	/// <summary>
	/// Refreshes for <paramref name="changed"/> only, checks it matches a full refresh, and returns that patch.
	/// </summary>
	static string AssertTargetedRefreshMatchesFull( GameObject instance, params GameObject[] changed )
	{
		instance.PrefabInstance.RefreshPatch( changed );
		var targeted = Describe( instance.PrefabInstance.Patch );

		instance.PrefabInstance.RefreshPatch();
		Assert.AreEqual( Describe( instance.PrefabInstance.Patch ), targeted );

		return targeted;
	}

	[TestMethod]
	public void PropertyRefreshMatchesFullRefresh()
	{
		using var registration = RegisterBasicPrefab( out var prefabScene );

		var scene = new Scene();
		using var sceneScope = scene.Push();

		var instance = prefabScene.Clone();
		var childA = GetChild( instance, "ChildA" );
		var childB = GetChild( instance, "ChildB" );

		// Child properties
		childA.Name = "ChildA_Renamed";
		childA.LocalPosition = new Vector3( 50, 0, 0 );
		StringAssert.Contains( AssertTargetedRefreshMatchesFull( instance, childA ), "ChildA_Renamed" );

		// The instance root's own properties
		instance.Name = "Renamed Instance";
		instance.WorldPosition = new Vector3( 0, 0, 100 );
		StringAssert.Contains( AssertTargetedRefreshMatchesFull( instance, instance ), "Renamed Instance" );

		// Overrides on other objects, components and added objects are left as they are
		childA.Components.Get<PrefabInstanceStatComponent>().Number = 42;
		var added = new GameObject( true, "AddedChild" );
		added.SetParent( instance );
		instance.PrefabInstance.RefreshPatch();

		childB.Name = "ChildB_Renamed";
		var withOthers = AssertTargetedRefreshMatchesFull( instance, childB );
		StringAssert.Contains( withOthers, "ChildB_Renamed" );
		StringAssert.Contains( withOthers, "AddedChild" );
		StringAssert.Contains( withOthers, ".Number=42" );

		// Setting a property back to the prefab's value drops its override
		childA.Name = "ChildA";
		var reverted = AssertTargetedRefreshMatchesFull( instance, childA );
		Assert.IsFalse( reverted.Contains( "ChildA_Renamed" ) );
		Assert.IsFalse( instance.PrefabInstance.IsPropertyOverridden( childA, "Name" ) );
	}

	[TestMethod]
	public void PropertyRefreshOfNestedRootMatchesFullRefresh()
	{
		using var registration = RegisterNestedPrefabs( out var outerScene );

		var scene = new Scene();
		using var sceneScope = scene.Push();

		var outer = outerScene.Clone();
		var nested = outer.Children[0];
		Assert.IsTrue( nested.IsNestedPrefabInstanceRoot );

		nested.Name = "InnerRoot_Renamed";
		nested.LocalPosition = new Vector3( 10, 0, 0 );

		StringAssert.Contains( AssertTargetedRefreshMatchesFull( outer, nested ), "InnerRoot_Renamed" );
	}
}
