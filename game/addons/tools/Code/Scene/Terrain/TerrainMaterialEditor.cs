using System.IO;
using System.Text.Json;

namespace Editor.TerrainEditor;

class TerrainMaterialEditor : BaseResourceEditor<TerrainMaterial>
{
	SerializedObject Object;
	TerrainMaterial _material;
	Asset _asset;
	RealTimeSince _sinceChange;
	bool _thumbDirty;
	ChannelSummary _summary;
	Texture _sizeBcr, _sizeNho;
	Widget _page;

	// The properties behind each channel tile, in tile order
	static readonly string[][] Pages =
	[
		[nameof( TerrainMaterial.AlbedoImage ), nameof( TerrainMaterial.Metalness )],
		[nameof( TerrainMaterial.NormalImage ), nameof( TerrainMaterial.NormalStrength )],
		[nameof( TerrainMaterial.RoughnessImage ), nameof( TerrainMaterial.Roughness )],
		[nameof( TerrainMaterial.HeightImage ), nameof( TerrainMaterial.HeightBlendStrength ), nameof( TerrainMaterial.DisplacementScale )],
		[nameof( TerrainMaterial.AOImage )],
		[nameof( TerrainMaterial.UVScale ), nameof( TerrainMaterial.NoTiling ), nameof( TerrainMaterial.Surface )],
	];

	// Kept across materials, so stepping through them stays on the same channel
	static int _channel;

	public TerrainMaterialEditor()
	{
		Layout = Layout.Column();
	}

	protected override void Initialize( Asset asset, TerrainMaterial resource )
	{
		Layout.Clear( true );

		Object = resource.GetSerialized();

		// Each channel as its image or what fills it without one, and the size of the generated textures
		_material = resource;
		_asset = asset;

		// Lit sphere of the material as it stands, unsaved edits included, above the channels that make it
		if ( Editor.Assets.AssetPreview.CreateForAsset( asset ) is PreviewTerrainMaterial preview )
		{
			preview.Material = resource;
			Layout.Add( new Editor.Inspectors.AssetPreviewWidget( preview ) { FixedHeight = 220 } );
		}

		_summary = new ChannelSummary( resource ) { Selected = _channel };
		_summary.SelectedChanged = ShowChannel;
		Layout.Add( _summary );
		_sizeBcr = _sizeNho = null;
		UpdateSize();

		_page = new Widget( null ) { Layout = Layout.Column() };
		_page.Layout.Margin = new Sandbox.UI.Margin( 8, 4, 8, 8 );
		Layout.Add( _page );
		Layout.AddStretchCell();
		ShowChannel( _channel );

		Object.OnPropertyChanged += ( p ) =>
		{
			TryAutoFill( p );
			NoteChanged( p );
			_summary.Update();

			// Height's blend and displacement only apply with a height image
			if ( p.Name == nameof( TerrainMaterial.HeightImage ) && _channel == 3 )
				ShowChannel( _channel );

			// The thumbnail is the 3D preview the paint palette shows: rebuild it once the slider settles
			_thumbDirty = true;
			_sinceChange = 0;

			// Cascade to all terrains, feel free to code a TerrainMaterial modified -> TerrainStorage -> Terrain event chain
			UpdateSceneTerrain();
		};
	}

	/// <summary>
	/// Shows the settings of one channel tile under the tiles.
	/// </summary>
	void ShowChannel( int channel )
	{
		_channel = channel;
		_page.Layout.Clear( true );

		var sheet = new ControlSheet();
		foreach ( var name in Pages[channel] )
		{
			if ( channel == 3 && name != nameof( TerrainMaterial.HeightImage ) && !_material.HasHeightTexture )
				continue;

			if ( Object.GetProperty( name ) is not { } property )
				continue;

			sheet.AddRow( property );

			// What the value does, in plain words, under its control rather than hidden in a tooltip
			if ( !string.IsNullOrEmpty( property.Description ) )
			{
				var row = Layout.Row();
				row.AddSpacingCell( 130 );

				var description = new Label( property.Description ) { WordWrap = true };
				description.SetStyles( $"color: {Theme.TextLight.Hex}; font-size: 11px; padding-bottom: 6px;" );
				row.Add( description, 1 );

				sheet.AddLayout( row );
			}
		}

		_page.Layout.Add( sheet );
	}

