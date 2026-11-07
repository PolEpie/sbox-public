using System.Buffers;
using Sandbox.Engine;
using Facepunch.ActionGraphs;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sandbox;

public abstract partial class Component : BytePack.ISerializer
{
	public JsonNode Serialize( GameObject.SerializeOptions options = null )
	{
		var isSceneForNetwork = options?.SceneForNetwork ?? false;
		var isSingleNetworkObject = options?.SingleNetworkObject ?? false;
		var isNetworked = isSceneForNetwork || isSingleNetworkObject;
		var skipNulls = options?.SkipNulls ?? false;

		if ( isNetworked && Flags.Contains( ComponentFlags.NotNetworked ) )
			return null;

		if ( Flags.Contains( ComponentFlags.NotCloned ) )
			return null;

		if ( !isNetworked && Flags.Contains( ComponentFlags.NotSaved ) )
			return null;

		var t = GlobalContext.Current.TypeLibrary.GetType( GetType() );
		if ( t is null )
		{
			Log.Warning( $"TypeLibrary could not find {GetType()}" );
			return null;
		}

		using var sceneScope = Scene.Push();

		// Will omit serializing the target of embedded Action Graphs
		using var targetScope = ActionGraph.PushTarget( InputDefinition.Target( typeof( GameObject ) ) );

		var json = new JsonObject
		{
			{ JsonKeys.Type, t.SerializedName },
			{ JsonKeys.Id, Id },
			{ JsonKeys.Enabled, Enabled },
			{ JsonKeys.Flags, (long)Flags }
		};

		if ( (isSceneForNetwork || isSingleNetworkObject) && this is INetworkSnapshot sw )
		{
			var writer = ByteStream.Create( 8 );
			sw.WriteSnapshot( ref writer );

			if ( writer.Length > 0 )
			{
				json[JsonKeys.Snapshot] = Json.ToNode( writer.ToArray() );
			}

			writer.Dispose();
		}

		if ( ComponentVersion != 0 ) json[JsonKeys.Version] = ComponentVersion;

		var values = ValueBuffers.Rent();
		try
		{
			foreach ( var member in ReflectionQueryCache.OrderedSerializableMembers( GetType() ) )
			{
				if ( member is FieldDescription field )
				{
					var value = field.GetValue( this );
					if ( skipNulls && value is null ) continue;
					try
					{
						values.Add( json, field.Name, value, field.FieldType );
					}
					catch ( System.Exception e )
					{
						Log.Warning( e, $"Error when serializing {this}.{field.Name} ({e.Message})\n{value}" );
					}
				}
				else if ( member is PropertyDescription prop )
				{
					var value = prop.GetValue( this );
					if ( skipNulls && value is null ) continue;
					try
					{
						values.Add( json, prop.Name, value, prop.PropertyType );
					}
					catch ( System.Exception e )
					{
						Log.Warning( e, $"Error when serializing {this}.{prop.Name} ({e.Message})\n{value}" );
					}
				}
			}

			values.Fill( json );
		}
		finally
		{
			ValueBuffers.Return( values );
		}

		return json;
	}

	/// <summary>
	/// Serializes a component's member values into one buffer and parses it once, instead of a buffer, a parse and a
	/// document per member (<see cref="Json.ToNode(object, Type)"/>). Each member gets the same kind of node that would
	/// have made, backed by the shared document.
	/// </summary>
	sealed class ValueBuffers : IBufferWriter<byte>
	{
		// Reused per thread; taken while in use, so a member that serializes a component gets its own.
		[ThreadStatic] static ValueBuffers _cached;

		// The member values as a JSON array, written straight from the serializer. Rented like the serializer's own buffer.
		byte[] _bytes = ArrayPool<byte>.Shared.Rent( 4096 );
		int _count;
		readonly List<string> _names = new();
		Utf8JsonWriter _writer;

		public static ValueBuffers Rent()
		{
			var buffers = _cached ?? new ValueBuffers();
			_cached = null;
			return buffers;
		}

		public static void Return( ValueBuffers buffers )
		{
			// Don't keep a large buffer (a big mesh's base64) for the thread's lifetime.
			if ( buffers._bytes.Length > 1 << 20 )
			{
				ArrayPool<byte>.Shared.Return( buffers._bytes );
				return;
			}

			buffers._count = 0;
			buffers._names.Clear();
			_cached = buffers;
		}

