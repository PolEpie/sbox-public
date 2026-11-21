using Sandbox.Engine.Shaders;
using Sandbox.Tasks;
using System.IO;
using System.Linq;

namespace EngineTests;

/// <summary>
/// A stage has four specialization constant ids for <c>SpecializedConstant()</c> attributes, and the engine's includes take
/// some of them (DDGI, light modulators, shadow filtering, tools views). One the ids don't reach stays a constant buffer
/// member the material fills, so a package shader with a few of its own still recompiles against today's includes
/// (meteorlab.veloxtech's <c>snow_cover</c> declares <c>VtWinterOn</c>).
/// </summary>
[TestClass]
public class ShaderSpecializedConstantsTest
{
	const string Source = """
		HEADER
		{
			Description = "More SpecializedConstant() attributes than ids";
		}

		MODES
		{
			Default();
		}

		FEATURES
		{
		}

		COMMON
		{
			#include "system.fxc"
		}

		VS
		{
			#include "common.fxc"

			struct VS_INPUT
			{
				float3 vPositionOs : POSITION < Semantic( PosXyz ); >;
			};

			struct VS_OUTPUT
			{
				float4 vPositionPs : SV_Position;
			};

			VS_OUTPUT MainVs( VS_INPUT i )
			{
				VS_OUTPUT o;
				o.vPositionPs = float4( i.vPositionOs.xyz, 1.0 );
				return o;
			}
		}

		PS
		{
			#include "common.fxc"

			struct PS_INPUT
			{
				float4 vPositionPs : SV_Position;
			};

			int SpecA < Attribute( "SpecA" ); Default( 0 ); SpecializedConstant(); >;
			int SpecB < Attribute( "SpecB" ); Default( 0 ); SpecializedConstant(); >;
			uint SpecC < Attribute( "SpecC" ); Default( 0 ); SpecializedConstant(); >;
			bool SpecD < Attribute( "SpecD" ); Default( 0 ); SpecializedConstant(); >;
			int SpecE < Attribute( "SpecE" ); Default( 0 ); SpecializedConstant(); >;

			float4 MainPs( PS_INPUT i ) : SV_Target0
			{
				float4 c = float4( 0, 0, 0, 1 );
				[branch] if ( SpecA != 0 ) c.r += 0.25;
				[branch] if ( SpecB != 0 ) c.g += 0.25;
				[branch] if ( SpecC != 0 ) c.b += 0.25;
				[branch] if ( SpecD ) c.r += 0.5;
				[branch] if ( SpecE != 0 ) c.g += 0.5;
				return c;
			}
		}
		""";

	[TestMethod]
	public void MoreThanTheIdsCompile()
	{
		var folder = EngineFileSystem.Root.GetFullPath( "/.source2/shadercache/compiled/tests" );
		Directory.CreateDirectory( folder );
		var path = Path.Combine( folder, "specialized_constants.shader" );
		File.WriteAllText( path, Source );

		try
		{
			var result = AsyncPump.RunSynchronously( () => ShaderCompile.Compile( path, "tests/specialized_constants.shader", new ShaderCompileOptions { ForceRecompile = true, ConsoleOutput = false }, default ) );
			var output = string.Join( "\n", result.Programs.SelectMany( p => p.Output ?? [] ) );
			Assert.IsTrue( result.Success, output );
			Assert.IsTrue( result.Programs.All( p => p.Success ), output );
		}
		finally
		{
			File.Delete( path );
			File.Delete( path + "_c" );
		}
	}
}
