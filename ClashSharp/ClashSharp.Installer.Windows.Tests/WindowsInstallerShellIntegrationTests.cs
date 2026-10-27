using System.Reflection;
using System.Runtime.InteropServices;
using ClashSharp.Installer.Windows.Execution;

namespace ClashSharp.Installer.Windows.Tests;

public sealed class WindowsInstallerShellIntegrationTests
{
    [Fact]
    public void PropertyVariantReservesTheCompleteNativeX64Union()
    {
        Assert.Equal(8, IntPtr.Size);
        Type variant = typeof(WindowsInstallerShellIntegration).GetNestedType("PropertyVariant", BindingFlags.NonPublic)!;
        Assert.Equal(24, Marshal.SizeOf(variant));
        Assert.Equal(0, Marshal.OffsetOf(variant, "Type").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf(variant, "Pointer").ToInt32());
    }
}
