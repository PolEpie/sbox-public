/// <summary>
/// Which button pictures a pad draws. SDL answers a *type*, and a driver or compatibility layer
/// will happily answer "Xbox" for a DualSense — measured on a pad SDL still named `#controller_ps5`
/// while reporting the Xbox glyph set, which drew Xbox glyphs on every prompt in the game. The
/// name is the second opinion, and it wins where it names a family.
/// </summary>
/// <remarks>
/// The set is compared by name: `GameControllerGlyphSet` is internal to NativeEngine, so a public
/// test signature cannot take one.
/// </remarks>
[TestClass]
public class ControllerGlyphSetTests
{
	/// <summary>The engine's own `#controller_*` tokens, which is what SDL hands back here.</summary>
	[TestMethod]
	[DataRow( "#controller_ps5", "PlayStation" )]
	[DataRow( "#controller_ps4", "PlayStation" )]
	[DataRow( "#controller_switch_pro", "Switch" )]
	[DataRow( "#controller_steam", "Steam" )]
	[DataRow( "#controller_xbox_one", "Xbox" )]
	public void ATokenNamesItsFamily( string name, string expected )
	{
		Assert.AreEqual( expected, Controller.GlyphSetFromName( name )?.ToString() );
	}

	/// <summary>SDL's product strings, for a platform that hands over the real name instead.</summary>
	[TestMethod]
	[DataRow( "Sony DualSense Wireless Controller", "PlayStation" )]
	[DataRow( "PS4 Controller", "PlayStation" )]
	[DataRow( "Nintendo Switch Pro Controller", "Switch" )]
	[DataRow( "Joy-Con (L)", "Switch" )]
	[DataRow( "Steam Controller", "Steam" )]
	[DataRow( "Xbox Wireless Controller", "Xbox" )]
	[DataRow( "XInput Controller #1", "Xbox" )]
	public void AProductStringNamesItsFamily( string name, string expected )
	{
		Assert.AreEqual( expected, Controller.GlyphSetFromName( name )?.ToString() );
	}

	/// <summary>
	/// A name that gives nothing away answers null, so the device's own reported set is what
	/// stands — the name is a correction, never a replacement.
	/// </summary>
	[TestMethod]
	[DataRow( "Generic USB Joystick" )]
	[DataRow( "" )]
	[DataRow( "   " )]
	[DataRow( null )]
	public void AnUnknownNameDefersToTheDevice( string name )
	{
		Assert.IsNull( Controller.GlyphSetFromName( name ) );
	}

	/// <summary>Case is the platform's business, not ours.</summary>
	[TestMethod]
	public void MatchingIgnoresCase()
	{
		Assert.AreEqual( "PlayStation", Controller.GlyphSetFromName( "SONY DUALSENSE" )?.ToString() );
		Assert.AreEqual( "Switch", Controller.GlyphSetFromName( "NINTENDO Switch Pro" )?.ToString() );
	}
}