	/// <summary>
	/// The generated textures are replaced when the material recompiles after an image change: refresh then.
	/// </summary>
	[EditorEvent.Frame]
	void UpdateSize()
	{
		if ( !_summary.IsValid() || _material is null ) return;

		if ( _thumbDirty && _sinceChange > 0.5f )
		{
			_thumbDirty = false;
			_asset?.RebuildThumbnail();
		}

		// Image thumbnails arrive asynchronously
		_summary.Update();

		if ( _sizeBcr == _material.BCRTexture && _sizeNho == _material.NHOTexture ) return;

		_sizeBcr = _material.BCRTexture;
		_sizeNho = _material.NHOTexture;

		static long Bytes( Texture t ) => TextureResidencyInfo.Get( t ) is { } info ? Math.Max( info.Disk.MemorySize, info.Loaded.MemorySize ) : 0;
		static string Describe( Texture t ) => t is null || t.IsError ? "missing" : $"{t.Width}×{t.Height} {t.ImageFormat}";

		var total = Bytes( _sizeBcr ) + Bytes( _sizeNho );
		_summary.Stats = $"Color {Describe( _sizeBcr )}    Normal {Describe( _sizeNho )}    {total / (1024.0 * 1024.0):0.#} MB VRAM";
	}

	void TryAutoFill( SerializedProperty p )
	{
		// Only work from albedo for now
		if ( p.Name != nameof( TerrainMaterial.AlbedoImage ) ) return;

		string albedoPath = p.GetValue<string>();

		var directoryname = Path.GetDirectoryName( albedoPath );
		var filename = Path.GetFileNameWithoutExtension( albedoPath );
		var extension = Path.GetExtension( albedoPath );

		if ( !filename.EndsWith( "_color" ) ) return;

		string baseName = filename.Remove( filename.Length - "_color".Length );

		var tries = new List<(string, string)>()
		{
			( "RoughnessImage", "_rough" ),
			( "RoughnessImage", "_roughness" ),
			( "NormalImage", "_normal" ),
			( "NormalImage", "_normalgl" ),
			( "HeightImage", "_displacement" ),
			( "HeightImage", "_height" ),
			( "AOImage", "_ao" ),
			( "AOImage", "_ambientocclusion" ),
		};

		foreach ( var a in tries )
		{
			var name = $"{directoryname}/{baseName}{a.Item2}{extension}".Replace( "\\", "/" );

			if ( !FileSystem.Content.FileExists( name ) ) continue;

			var property = Object.GetProperty( a.Item1 );
			property.SetValue<string>( name );
		}
	}

	void UpdateSceneTerrain()
	{
		var terrains = SceneEditorSession.All
			.SelectMany( scene => scene.Scene.GetAllComponents<Terrain>() );

		foreach ( var terrain in terrains )
		{
			terrain.UpdateMaterialsBuffer();
		}
	}
}

/// <summary>
/// The material's channels at a glance: the bound image, or the flat value that fills the channel without one.
/// An empty image slot reads like a missing one in the property sheet.
/// </summary>
class ChannelSummary : Widget
{
	const float Tile = 56, Column = 76, Top = 10, Left = 12;

	readonly TerrainMaterial _material;

	public string Stats { get; set; } = "";

	/// <summary>
	/// The channel whose settings show below, highlighted.
	/// </summary>
	public int Selected { get; set; }
	public Action<int> SelectedChanged { get; set; }

	public ChannelSummary( TerrainMaterial material ) : base( null )
	{
		_material = material;
		FixedHeight = 128;
		Cursor = CursorShape.Finger;
	}

	protected override void OnMouseClick( MouseEvent e )
	{
		var column = (int)MathF.Floor( (e.LocalPosition.x - Left + (Column - Tile) / 2) / Column );
		if ( column < 0 || column > 5 || e.LocalPosition.y > Top + Tile + 32 ) return;

		Selected = column;
		SelectedChanged?.Invoke( column );
		Update();
	}

	static bool IsBound( string image ) => !string.IsNullOrEmpty( image ) && image != "materials/default/default_ao.tga";