		/// <summary>
		/// Adds <paramref name="name"/> to <paramref name="json"/>, with its value filled in by <see cref="Fill"/>.
		/// Throws, adding nothing, if the value can't be serialized.
		/// </summary>
		public void Add( JsonObject json, string name, object value, Type type )
		{
			if ( value is IJsonPopulator jss )
			{
				json.Add( name, jss.Serialize() );
				return;
			}

			var start = _count;
			try
			{
				GetSpan( 1 )[0] = (byte)(_names.Count == 0 ? '[' : ',');
				Advance( 1 );

				var options = Json.options;
				if ( _writer is null )
					_writer = new Utf8JsonWriter( this, new JsonWriterOptions { Encoder = options.Encoder, MaxDepth = options.MaxDepth } );
				else
					_writer.Reset( this );

				System.Text.Json.JsonSerializer.Serialize( _writer, value, type, options );
				_writer.Flush();
				if ( _count == start + 1 ) throw new JsonException( "No value was written" );

				json.Add( name, null );
				_names.Add( name );
			}
			catch
			{
				_count = start;
				throw;
			}
		}

		public void Fill( JsonObject json )
		{
			if ( _names.Count == 0 ) return;

			GetSpan( 1 )[0] = (byte)']';
			Advance( 1 );

			var options = Json.options;
			var reader = new Utf8JsonReader( _bytes.AsSpan( 0, _count ), new JsonReaderOptions { MaxDepth = options.MaxDepth + 1 } );
			var values = JsonElement.ParseValue( ref reader );
			var nodeOptions = new JsonNodeOptions { PropertyNameCaseInsensitive = options.PropertyNameCaseInsensitive };

			var i = 0;
			foreach ( var value in values.EnumerateArray() )
			{
				json[_names[i++]] = value.ValueKind switch
				{
					JsonValueKind.Object => JsonObject.Create( value, nodeOptions ),
					JsonValueKind.Array => JsonArray.Create( value, nodeOptions ),
					_ => JsonValue.Create( value, nodeOptions ),
				};
			}
		}

		public void Advance( int count ) => _count += count;

		public Memory<byte> GetMemory( int sizeHint = 0 )
		{
			Reserve( sizeHint );
			return _bytes.AsMemory( _count );
		}

		public Span<byte> GetSpan( int sizeHint = 0 )
		{
			Reserve( sizeHint );
			return _bytes.AsSpan( _count );
		}

		void Reserve( int sizeHint )
		{
			sizeHint = Math.Max( sizeHint, 1 );
			if ( _bytes.Length - _count >= sizeHint ) return;

			var bigger = ArrayPool<byte>.Shared.Rent( Math.Max( _bytes.Length * 2, _count + sizeHint ) );
			_bytes.AsSpan( 0, _count ).CopyTo( bigger );
			ArrayPool<byte>.Shared.Return( _bytes );
			_bytes = bigger;
		}
	}

	JsonObject jsonData;
	// When true, properties absent from jsonData are explicitly cleared in PostDeserialize
	// rather than being left at their current value.
	bool _clearAbsentFields;

	public void Deserialize( JsonObject node ) => DeserializeInternal( node, false );

	/// <summary>
	/// When <paramref name="clearAbsentFields"/> is <see langword="true"/>, properties absent from
	/// <paramref name="node"/> (e.g. omitted by <see cref="GameObject.SerializeOptions.SkipNulls"/>)
	/// are explicitly cleared to null/default rather than left at their existing value.
	/// Used for all network receive paths.
	/// </summary>
	internal void DeserializeInternal( JsonObject node, bool clearAbsentFields )
	{
		var serializedVersion = (int)(node[JsonKeys.Version] ?? 0);
		if ( serializedVersion < ComponentVersion )
		{
			//Log.Warning( $"{this} needs an API update, running upgraders" );
			JsonUpgrader.Upgrade( serializedVersion, node, GetType() );
		}

		if ( node.TryGetPropertyValue( JsonKeys.Id, out var id ) )
		{
			Id = (Guid)id;
		}

		DeserializeFlags( node );

		if ( node.TryGetPropertyValue( JsonKeys.Snapshot, out var snapshotNode ) && this is INetworkSnapshot sw )
		{
			var data = snapshotNode.Deserialize<byte[]>();
			var reader = ByteStream.CreateReader( data );
			sw.ReadSnapshot( ref reader );
			reader.Dispose();
		}

		jsonData = node;
		_clearAbsentFields = clearAbsentFields;

		InitializeComponent();

		Enabled = (bool)(jsonData[JsonKeys.Enabled] ?? true);
	}

