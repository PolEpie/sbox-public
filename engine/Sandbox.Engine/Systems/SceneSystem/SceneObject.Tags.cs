using System.Collections.Frozen;

namespace Sandbox
{
	public partial class SceneObject
	{
		/// <summary>
		/// List of tags for this scene object.
		/// </summary>
		public ITagSet Tags { get; private set; }

		/// <summary>The tags as string tokens, in the order they were added (as native's <c>CSceneObject::m_tags</c>).</summary>
		internal IReadOnlyList<uint> TagTokens => ((Internal.SceneObjectTags)Tags).Tokens;
	}

	namespace Internal
	{
		/// <summary>
		/// A scene object's tags, kept in managed code. The first time anything asks they're read from the native object, which
		/// has those native code gave it by then (a map's world geometry); from then on managed code owns them. A native twin, if
		/// there is one, still gets every change.
		/// </summary>
		internal class SceneObjectTags : ITagSet
		{
			readonly SceneObject SceneObject;
			readonly List<uint> tokens = new();
			bool read;

			internal SceneObjectTags( SceneObject sceneObject )
			{
				SceneObject = sceneObject;
			}

			/// <summary>The tags as string tokens, in the order they were added.</summary>
			internal List<uint> Tokens
			{
				get
				{
					if ( !read && SceneObject.HasTwin )
					{
						read = true;
						var count = SceneObject.native.GetTagCount();
						for ( var i = 0; i < count; i++ ) tokens.Add( SceneObject.native.GetTagAt( i ) );
					}

					return tokens;
				}
			}

			public override void Add( string tag )
			{
				if ( !SceneObject.IsValid() ) return;
				var token = StringToken.FindOrCreate( tag );
				if ( Tokens.Contains( token ) ) return;
				tokens.Add( token );
				if ( SceneObject.HasTwin ) SceneObject.native.AddTag( token );
				SceneObject.TagsChanged();
			}

			public override IEnumerable<string> TryGetAll()
			{
				if ( !SceneObject.IsValid() )
					yield break;

				foreach ( var token in Tokens.ToArray() )
					yield return StringToken.GetValue( token );
			}

			/// <summary>
			/// Try to get all tags in the set.
			/// </summary>
			public override IReadOnlySet<uint> GetTokens()
			{
				return Tokens.ToFrozenSet();
			}

			public override bool Has( string tag )
			{
				if ( !SceneObject.IsValid() ) return false;
				return Tokens.Contains( StringToken.FindOrCreate( tag ) );
			}

			public override void Remove( string tag )
			{
				if ( !SceneObject.IsValid() ) return;
				var token = StringToken.FindOrCreate( tag );
				if ( !Tokens.Remove( token ) ) return;
				if ( SceneObject.HasTwin ) SceneObject.native.RemoveTag( token );
				SceneObject.TagsChanged();
			}

			public override void RemoveAll()
			{
				if ( !SceneObject.IsValid() ) return;
				read = true;
				tokens.Clear();
				if ( SceneObject.HasTwin ) SceneObject.native.RemoveAllTags();
				SceneObject.TagsChanged();
			}
		}
	}
}
