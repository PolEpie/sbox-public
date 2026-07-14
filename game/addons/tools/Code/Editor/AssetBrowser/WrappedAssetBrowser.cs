using Sandbox.Mounting;

namespace Editor;

public class WrappedAssetBrowser : Widget
{
	public LocalAssetBrowser Local { get; private set; }
	public MountsAssetBrowser Mounts { get; private set; }
	public CloudAssetBrowser Cloud { get; private set; }

	private VerticalTabWidget Tabs;

	public WrappedAssetBrowser( Widget parent, List<AssetType> assetTypeFilters ) : base( parent )
	{
		MinimumSize = new( 100, 100 );

		Layout = Layout.Row();

		Local = new LocalAssetBrowser( this, assetTypeFilters );
		Mounts = new MountsAssetBrowser( this, assetTypeFilters );
		Cloud = new CloudAssetBrowser( this, assetTypeFilters );

		Tabs = Layout.Add( new VerticalTabWidget( this ) );
		Tabs.AddPage( "Local", "folder", Local, "Local" );
		Tabs.AddPage( "Cloud", "cloud", Cloud, "Cloud" );
		Tabs.AddPage( "Mounts", "museum", Mounts, "Mounts" );
	}

	public AssetBrowser GetBrowser( string path )
	{
		if ( MountUtility.IsMountPath( path ) )
			return Mounts;

		return Local;
	}

	public AssetBrowser GetBrowser( Asset asset ) => GetBrowser( asset.Path );
	public AssetBrowser GetBrowser( AssetEntry asset ) => GetBrowser( asset.AbsolutePath );

	public void SwitchTo( Widget widget ) => Tabs.SetPage( widget );

	/// <summary>
	/// Create a new browser in a new dock with the given path opened, splitting this dock's
	/// space on the given side. <see cref="DockArea.Center"/> adds it as a tab of this dock instead.
	/// </summary>
	public MainAssetBrowser OpenInNewDock( string path, DockArea area )
	{
		var dockManager = EditorWindow.DockManager;

		// the main dock owns the plain "Asset Browser" name, side docks count up from 2
		var index = 2;
		while ( dockManager.FindDockWidget( $"Asset Browser {index}" ) is not null ) index++;

		var browser = new MainAssetBrowser( null );
		browser.DeleteOnClose = true;

		var dock = dockManager.CreateDockWidget( $"Asset Browser {index}", "folder_open", browser );

		// split our own dock area rather than the whole window, so the rest of the layout is untouched
		dockManager.AddDock( dock, area, dockManager.FindDockWidget( this ) );
		dock.SetAsCurrentTab();

		var page = browser.GetBrowser( path );
		browser.SwitchTo( page );
		page.NavigateTo( path );

		// title the dock by where the browser is looking, rather than "Asset Browser N"
		browser._titleDock = dock;

		return browser;
	}

	DockWidget _titleDock;

	/// <summary>
	/// A short title describing where this browser is currently looking.
	/// </summary>
	string CurrentTitle => Tabs?.CurrentPage switch
	{
		AssetBrowser browser => browser.CurrentLocation?.Name ?? "Assets",
		CloudAssetBrowser => "Cloud",
		_ => "Assets",
	};

	[EditorEvent.Frame]
	void UpdateDockTitle()
	{
		if ( !_titleDock.IsValid() ) return;

		// only the visible title changes - the dock keeps its "Asset Browser N" name for lookups
		var title = CurrentTitle;
		if ( _titleDock.WindowTitle == title ) return;

		_titleDock.WindowTitle = title;
	}

	/// <summary>
	/// Finds the browser hosting the given widget, if any.
	/// </summary>
	internal static WrappedAssetBrowser Find( Widget widget )
	{
		for ( var w = widget; w.IsValid(); w = w.Parent )
		{
			if ( w is WrappedAssetBrowser browser )
				return browser;
		}

		return null;
	}
}