	private void DeserializeFlags( JsonObject node )
	{
		if ( !node.TryGetPropertyValue( JsonKeys.Flags, out var inFlagNode ) )
			return;

		var inFlags = (ComponentFlags)(long)inFlagNode;

		const ComponentFlags savedFlags = ComponentFlags.ShowAdvancedProperties;

		Flags = (Flags & ~savedFlags) | (inFlags & savedFlags);
	}

	internal void PostDeserialize()
	{
		if ( jsonData is null )
			return;

		using var sceneScope = Scene.Push();

		// Inject the host object into embedded Action Graphs
		using var targetScope = ActionGraph.PushTarget( InputDefinition.Target( typeof( GameObject ), GameObject ) );

		try
		{
			foreach ( var field in ReflectionQueryCache.OrderedSerializableMembers( GetType() ) )
			{
				// Skip fields that are not PRESENT in json data.
				// We want those to be set to code defined defaults, when deserializing older versions from disk.
				//
				// Except, during a network refresh with SkipNulls, a missing key means the sender had null for
				// that property, so we explicitly clear it.
				if ( !jsonData.ContainsKey( field.Name ) )
				{
					if ( _clearAbsentFields )
					{
						DeserializeProperty( field, null );
					}
					continue;
				}

				// This also includes fields that are null
				var v = jsonData[field.Name];

				try
				{
					DeserializeProperty( field, v );
				}
				catch ( System.Exception e )
				{
					Log.Warning( e, $"Error when deserializing {this}.{field.Name} ({e.Message})\n{v}" );
				}
			}
		}
		finally
		{
			jsonData = null;
			_clearAbsentFields = false;
		}

		CheckRequireComponent();
	}

	/// <summary>
	/// Rewrites members marked with <see cref="SyncFlags.FromHost"/> in a component JSON payload
	/// to this component's current values, so incoming refresh data cannot overwrite host-authoritative state.
	/// </summary>
	internal void PreserveFromHostSyncMembers( JsonObject componentJson )
	{
		foreach ( var propertyAndAttribute in ReflectionQueryCache.SyncProperties( GetType() ) )
		{
			if ( !propertyAndAttribute.Attribute.Flags.HasFlag( SyncFlags.FromHost ) )
				continue;

			try
			{
				var currentValue = propertyAndAttribute.Property.GetValue( this );
				componentJson[propertyAndAttribute.Property.Name] = Json.ToNode( currentValue, propertyAndAttribute.Property.PropertyType );
			}
			catch
			{
				componentJson.Remove( propertyAndAttribute.Property.Name );
			}
		}
	}

	/// <summary>
	/// Deserialize this component as per <see cref="Deserialize"/> but update <see cref="GameObject"/> and <see cref="Component"/> property
	/// references immediately instead of having them deferred.
	/// </summary>
	public void DeserializeImmediately( JsonObject node )
	{
		DeserializeInternal( node, false );
		PostDeserialize();
	}

	[ConVar( "serialization_warn_time", ConVarFlags.Protected, Help = "Warn if deserializing a component property takes longer than this number of milliseconds." )]
	internal static int DeserializeTimeWarnThreshold { get; set; }

	private void StopTiming( MemberDescription member, Type type, FastTimer timer )
	{
		if ( DeserializeTimeWarnThreshold <= 0 ) return;
		if ( Scene.IsLoading ) return;

		var duration = timer.ElapsedMilliSeconds;

		if ( duration > DeserializeTimeWarnThreshold )
		{
			Log.Warning( $"Deserializing {type.Name} {member.TypeDescription.Name}.{member.Name} took {duration:F1}ms!" );
		}
	}

