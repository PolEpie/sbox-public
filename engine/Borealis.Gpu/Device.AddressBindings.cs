using Borealis.Gpu.Memory;
using Borealis.Gpu.Vulkan;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace Borealis.Gpu;

/// <summary>
/// Which resource a faulting GPU address belonged to. With VK_EXT_device_address_binding_report the driver says, through a
/// debug messenger, each GPU address range it binds and each it takes back: AMD's say it of each image bound to memory, as a
/// range of the memory block it's bound in, when it's bound (and when the block goes). The report comes during the
/// <c>vkBind*Memory</c> call, so the device names the range after what that thread is binding, and takes it back itself when
/// it frees that memory. It keeps the ranges bound now and the last ones taken back, so the device-loss report names what a
/// fault address lies in, or lay in before it was freed.
/// </summary>
public sealed unsafe partial class Device
{
	/// <summary>A GPU address range the driver bound, or took back, and what Borealis.Gpu was binding there.</summary>
	internal readonly record struct AddressRange( ulong Base, ulong Size, VkObjectType Type, string Name, bool Internal, ulong Frame, long Ticks );

	/// <summary>How many ranges taken back are kept for the report: a few frames' worth of a busy load.</summary>
	const int UnboundKept = 8192;

	// Ranges bound now, by base address, and the last taken back (a ring)
	readonly Dictionary<ulong, AddressRange> boundRanges = new();
	readonly AddressRange[] unboundRanges = new AddressRange[UnboundKept];
	int unboundNext, unboundCount;
	readonly object addressLock = new();

	// Where each resource the driver reported is on the GPU, by its block and offset, so freeing it takes its range back
	readonly Dictionary<(ulong Memory, ulong Offset), ulong> placedAt = new();

	// What this thread is binding to memory now, which a binding report during the call is about
	[ThreadStatic] static VkObjectType bindingType;
	[ThreadStatic] static string bindingName;
	[ThreadStatic] static ulong bindingMemory, bindingOffset;

	// The messenger the driver's binding reports come to: one of their own, made once the device has the extension
	ulong bindingMessenger;

	/// <summary>Whether the driver reports the address ranges it binds (the device was made with VK_EXT_device_address_binding_report).</summary>
	internal bool AddressReports { get; private set; }

	/// <summary>Have the driver's address binding reports (information, of their own type) come to <see cref="DebugCallback"/>.</summary>
	void ListenForAddressBindings()
	{
		if ( Instance.vkCreateDebugUtilsMessengerEXT == null )
		{
			AddressReports = false;
			return;
		}

		var info = new VkDebugUtilsMessengerCreateInfoEXT
		{
			sType = VkStructureType.DebugUtilsMessengerCreateInfoExt,
			messageSeverity = Vulkan.Vk.SeverityVerbose | Vulkan.Vk.SeverityInfo | Vulkan.Vk.SeverityWarning | Vulkan.Vk.SeverityError,
			messageType = Vulkan.Vk.MessageDeviceAddressBinding,
			pfnUserCallback = &DebugCallback,
			pUserData = (void*)GCHandle.ToIntPtr( self ),
		};

		ulong handle;
		if ( Instance.vkCreateDebugUtilsMessengerEXT( Instance.Instance, &info, null, &handle ) == VkResult.Success ) bindingMessenger = handle;
		else AddressReports = false;
	}

	/// <summary>While a resource is bound to memory: what a binding report during the call names.</summary>
	internal readonly ref struct BindingScope( bool set )
	{
		public void Dispose()
		{
			if ( !set ) return;
			bindingType = VkObjectType.Unknown;
			bindingName = null;
		}
	}

	/// <summary>
	/// Name the binding reports this thread gets, until the scope ends, after a <paramref name="type"/> called <paramref name="name"/>
	/// bound to <paramref name="memory"/>.
	/// </summary>
	BindingScope BindingMemory( VkObjectType type, string name, in Allocation memory )
	{
		if ( !AddressReports ) return default;
		bindingType = type;
		bindingName = name;
		(bindingMemory, bindingOffset) = (memory.Memory, memory.Offset);
		return new BindingScope( true );
	}

	/// <summary>Free a resource's memory, taking back the range the driver reported for it.</summary>
	void FreeMemory( in Allocation memory )
	{
		if ( AddressReports && memory.Block is not null )
		{
			lock ( addressLock )
			{
				if ( placedAt.Remove( (memory.Memory, memory.Offset), out var at ) && boundRanges.Remove( at, out var bound ) ) TakeBack( bound with { Frame = FrameNumber, Ticks = Stopwatch.GetTimestamp() } );
			}
		}

		Allocator.Free( memory );
	}

