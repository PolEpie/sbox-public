using SkiaSharp;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Topten.RichTextKit;

namespace Sandbox;

file class SKTypefaceEqualityComparer : IEqualityComparer<SKTypeface>
{
	public bool Equals( SKTypeface x, SKTypeface y )
	{
		return x.FamilyName == y.FamilyName
			&& x.FontWeight == y.FontWeight
			&& x.FontSlant == y.FontSlant;
	}

	public int GetHashCode( [DisallowNull] SKTypeface obj )
	{
		return HashCode.Combine( obj.FamilyName, obj.FontWeight, obj.FontSlant );
	}
}

internal class FontManager : FontMapper
{
	public static FontManager Instance = new FontManager();

	static ConcurrentDictionary<int, SKTypeface> LoadedFonts = new();

	static Dictionary<int, SKTypeface> Cache = new();

	public static IEnumerable<string> FontFamilies => LoadedFonts.Values.Select( x => x.FamilyName ).Distinct();

	private void Load( System.IO.Stream stream )
	{
		if ( stream == null ) return;

		var face = SKTypeface.FromStream( stream );
		if ( face is null ) return;

		var hash = HashCode.Combine( face.FamilyName, face.FontWeight, face.FontSlant );

		LoadedFonts[hash] = face;

		Log.Trace( $"Loaded font {face.FamilyName} weight {face.FontWeight}" );
	}

	List<FileWatch> watchers = new();

	public void LoadAll( BaseFileSystem fileSystem )
	{
		// If we're loading new fonts, we may have cached it already
		Cache.Clear();

		var fontFiles = fileSystem.FindFile( "/fonts/", "*.ttf", true )
			.Union( fileSystem.FindFile( "/fonts/", "*.otf", true ) );

		Parallel.ForEach( fontFiles, ( string font ) =>
		{
			Load( fileSystem.OpenRead( $"/fonts/{font}" ) );
		} );

		// Load any new fonts
		var ttfWatch = fileSystem.Watch( $"*.ttf" );
		ttfWatch.OnChanges += ( w ) => OnFontFilesChanged( w, fileSystem );
		var otfWatch = fileSystem.Watch( $"*.otf" );
		otfWatch.OnChanges += ( w ) => OnFontFilesChanged( w, fileSystem );

		watchers.Add( ttfWatch );
		watchers.Add( otfWatch );
	}

	private void OnFontFilesChanged( FileWatch w, BaseFileSystem fs )
	{
		Cache.Clear();

		foreach ( var file in w.Changes )
		{
			Load( fs.OpenRead( file ) );
		}
	}

	private static string GetLegacyName( SKTypeface face )
	{
		// OpenType "name" table tag (see OpenType spec); using a named constant avoids a magic hex literal.
		const uint NameTableTag = 0x6E616D65;

		var data = face.GetTableData( NameTableTag );
		if ( data == null || data.Length < 6 ) return null;
		int count = (data[2] << 8) | data[3];
		int stringOffset = (data[4] << 8) | data[5];
		for ( int i = 0; i < count; i++ )
		{
			int recordOffset = 6 + i * 12;
			int platformID = (data[recordOffset] << 8) | data[recordOffset + 1];
			int encodingID = (data[recordOffset + 2] << 8) | data[recordOffset + 3];
			int nameID = (data[recordOffset + 6] << 8) | data[recordOffset + 7];
			int length = (data[recordOffset + 8] << 8) | data[recordOffset + 9];
			int offset = (data[recordOffset + 10] << 8) | data[recordOffset + 11];
			// Name ID 1, Windows platform (3), Unicode BMP (1)
			if ( nameID == 1 && platformID == 3 && encodingID == 1 )
			{
				return System.Text.Encoding.BigEndianUnicode.GetString( data, stringOffset + offset, length );
			}
		}
		return null;
	}

	/// <summary>
	/// Tries to get the best matching font for the given style.
	/// Will return a matching font family with the closest font weight and optionally slant.
	/// </summary>
	private SKTypeface GetBestTypeface( IStyle style )
	{
		// Must be of same family
		var familyFonts = LoadedFonts.Values.Where( x => string.Equals( x.FamilyName, style.FontFamily, StringComparison.OrdinalIgnoreCase ) );
		if ( !familyFonts.Any() )
			familyFonts = LoadedFonts.Values.Where( x => string.Equals( GetLegacyName( x ), style.FontFamily, StringComparison.OrdinalIgnoreCase ) );
		if ( !familyFonts.Any() ) return null;

		// Get matching slants, if no matching fallback to regular
		var slantFonts = familyFonts.Where( x => x.IsItalic == style.FontItalic );
		if ( slantFonts.Any() ) familyFonts = slantFonts;

		// Finally get the closest font weight
		return familyFonts.Select( x => new { x, distance = Math.Abs( x.FontWeight - style.FontWeight ) } )
			.OrderBy( x => x.distance )
			.First().x;
	}

	public override SKTypeface TypefaceFromStyle( IStyle style, bool ignoreFontVariants )
	{
		var hash = HashCode.Combine( style.FontFamily, style.FontWeight, style.FontItalic );

		lock ( Cache )
		{
			if ( Cache.TryGetValue( hash, out var cachedFace ) ) return cachedFace;
		}

		var f = GetBestTypeface( style );

		// Fallback on system font
		f ??= Default.TypefaceFromStyle( style, ignoreFontVariants );

		lock ( Cache )
		{
			Cache[hash] = f;
		}

		return f;
	}

	public void Reset()
	{
		foreach ( var watcher in watchers )
		{
			watcher.Dispose();
		}
		watchers.Clear();

		foreach ( var (_, font) in LoadedFonts )
		{
			font?.Dispose();
		}

		foreach ( var (_, font) in Cache )
		{
			font?.Dispose();
		}

		LoadedFonts.Clear();
		Cache.Clear();
	}
}