	protected override void OnPaint()
	{
		var m = _material;
		var x = Left;

		// Selected channel: a panel behind its tile and labels, joining it to the settings below
		Paint.ClearPen();
		Paint.SetBrush( Theme.Blue.WithAlpha( 0.25f ) );
		Paint.DrawRect( new Rect( Left + Selected * Column - 8, Top - 6, Tile + 16, Tile + 42 ), 6 );
		Paint.SetBrush( Theme.Blue );
		Paint.DrawRect( new Rect( Left + Selected * Column - 8, Top + Tile + 34, Tile + 16, 2 ) );

		Channel( ref x, "Color", m.AlbedoImage, Color.White, "image" );
		Channel( ref x, "Normal", m.NormalImage, new Color( 0.5f, 0.5f, 1f ), $"× {m.NormalStrength:0.##}" );

		var defaultRough = m.RoughnessImage == "materials/default/default_rough.tga";
		Channel( ref x, "Roughness", IsBound( m.RoughnessImage ) ? m.RoughnessImage : null, Color.Lerp( Color.Black, Color.White, m.Roughness ),
			IsBound( m.RoughnessImage ) ? $"{(defaultRough ? "default" : "image")} × {m.Roughness:0.##}" : $"flat {m.Roughness:0.##}" );

		Channel( ref x, "Height", IsBound( m.HeightImage ) ? m.HeightImage : null, null, IsBound( m.HeightImage ) ? "image" : "none" );
		Channel( ref x, "AO", IsBound( m.AOImage ) ? m.AOImage : null, null, IsBound( m.AOImage ) ? "image" : "none" );

		// Tiling has no image: an icon for the mode
		var rect = new Rect( x, Top, Tile, Tile );
		Paint.ClearPen();
		Paint.SetBrush( Theme.ControlBackground );
		Paint.DrawRect( rect, 4 );
		Paint.SetPen( m.NoTiling ? Theme.Primary : Theme.TextLight );
		Paint.DrawIcon( rect, m.NoTiling ? "grid_off" : "grid_on", 28, TextFlag.Center );
		Labels( x, "Tiling", $"{(m.NoTiling ? "no tiling" : "repeat")} × {m.UVScale:0.##}" );

		Paint.SetDefaultFont( 8 );
		Paint.SetPen( Theme.TextLight );
		Paint.DrawText( new Rect( Left, Top + Tile + 40, LocalRect.Width - Left * 2, 16 ), Stats, TextFlag.LeftCenter );
	}

	/// <summary>
	/// One tile: the image's thumbnail, or <paramref name="flat"/> when nothing is bound. A null flat color is an
	/// unbound optional channel, drawn dimmed.
	/// </summary>
	void Channel( ref float x, string name, string image, Color? flat, string status )
	{
		var rect = new Rect( x, Top, Tile, Tile );
		Paint.ClearPen();

		if ( image is not null && AssetSystem.FindByPath( image )?.GetAssetThumb() is { } thumb )
		{
			Paint.Draw( rect, thumb );
		}
		else if ( image is null && flat is { } color )
		{
			Paint.SetBrush( color );
			Paint.DrawRect( rect, 4 );
		}
		else
		{
			// Unbound optional channel, or an image that isn't an asset (yet)
			Paint.SetBrush( Theme.ControlBackground );
			Paint.DrawRect( rect, 4 );
			Paint.SetPen( Theme.TextLight.WithAlpha( 0.5f ) );
			Paint.DrawIcon( rect, image is null ? "block" : "image", 24, TextFlag.Center );
		}

		Labels( x, name, status, dim: image is null && flat is null );
		x += Column;
	}

	static void Labels( float x, string name, string status, bool dim = false )
	{
		var alpha = dim ? 0.5f : 1f;

		Paint.SetDefaultFont( 8, 600 );
		Paint.SetPen( Theme.Text.WithAlpha( alpha ) );
		Paint.DrawText( new Rect( x - 8, Top + Tile + 4, Tile + 16, 14 ), name, TextFlag.Center );

		Paint.SetDefaultFont( 7 );
		Paint.SetPen( Theme.TextLight.WithAlpha( alpha ) );
		Paint.DrawText( new Rect( x - 8, Top + Tile + 18, Tile + 16, 14 ), status, TextFlag.Center );
	}
}
