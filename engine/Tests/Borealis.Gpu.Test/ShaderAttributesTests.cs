using Borealis.Backend;
using Borealis.Source2.Shaders;

namespace BorealisGpuTests;

/// <summary>
/// The packed form of a draw's dynamic combos that <see cref="ShaderDrawer.Variant"/> finds variants by
/// (<see cref="ShaderAttributes.ComboBits"/>): it tells apart what <see cref="ShaderAttributes.ComboKey"/> tells apart.
/// </summary>
[TestClass]
public class ShaderAttributesTests
{
	/// <summary>
	/// A lit shader's dynamic combos (surface_test_rock's): none set packs to zero, each set alone packs differently, the
	/// same values pack the same, and a value past a byte doesn't pack (the caller keys by name).
	/// </summary>
	[TestMethod]
	public void ComboBitsTellCombosApart()
	{
		var shader = ShaderFile.Read( ShaderFileTests.Files.Read( "shaders/surface_test_rock.shader" ), "surface_test_rock" );
		var names = new[] { ShaderProgramType.Vertex, ShaderProgramType.Pixel }.SelectMany( t => shader[t]?.DynamicCombos ?? [] ).Select( c => c.Name ).Distinct().ToArray();
		if ( names.Length == 0 ) Assert.Inconclusive( "surface_test_rock.shader has no dynamic combos" );

		Assert.IsTrue( new ShaderAttributes().ComboBits( shader, out var none ) );
		Assert.AreEqual( 0UL, none );

		var seen = new Dictionary<ulong, string> { [none] = "" };
		foreach ( var name in names )
		{
			var attributes = new ShaderAttributes();
			attributes.SetCombo( name, 1 );
			Assert.IsTrue( attributes.ComboBits( shader, out var bits ), name );
			Assert.IsFalse( seen.ContainsKey( bits ), $"{name} packs as {seen.GetValueOrDefault( bits )} does" );
			seen[bits] = name;

			var again = new ShaderAttributes();
			again.SetCombo( name.ToLowerInvariant(), 1 );
			Assert.IsTrue( again.ComboBits( shader, out var same ) );
			Assert.AreEqual( bits, same, "combo names compare ignoring case" );
			Assert.AreEqual( attributes.ComboKey( shader ), again.ComboKey( shader ) );

			var wide = new ShaderAttributes();
			wide.SetCombo( name, 256 );
			Assert.IsFalse( wide.ComboBits( shader, out _ ), $"{name} = 256 is keyed by name" );
		}
	}

	/// <summary>
	/// Numbers set one at a time reuse an array per name (every reader binds them at once); a merge copies them, so setting
	/// the name again in the source doesn't change what the merged set holds.
	/// </summary>
	[TestMethod]
	public void MergedNumbersDontFollowLaterSets()
	{
		var source = new ShaderAttributes();
		source.Set( "TestNumber", 1.0f );
		source.Set( "TestVector", 1.0f, 2.0f );
		var merged = new ShaderAttributes();
		merged.Merge( source );
		source.Set( "TestNumber", 5.0f );
		source.Set( "TestVector", 5.0f, 6.0f );
		CollectionAssert.AreEqual( new[] { 1.0f }, (float[])merged.Get( "TestNumber" ) );
		CollectionAssert.AreEqual( new[] { 1.0f, 2.0f }, (float[])merged.Get( "TestVector" ) );
		CollectionAssert.AreEqual( new[] { 5.0f }, (float[])source.Get( "TestNumber" ) );
	}

	/// <summary>
	/// A set under another (the engine's block attributes under a draw's) shows through where the draw sets nothing of the same
	/// name: by name, by id, combos, and enumerating, which gives each name once, the draw's value where both set it. Clearing
	/// drops the set under it, and merging or copying takes both.
	/// </summary>
	[TestMethod]
	public void ASetUnderAnotherShowsThrough()
	{
		var block = new ShaderAttributes();
		block.Set( "TestUnderShared", 1.0f );
		block.Set( "TestUnderBlockOnly", 2.0f );
		block.SetCombo( "D_TEST_UNDER", 3 );
		block.SetCombo( "D_TEST_UNDER_REPLACED", 1 );

		var draw = new ShaderAttributes();
		draw.Set( "TestUnderShared", 10.0f );
		draw.Set( "TestUnderDrawOnly", 20.0f );
		draw.SetCombo( "D_TEST_UNDER_REPLACED", 2 );
		draw.Under = block;

		CollectionAssert.AreEqual( new[] { 10.0f }, (float[])draw.Get( "TestUnderShared" ), "the draw's own over the block's" );
		CollectionAssert.AreEqual( new[] { 2.0f }, (float[])draw.Get( AttributeIds.Of( "testunderblockonly" ) ), "the block's, by id" );
		Assert.AreEqual( 3, draw.GetCombo( "D_TEST_UNDER" ) );
		Assert.AreEqual( 2, draw.GetCombo( "D_TEST_UNDER_REPLACED" ) );

		var seen = draw.Values.Select( v => (v.Key, ((float[])v.Value)[0]) ).OrderBy( v => v.Key, StringComparer.Ordinal ).ToArray();
		CollectionAssert.AreEqual( new[] { ("TestUnderBlockOnly", 2.0f), ("TestUnderDrawOnly", 20.0f), ("TestUnderShared", 10.0f) }, seen );

		var merged = new ShaderAttributes();
		merged.Merge( draw );
		CollectionAssert.AreEqual( new[] { 2.0f }, (float[])merged.Get( "TestUnderBlockOnly" ), "merging takes what's under" );
		CollectionAssert.AreEqual( new[] { 10.0f }, (float[])merged.Get( "TestUnderShared" ) );
		Assert.AreEqual( 2, merged.GetCombo( "D_TEST_UNDER_REPLACED" ) );

		// A matrix set by id is the array given (the engine's lowered attributes share theirs), not copied
		var matrix = new float[16];
		draw.SetById( AttributeIds.Of( "TestUnderMatrix" ), matrix );
		Assert.AreSame( matrix, draw.Get( "TestUnderMatrix" ) );

		draw.Clear();
		Assert.IsNull( draw.Under );
		Assert.IsNull( draw.Get( "TestUnderBlockOnly" ) );
		Assert.IsNull( draw.GetCombo( "D_TEST_UNDER" ) );
		Assert.AreEqual( 0, draw.Values.Count() );
	}

