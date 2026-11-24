using Borealis.Gpu;

namespace BorealisGpuTests;

/// <summary>
/// The driver's address binding reports (VK_EXT_device_address_binding_report, with <see cref="DeviceOptions.ProgressMarkers"/>):
/// the device-loss report names the resource a fault address lies in, and one freed before the fault by the name it had.
/// </summary>
[TestClass]
public class AddressBindingTests
{
	public TestContext TestContext { get; set; }

	[TestMethod]
	public void FaultAddressNamesTheTextureItLiesIn()
	{
		if ( !Device.IsSupported ) Assert.Inconclusive( "No Vulkan loader is installed" );

		Device device = null;
		try
		{
			try
			{
				device = Device.Create( new DeviceOptions { ApplicationName = "Borealis.Gpu.Test", Presentation = false, ProgressMarkers = true } );
			}
			catch ( GpuException e )
			{
				Assert.Inconclusive( e.Message );
			}

			if ( !device.AddressReports ) Assert.Inconclusive( "The device has no VK_EXT_device_address_binding_report" );

			// Suballocated, as textures are: the driver's report during the bind is named after the texture
			const string Name = "Address binding test texture";
			var texture = device.CreateTexture( TextureInfo.Texture2D( 256, 256, Format.RGBA16Float, name: Name ) );
			var buffer = device.CreateBuffer( new BufferInfo { Size = 1 << 20, Usage = BufferUsage.Storage, Memory = MemoryLocation.Device, Name = "Address binding test buffer" } );
			var ranges = device.BoundRanges();
			TestContext.WriteLine( $"{ranges.Length} ranges bound: {string.Join( ", ", ranges.Select( r => $"{r.Type} '{r.Name}' at 0x{r.Base:X}+0x{r.Size:X}" ) )}" );
			var bound = device.AddressRangeOf( Name, unbound: false );
			Assert.IsNotNull( bound, "the texture's range is known while it's bound" );
			var inside = (bound.Value.Base + bound.Value.Size / 2) & ~0xFFFUL;
			var described = device.DescribeAddress( inside, 0x1000 );
			TestContext.WriteLine( described );
			StringAssert.Contains( described, $"Image '{Name}'" );
			StringAssert.Contains( described, ", bound " );

			// Destroyed, and freed once the frames after it are done: the address is named as taken back from it
			device.Destroy( texture );
			device.Destroy( buffer );
			for ( int i = 0; i < 4; i++ )
			{
				device.BeginFrame();
				device.WaitIdle();
			}

			Assert.IsNull( device.AddressRangeOf( Name, unbound: false ), "the texture's range isn't bound any more" );
			var unbound = device.AddressRangeOf( Name, unbound: true );
			Assert.IsNotNull( unbound, "the texture's range is kept once it's taken back" );
			described = device.DescribeAddress( inside, 0x1000 );
			TestContext.WriteLine( described );
			StringAssert.Contains( described, $"Image '{Name}'" );
			StringAssert.Contains( described, ", taken back " );
		}
		finally
		{
			device?.Dispose();
		}
	}
}
