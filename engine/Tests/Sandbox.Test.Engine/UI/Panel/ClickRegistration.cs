using NativeEngine;
using Sandbox.UI;
using static UITests.UiTesting;

namespace UITests.Panels;

[TestClass]
[DoNotParallelize] // Modifies UI System Global
public class ClickRegistrationTest
{
	[TestMethod]
	public void PressAndReleaseBetweenTwoTicksStillClicks()
	{
		using var surface = new UISurface { Size = new Vector2( 800, 600 ), MouseInside = true };
		var panel = AddBox( surface, 100 );
		int clicks = 0;
		panel.AddEventListener( "onclick", () => clicks++ );

		surface.MousePosition = new Vector2( 150, 50 );
		Frame( surface );
		Frame( surface );

		surface.Input.AddMouseButton( ButtonCode.MouseLeft, true, default );
		surface.Input.AddMouseButton( ButtonCode.MouseLeft, false, default );
		Frame( surface );
		Frame( surface );

		Assert.AreEqual( 1, clicks );
		Assert.IsFalse( surface.Input.MouseStates[0].Pressed );
	}

	[TestMethod]
	public void ReleaseJustOffThePressedPanelStillClicksIt()
	{
		using var surface = new UISurface { Size = new Vector2( 800, 600 ), MouseInside = true };
		var panel = AddBox( surface, 100 );
		int clicks = 0;
		panel.AddEventListener( "onclick", () => clicks++ );

		surface.MousePosition = new Vector2( 198, 50 );
		Frame( surface );
		surface.Input.AddMouseButton( ButtonCode.MouseLeft, true, default );
		Frame( surface );

		// Pressing shrank the panel out from under the cursor
		panel.Style.Width = 90;
		Frame( surface );
		Frame( surface );
		Assert.AreNotSame( panel, surface.Hovered );

		surface.Input.AddMouseButton( ButtonCode.MouseLeft, false, default );
		Frame( surface );

		Assert.AreEqual( 1, clicks );
	}

	[TestMethod]
	public void ReleaseFarFromThePressedPanelDoesNotClick()
	{
		using var surface = new UISurface { Size = new Vector2( 800, 600 ), MouseInside = true };
		var panel = AddBox( surface, 100 );
		int clicks = 0;
		panel.AddEventListener( "onclick", () => clicks++ );

		surface.MousePosition = new Vector2( 198, 50 );
		Frame( surface );
		surface.Input.AddMouseButton( ButtonCode.MouseLeft, true, default );
		Frame( surface );

		surface.MousePosition = new Vector2( 400, 50 );
		Frame( surface );
		surface.Input.AddMouseButton( ButtonCode.MouseLeft, false, default );
		Frame( surface );

		Assert.AreEqual( 0, clicks );
	}

	static Panel AddBox( UISurface surface, float left )
	{
		var panel = surface.Root.AddChild<Panel>();
		panel.Style.PointerEvents = PointerEvents.All;
		panel.Style.Position = PositionMode.Absolute;
		panel.Style.Left = left;
		panel.Style.Top = 0;
		panel.Style.Width = 100;
		panel.Style.Height = 100;
		Frame( surface );
		return panel;
	}
}
