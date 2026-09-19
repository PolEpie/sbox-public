using NativeEngine;

namespace Sandbox;

internal sealed partial class Controller
{
	private Color[] ControllerColors = new[]
	{
		Color.Red,
		Color.Green,
		Color.Blue,
		Color.White
	};


	[ConCmd( "controller_debug", ConVarFlags.Protected )]
	public static void ControllerDebug()
	{
		Log.Info( "---------------------------------------------------------------------------" );
		Log.Info( $"Detected {Controller.All.Count()} controllers" );
		foreach ( var controller in Controller.All )
		{
			Log.Info( $"\t> {controller.Name}, GlyphSet: {controller.GlyphSet}, Handle: {controller.SDLHandle}, Device ID: {controller.DeviceId}" );
			Log.Info( $"\t\t> Color: {controller.LEDColor}, GlyphVendor: {controller.GlyphVendor}" );
			Log.Info( $"\t\t> Accel: {controller.Accelerometer}, Gyro: {controller.Gyroscope}" );
		}
		Log.Info( "---------------------------------------------------------------------------" );
	}

	public int SDLHandle { get; init; }
	public int DeviceId { get; init; }

	/// <summary>
	/// The glyph set for this controller, used for icon/prompt selection.
	/// </summary>
	public GameControllerGlyphSet GlyphSet { get; init; }

	internal Controller( int joystickHandle, int deviceHandle )
	{
		SDLHandle = joystickHandle;
		DeviceId = deviceHandle;
		InputContext = Input.Context.Create( $"GameController:{SDLHandle}" );
		Name = NativeEngine.SDLGameController.GetControllerName( SDLHandle );

		// The name wins where it names a family: a driver or compatibility layer will report a
		// generic (usually Xbox) glyph set for a pad SDL still names as a DualSense or a Pro
		// Controller, and then every prompt in the game draws the wrong buttons.
		GlyphSet = GlyphSetFromName( Name )
			?? NativeEngine.SDLGameController.GetControllerGlyphSet( SDLHandle );

		var id = joystickHandle % 4;
		LEDColor = ControllerColors[id];
	}

	/// <summary>
	/// The glyph set a device name implies, or null when it names no family. Matches both SDL's
	/// product strings ("Sony DualSense", "Nintendo Switch Pro Controller") and the engine's own
	/// `#controller_*` tokens.
	/// </summary>
	internal static GameControllerGlyphSet? GlyphSetFromName( string name )
	{
		if ( string.IsNullOrWhiteSpace( name ) )
			return null;

		var lower = name.ToLowerInvariant();

		if ( Mentions( lower, "playstation", "dualsense", "dualshock", "ps5", "ps4", "ps3" ) )
			return GameControllerGlyphSet.PlayStation;

		if ( Mentions( lower, "nintendo", "switch", "joy-con", "joycon", "joy con" ) )
			return GameControllerGlyphSet.Switch;

		if ( Mentions( lower, "steam" ) )
			return GameControllerGlyphSet.Steam;

		if ( Mentions( lower, "xbox", "xinput", "x360" ) )
			return GameControllerGlyphSet.Xbox;

		return null;
	}

	static bool Mentions( string name, params string[] words )
	{
		foreach ( var word in words )
		{
			if ( name.Contains( word, StringComparison.Ordinal ) )
				return true;
		}

		return false;
	}

	public override string ToString()
	{
		return $"{Name}";
	}

	/// <summary>
	/// Gets a sensor reading from the device's gyroscope (if it has one)
	/// </summary>
	public Angles Gyroscope
	{
		get
		{
			var vec = NativeEngine.SDLGameController.GetGyroscope( SDLHandle );
			return new Angles( vec.x, vec.y, vec.z );
		}
	}

	/// <summary>
	/// Gets a sensor reading from the device's accelerometer (if it has one)
	/// </summary>
	public Vector3 Accelerometer => NativeEngine.SDLGameController.GetAccelerometer( SDLHandle );

	private Color32 ledColor = Color.White;
	/// <summary>
	/// Sets the color of the gamepad if supported
	/// </summary>
	public Color32 LEDColor
	{
		get => ledColor;
		set
		{
			if ( NativeEngine.SDLGameController.SetLEDColor( SDLHandle, value.r, value.g, value.b ) )
			{
				ledColor = value;
			}
		}
	}

	/// <summary>
	/// The name of this controller (e.g. "Xbox Wireless Controller", "Steam Controller")
	/// </summary>
	public string Name { get; init; }

	/// <summary>
	/// Which glyph folder to use for this controller.
	/// Derived from the controller's glyph set.
	/// </summary>
	public string GlyphVendor => GlyphSet switch
	{
		GameControllerGlyphSet.Xbox => "xbox",
		GameControllerGlyphSet.PlayStation => "playstation",
		GameControllerGlyphSet.Switch => "switch",
		GameControllerGlyphSet.Steam => "steam",
		_ => "xbox"
	};

	/// <summary>
	/// Rumbles the controller.
	/// </summary>
	/// <param name="leftMotor">The speed of the left motor, between 0 and 0xFFFF</param>
	/// <param name="rightMotor">The speed of the right motor, between 0 and 0xFFFF</param>
	/// <param name="duration">The duration of the vibration in ms</param>
	public void Rumble( int leftMotor, int rightMotor, int duration )
	{
		// Log.Trace( $"Trying to rumble {leftMotor}, {rightMotor}" );
		NativeEngine.SDLGameController.Rumble( SDLHandle, leftMotor, rightMotor, duration );
	}

	/// <summary>
	/// Rumbles the controller's triggers (if supported)
	/// </summary>
	/// <param name="leftTrigger">The speed of the left trigger motor, between 0 and 0xFFFF</param>
	/// <param name="rightTrigger">The speed of the right trigger motor, between 0 and 0xFFFF</param>
	/// <param name="duration">The duration of the vibration in ms</param>
	public void RumbleTriggers( int leftTrigger, int rightTrigger, int duration )
	{
		// Log.Trace( $"Trying to rumble triggers {leftTrigger}, {rightTrigger}" );
		NativeEngine.SDLGameController.RumbleTriggers( SDLHandle, leftTrigger, rightTrigger, duration );
	}

	/// <summary>
	/// Stops all rumble and haptic events on this controller.
	/// </summary>
	public void StopAllHaptics()
	{
		// Calling with 0 intensity stops any rumbling
		NativeEngine.SDLGameController.Rumble( SDLHandle, 0, 0, 0 );
		NativeEngine.SDLGameController.RumbleTriggers( SDLHandle, 0, 0, 0 );

		ActiveHapticEffect = null;
	}
}
