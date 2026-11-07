using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sandbox;
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace HotloadTests;

[TestClass]
public class ReferencePathTests
{
	private static object Root;

	private static FieldInfo RootField => typeof( ReferencePathTests ).GetField( nameof( Root ), BindingFlags.NonPublic | BindingFlags.Static );

	/// <summary>
	/// Paths must not keep an unloaded assembly alive after the hotload that walked it. A hotload walks the outgoing
	/// assembly's fields, then into engine types below them; a kept tree held those fields through the engine types.
	/// </summary>
	[TestMethod]
	public void ClearedPathsReleaseCollectibleMember()
	{
		var type = AddCollectiblePath();

		Hotload.ReferencePath.Clear();

		for ( var i = 0; type.IsAlive && i < 10; i++ )
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
		}

		Assert.IsFalse( type.IsAlive, "The reference paths kept the collectible type, and its assembly, alive" );
	}

	/// <summary>
	/// The same member resolves to the same path while the tree is kept.
	/// </summary>
	[TestMethod]
	public void ChildIsStable()
	{
		var root = Hotload.ReferencePath.GetRoot( RootField );

		Assert.AreSame( root, Hotload.ReferencePath.GetRoot( RootField ) );
		Assert.AreSame( root[RootField], root[RootField] );
		Assert.AreSame( root, root[RootField].Parent );
	}

	/// <summary>
	/// Walks root, a field of a fresh collectible type, then an engine type below it, in its own frame.
	/// </summary>
	[MethodImpl( MethodImplOptions.NoInlining )]
	private static WeakReference AddCollectiblePath()
	{
		var assembly = AssemblyBuilder.DefineDynamicAssembly( new AssemblyName( "ReferencePathCollectible" ), AssemblyBuilderAccess.RunAndCollect );
		var builder = assembly.DefineDynamicModule( "Main" ).DefineType( "Probe", TypeAttributes.Public | TypeAttributes.Class );
		builder.DefineField( "Value", typeof( object ), FieldAttributes.Public );

		var type = builder.CreateType();
		Assert.IsTrue( type.IsCollectible );

		_ = Hotload.ReferencePath.GetRoot( RootField )[type.GetField( "Value" )][typeof( object )];
		return new WeakReference( type );
	}
}