	/// <summary>Keep a range taken back for the report. Under the lock.</summary>
	void TakeBack( in AddressRange range )
	{
		unboundRanges[unboundNext] = range;
		unboundNext = (unboundNext + 1) % UnboundKept;
		unboundCount = Math.Min( unboundCount + 1, UnboundKept );
	}

	/// <summary>A binding report from the debug messenger: a range bound, or taken back.</summary>
	void NoteAddressBinding( VkDebugUtilsMessengerCallbackDataEXT* data )
	{
		VkDeviceAddressBindingCallbackDataEXT* binding = null;
		for ( var next = (VkBaseStructure*)data->pNext; next != null; next = (VkBaseStructure*)next->pNext )
		{
			if ( next->sType != VkStructureType.DeviceAddressBindingCallbackDataExt ) continue;
			binding = (VkDeviceAddressBindingCallbackDataEXT*)next;
			break;
		}

		if ( binding == null ) return;

		var objects = (VkDebugUtilsObjectNameInfoEXT*)data->pObjects;
		var reported = data->objectCount > 0 && objects != null ? objects[0].objectType : VkObjectType.Unknown;
		var range = new AddressRange( binding->baseAddress, binding->size, bindingName is null ? reported : bindingType, bindingName, (binding->flags & 1) != 0, FrameNumber, Stopwatch.GetTimestamp() );

		lock ( addressLock )
		{
			if ( binding->bindingType == 0 )
			{
				// A range bound where another still was (its resource freed without the device seeing it) takes it back
				if ( boundRanges.Remove( range.Base, out var replaced ) ) TakeBack( replaced );
				boundRanges[range.Base] = range;
				if ( bindingName is not null ) placedAt[(bindingMemory, bindingOffset)] = range.Base;
				return;
			}

			// Taken back by the driver (its block freed): kept with what was bound there
			if ( boundRanges.Remove( range.Base, out var bound ) ) range = range with { Type = bound.Type, Name = bound.Name };
			TakeBack( range );
		}
	}

	/// <summary>Every range bound now, for the tests.</summary>
	internal AddressRange[] BoundRanges()
	{
		lock ( addressLock ) return [.. boundRanges.Values];
	}

	/// <summary>The latest range bound to a resource of that name now, or taken back from one (<paramref name="unbound"/>); null if none. For the tests.</summary>
	internal AddressRange? AddressRangeOf( string name, bool unbound )
	{
		lock ( addressLock )
		{
			if ( !unbound ) return boundRanges.Values.Where( r => r.Name == name ).OrderByDescending( r => r.Ticks ).Cast<AddressRange?>().FirstOrDefault();
			for ( int i = 0; i < unboundCount; i++ )
			{
				var range = unboundRanges[(unboundNext - 1 - i + UnboundKept) % UnboundKept];
				if ( range.Name == name ) return range;
			}

			return null;
		}
	}

	/// <summary>
	/// What the GPU addresses from <paramref name="address"/> to <paramref name="address"/> + <paramref name="precision"/>
	/// lie in: the ranges bound there now and taken back from there lately, newest first.
	/// </summary>
	internal string DescribeAddress( ulong address, ulong precision )
	{
		if ( !AddressReports ) return "no address binding reports (VK_EXT_device_address_binding_report)";

		var end = address + Math.Max( precision, 1 );
		var found = new List<(AddressRange Range, bool Unbound)>();
		lock ( addressLock )
		{
			foreach ( var range in boundRanges.Values )
			{
				if ( range.Base < end && address < range.Base + range.Size ) found.Add( (range, false) );
			}

			for ( int i = 0; i < unboundCount; i++ )
			{
				var range = unboundRanges[(unboundNext - 1 - i + UnboundKept) % UnboundKept];
				if ( range.Base < end && address < range.Base + range.Size ) found.Add( (range, true) );
			}
		}

		if ( found.Count == 0 ) return $"in no range bound now or among the last {unboundCount} taken back (buffers aren't reported)";

		var now = Stopwatch.GetTimestamp();
		var frame = FrameNumber;
		var lines = new List<string>();
		foreach ( var (range, unbound) in found.OrderByDescending( f => f.Range.Ticks ).Take( 6 ) )
		{
			var ago = $"{Stopwatch.GetElapsedTime( range.Ticks, now ).TotalMilliseconds:F0} ms, {frame - Math.Min( frame, range.Frame )} frames ago";
			lines.Add( $"{range.Type} '{range.Name ?? "unnamed"}'{(range.Internal ? " (the driver's own)" : "")} 0x{range.Base:X}+0x{range.Size:X}, {(unbound ? "taken back" : "bound")} {ago}" );
		}

		if ( found.Count > 6 ) lines.Add( $"and {found.Count - 6} more" );
		return string.Join( "; ", lines );
	}
}