	/// <summary>
	/// Values stored by attribute id read back by any spelling of the name and by id; clearing forgets them and the combos;
	/// merging takes the other's over its own; enumerating gives each name once, as first spelled.
	/// </summary>
	[TestMethod]
	public void ValuesByIdBehaveAsByName()
	{
		var attributes = new ShaderAttributes();
		attributes.Set( "TestAttributeOne", 1.0f );
		attributes.Set( "testattributeone", 2.0f );
		attributes.SetData( "TestAttributeTwo", [1, 2, 3] );
		attributes.SetCombo( "D_TEST", 1 );

		CollectionAssert.AreEqual( new[] { 2.0f }, (float[])attributes.Get( "TESTATTRIBUTEONE" ) );
		CollectionAssert.AreEqual( new[] { 2.0f }, (float[])attributes.Get( AttributeIds.Of( "TestAttributeOne" ) ) );
		Assert.AreEqual( 2, attributes.Values.Count(), "one entry a name" );
		Assert.IsNull( attributes.Get( "TestAttributeThree" ) );

		var other = new ShaderAttributes();
		other.Set( "TESTATTRIBUTETWO", 5.0f );
		other.Set( "TestAttributeThree", 3.0f );
		attributes.Merge( other );
		CollectionAssert.AreEqual( new[] { 5.0f }, (float[])attributes.Get( "TestAttributeTwo" ) );
		CollectionAssert.AreEqual( new[] { 3.0f }, (float[])attributes.Get( "TestAttributeThree" ) );
		Assert.AreEqual( 3, attributes.Values.Count() );

		attributes.Clear();
		Assert.IsNull( attributes.Get( "TestAttributeOne" ) );
		Assert.IsNull( attributes.GetCombo( "D_TEST" ) );
		Assert.AreEqual( 0, attributes.Values.Count() );

		attributes.Set( "TestAttributeOne", 4.0f );
		CollectionAssert.AreEqual( new[] { 4.0f }, (float[])attributes.Get( "TestAttributeOne" ) );
		CollectionAssert.AreEqual( new[] { "TestAttributeOne" }, attributes.Values.Select( v => v.Key ).ToArray() );
	}
}

/// <summary>
/// <see cref="ShaderDrawer.WriteConstants"/> writes what the stage's full fill writes (<see cref="ShaderStage.WriteMaterialConstants"/>
/// with the draw's values), though it fills the material's part once and the draw's variables each draw.
/// </summary>
[TestClass]
public class DrawConstantsTests : GpuTest
{
	[TestMethod]
	public void DrawConstantsMatchTheFullFill()
	{
		using var library = new ShaderLibrary( Device, ShaderFileTests.Files );
		using var defaults = new ShaderDefaults( Device );
		using var drawer = new ShaderDrawer( Device, library, defaults );
		var compared = 0;
		foreach ( var path in new[] { "shaders/sprite.shader", "shaders/line.shader", "shaders/tools_generic.shader", "shaders/tools_2d_generic.shader", "shaders/copytexture.shader", "shaders/surface_test_rock.shader" } )
		{
			var shader = library.Load( path );
			if ( shader is null ) continue;
			var variant = drawer.Variant( shader, new ShaderAttributes() );
			foreach ( var stage in new[] { variant?.Vertex, variant?.Pixel }.Where( s => s?.MaterialBuffer.Size > 0 && s.MaterialConstants.Count > 0 ) )
			{
				var attributeVariables = stage.MaterialConstants.Select( c => c.Variable ).Where( v => v.Source == VariableSource.Attribute && !string.IsNullOrEmpty( v.Attribute ) ).ToArray();
				for ( int draw = 0; draw < 3; draw++ )
				{
					var attributes = new ShaderAttributes();
					foreach ( var variable in attributeVariables ) attributes.Set( variable.Attribute, Enumerable.Range( 0, 16 ).Select( i => (float)(draw * 100 + i) ).ToArray() );
					var viewport = new System.Numerics.Vector4( draw, 2 * draw, 640 + draw, 480 );

					var expected = new byte[stage.MaterialBuffer.Size];
					stage.WriteMaterialConstants( expected, variable => variable.Source switch
					{
						VariableSource.Viewport => [viewport.X, viewport.Y, viewport.Z, viewport.W],
						VariableSource.InvViewportSize => [1 / viewport.Z, 1 / viewport.W],
						VariableSource.Attribute => attributes.Get( variable.Attribute ) as float[],
						_ => null,
					} );

					var written = new byte[stage.MaterialBuffer.Size];
					written.AsSpan().Fill( 0xCD );
					drawer.WriteConstants( written, stage, attributes, viewport );
					CollectionAssert.AreEqual( expected, written, $"{path} {stage.Program.Type} draw {draw}" );
					compared++;
				}
			}
		}

		if ( compared == 0 ) Assert.Inconclusive( "none of the shaders is compiled" );
	}
}