	internal void DeserializeProperty( MemberDescription member, JsonNode node )
	{
		var startTime = FastTimer.StartNew();

		if ( member is PropertyDescription prop )
		{
			if ( !prop.IsSetMethodPublic )
			{
				// By getting the property from the declaring class, we can allow setting
				// properties with a private setter.
				var declaringType = GlobalContext.Current.TypeLibrary.GetType( prop.MemberInfo.DeclaringType );
				prop = declaringType.GetProperty( member.Name ) ?? prop;
			}

			if ( prop.PropertyType.IsAssignableTo( typeof( IJsonPopulator ) ) )
			{
				var value = prop.GetValue( this );
				if ( value == null ) value = Activator.CreateInstance( prop.PropertyType );

				if ( value is IJsonPopulator jsonConvert )
				{
					jsonConvert.Deserialize( node );
				}

				if ( prop.PropertyType.IsValueType )
				{
					prop.SetValue( this, value );
				}
			}
			else
			{
				prop.SetValue( this, Json.FromNode( node, prop.PropertyType ) );
			}

			StopTiming( prop, prop.PropertyType, startTime );
			return;
		}

		if ( member is FieldDescription field )
		{
			if ( !field.IsPublic )
			{
				// By getting the field from the declaring class, we can allow setting
				// private fields.
				var declaringType = GlobalContext.Current.TypeLibrary.GetType( field.MemberInfo.DeclaringType );
				field = declaringType.GetField( member.Name ) ?? field;
			}

			if ( field.FieldType.IsAssignableTo( typeof( IJsonPopulator ) ) )
			{
				var value = field.GetValue( this );
				if ( value == null ) value = Activator.CreateInstance( field.FieldType );

				if ( value is IJsonPopulator jsonConvert )
				{
					jsonConvert.Deserialize( node );
				}

				if ( field.FieldType.IsValueType )
				{
					field.SetValue( this, value );
				}
			}
			else
			{
				field.SetValue( this, Json.FromNode( node, field.FieldType ) );
			}

			StopTiming( field, field.FieldType, startTime );
			return;
		}
	}

	static object BytePack.ISerializer.BytePackRead( ref ByteStream bs, Type targetType )
	{
		var id = bs.Read<Guid>();

		if ( !Game.ActiveScene.IsValid() ) return default;
		return Game.ActiveScene.Directory.FindComponentByGuid( id );
	}

	static void BytePack.ISerializer.BytePackWrite( object value, ref ByteStream bs )
	{
		if ( value is not Component component )
		{
			bs.Write( Guid.Empty );
			return;
		}

		bs.Write( component.Id );
	}

	public static object JsonRead( ref Utf8JsonReader reader, Type targetType )
	{
		if ( reader.TokenType == JsonTokenType.StartObject )
		{
			var compRef = JsonSerializer.Deserialize<ComponentReference>( ref reader );

			return compRef.Resolve( Game.ActiveScene, targetType, warn: true );
		}

		//
		// Legacy way
		//
		if ( reader.TryGetGuid( out Guid guid ) )
		{
			if ( !Game.ActiveScene.IsValid() )
			{
				Log.Warning( "Tried to read component - but active scene was null!" );
				return null;
			}

			var go = Game.ActiveScene.Directory.FindByGuid( guid );

			if ( go is null )
				throw new( $"GameObject {guid} was not found" );

			var component = go.Components.Get( targetType, FindMode.EverythingInSelf );
			if ( component is null )
				throw new( $"Component {targetType} was not found on {go}" );

			return component;
		}

		return null;
	}

	public static void JsonWrite( object value, Utf8JsonWriter writer )
	{
		if ( value is not Component component )
			throw new NotImplementedException();

		if ( !component.IsValid )
		{
			writer.WriteNullValue();
			return;
		}

		JsonSerializer.Serialize( writer, ComponentReference.FromInstance( component ), Json.options );
	}

	/// <summary>
	/// Json Keys used for serialization and deserialization of Components.
	/// Kept here so they are easier to change, and we are less susceptible to typos.
	/// </summary>
	internal static class JsonKeys
	{
		internal const string Id = "__guid";
		internal const string Flags = "Flags";
		internal const string Version = "__version";
		internal const string Type = "__type";
		internal const string Snapshot = "__snapshot";
		internal const string Enabled = "__enabled";
	}

}
