using System.Reflection;

namespace HotloadTests;

[TestClass]
public class ReferencePathTests : HotloadFixtureTestBase
{
	public static object Root;

	private static FieldInfo RootField => typeof( ReferencePathTests ).GetField( nameof( Root ), BindingFlags.Public | BindingFlags.Static );

	/// <summary>
	/// Paths must not outlive the hotload that walked them: a kept tree pinned unloaded assemblies.
	/// </summary>
	[TestMethod]
	public async Task PathsAreDroppedAfterHotload()
	{
		using var fixture = await CreateFixtureAsync(
			"""
			public class PathsAreDroppedAfterHotload : IHotloadTestFixture
			{
				public int Value;
			}
			""" );

		try
		{
			Root = fixture.OldInstance;

			var path = Sandbox.Hotload.ReferencePath.GetRoot( RootField );

			// The hotload walks Root, so it reuses this path until it clears them
			await fixture.HotloadAsync( configure: x => WatchStatics( x, typeof( ReferencePathTests ) ) );

			Assert.AreNotSame( path, Sandbox.Hotload.ReferencePath.GetRoot( RootField ), "The reference paths were kept after the hotload" );
		}
		finally
		{
			Root = null;
		}
	}
}
